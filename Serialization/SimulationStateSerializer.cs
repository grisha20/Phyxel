using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.Mathematics.Interop;
using Buffer = SharpDX.Direct3D11.Buffer;

namespace Phyxel.Serialization;

public sealed record SimulationSceneState(
    int Version,
    float Scale,
    float Gravity,
    int BrushRadius,
    float SpawnDensity,
    bool SolidGravity,
    ushort SelectedMaterial,
    DateTimeOffset SavedAt,
    bool HydraulicPressure = false);

public sealed record SimulationWorldSnapshot(int Width, int Height, byte[] Grid);

public sealed record LoadedSimulationScene(
    SimulationSceneState State,
    SimulationWorldSnapshot? World,
    IReadOnlyList<string> Warnings);

public sealed class SimulationStateSerializer
{
    private sealed class SceneFileV4
    {
        public int Version { get; set; }
        public float Scale { get; set; }
        public float Gravity { get; set; }
        public int BrushRadius { get; set; }
        public float SpawnDensity { get; set; }
        public bool SolidGravity { get; set; }
        public string SelectedMaterialId { get; set; } = CoreMaterialIds.Sand;
        public DateTimeOffset SavedAt { get; set; }
        public bool HydraulicPressure { get; set; }
        public string[] MaterialPalette { get; set; } = [];
    }

    private sealed record WorldFileData(int Version, SimulationWorldSnapshot Snapshot);

    private const uint WorldFileMagic = 0x5058594C;
    private const int CurrentVersion = 4;
    private readonly JsonSerializerOptions options = new() { WriteIndented = true };
    private bool capturePending;
    private SimulationWorldSnapshot? emptySnapshot;

    public void BeginWorldCapture(GpuSimulationResources resources)
    {
        if (capturePending)
        {
            return;
        }
        if (!resources.IsSimulationAllocated)
        {
            emptySnapshot = new SimulationWorldSnapshot(resources.Width, resources.Height, []);
            capturePending = true;
            return;
        }
        resources.Context.CopyResource(resources.Grid.ReadBuffer, resources.GridStaging);
        resources.Context.End(resources.SceneTransferQuery);
        resources.Context.Flush();
        capturePending = true;
    }

    public bool TryCompleteWorldCapture(
        GpuSimulationResources resources,
        out SimulationWorldSnapshot? snapshot)
    {
        snapshot = null;
        if (!capturePending)
        {
            return false;
        }
        if (emptySnapshot is not null)
        {
            snapshot = emptySnapshot;
            emptySnapshot = null;
            capturePending = false;
            return true;
        }
        bool ready = resources.Context.GetData(
            resources.SceneTransferQuery,
            AsynchronousFlags.DoNotFlush,
            out RawBool completed);
        if (!ready || !completed)
        {
            return false;
        }
        snapshot = new SimulationWorldSnapshot(
            resources.Width,
            resources.Height,
            ReadBuffer(resources.Context, resources.GridStaging));
        capturePending = false;
        return true;
    }

    public async Task SaveAsync(
        string path,
        SimulationSettings settings,
        ushort selectedMaterial,
        SimulationWorldSnapshot world,
        MaterialRegistry materialRegistry,
        CancellationToken cancellationToken = default)
    {
        if (!materialRegistry.TryGet(selectedMaterial, out MaterialDefinition selectedDefinition))
        {
            throw new InvalidDataException($"Выбранный runtime-индекс материала {selectedMaterial} отсутствует в реестре.");
        }

        (SimulationWorldSnapshot encodedWorld, string[] palette) = EncodeSceneSnapshot(world, materialRegistry);
        SceneFileV4 state = new()
        {
            Version = CurrentVersion,
            Scale = settings.Scale,
            Gravity = settings.Gravity,
            BrushRadius = settings.BrushRadius,
            SpawnDensity = settings.SpawnDensity,
            SolidGravity = settings.SolidGravity,
            SelectedMaterialId = selectedDefinition.Id,
            SavedAt = DateTimeOffset.UtcNow,
            HydraulicPressure = settings.HydraulicPressure,
            MaterialPalette = palette
        };
        string directory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(directory);
        await using (FileStream stream = File.Create(path))
        {
            await JsonSerializer.SerializeAsync(stream, state, options, cancellationToken);
        }
        await WriteWorldAsync(
            Path.ChangeExtension(path, ".world"),
            CurrentVersion,
            encodedWorld,
            cancellationToken);
    }

    public async Task<LoadedSimulationScene?> LoadAsync(
        string path,
        MaterialRegistry materialRegistry,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] sceneJson = await File.ReadAllBytesAsync(path, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(sceneJson);
        if (!document.RootElement.TryGetProperty("Version", out JsonElement versionElement) ||
            !versionElement.TryGetInt32(out int version))
        {
            return null;
        }

        WorldFileData? worldFile = await ReadWorldAsync(
            Path.ChangeExtension(path, ".world"),
            cancellationToken);
        if (worldFile is not null && worldFile.Version != version)
        {
            throw new InvalidDataException(
                $"Версии scene.json ({version}) и .world ({worldFile.Version}) не совпадают.");
        }

        List<string> warnings = [];
        return version switch
        {
            3 => LegacySceneV3Loader.Load(
                sceneJson,
                worldFile?.Snapshot,
                materialRegistry,
                warnings,
                options),
            CurrentVersion => LoadV4(sceneJson, worldFile?.Snapshot, materialRegistry, warnings),
            _ => null
        };
    }

    public void ApplyWorldSnapshot(GpuSimulationResources resources, SimulationWorldSnapshot world)
    {
        if (resources.Width != world.Width || resources.Height != world.Height)
        {
            throw new InvalidDataException("Размер снимка мира не совпадает с размером GPU-ресурсов.");
        }
        if (world.Grid.Length == 0)
        {
            return;
        }
        UploadBuffer(resources.Context, resources.GridStaging, world.Grid, resources.Grid.Buffers);
    }

    public static void Apply(SimulationSceneState state, SimulationSettings settings)
    {
        settings.ApplyScale(state.Scale);
        settings.Gravity = Math.Clamp(state.Gravity, 0, 4000);
        settings.BrushRadius = Math.Clamp(state.BrushRadius, 1, 96);
        settings.SpawnDensity = Math.Clamp(state.SpawnDensity, 0.05f, 1);
        settings.SolidGravity = state.SolidGravity;
        settings.HydraulicPressure = state.HydraulicPressure;
    }

    public static bool ContainsMatter(SimulationWorldSnapshot world)
    {
        ReadOnlySpan<GridCell> grid = MemoryMarshal.Cast<byte, GridCell>(world.Grid);
        foreach (GridCell cell in grid)
        {
            if (cell.IsActive != 0)
            {
                return true;
            }
        }
        return false;
    }

    private LoadedSimulationScene LoadV4(
        byte[] sceneJson,
        SimulationWorldSnapshot? world,
        MaterialRegistry materialRegistry,
        List<string> warnings)
    {
        SceneFileV4 state = JsonSerializer.Deserialize<SceneFileV4>(sceneJson, options) ??
            throw new InvalidDataException("Сцена v4 не содержит состояния.");
        if (state.MaterialPalette.Length is 0 or > MaterialRegistry.MaximumMaterials)
        {
            throw new InvalidDataException("Палитра сцены v4 пуста или превышает допустимый размер.");
        }
        if (MaterialRegistry.NormalizeId(state.MaterialPalette[0]) != CoreMaterialIds.Empty)
        {
            throw new InvalidDataException("Индекс 0 палитры сцены v4 должен быть core:empty.");
        }

        ushort selectedMaterial;
        if (!materialRegistry.TryGet(state.SelectedMaterialId, out MaterialDefinition selectedDefinition) ||
            selectedDefinition.Hidden)
        {
            selectedMaterial = materialRegistry.GetRequiredRuntimeIndex(CoreMaterialIds.Sand);
            AddWarning(warnings, $"Выбранный материал '{state.SelectedMaterialId}' отсутствует; выбран core:sand.");
        }
        else
        {
            selectedMaterial = selectedDefinition.RuntimeIndex;
        }
        if (world is not null)
        {
            RemapSnapshotToRuntime(world, state.MaterialPalette, materialRegistry, warnings);
        }

        return new LoadedSimulationScene(
            new SimulationSceneState(
                state.Version,
                state.Scale,
                state.Gravity,
                state.BrushRadius,
                state.SpawnDensity,
                state.SolidGravity,
                selectedMaterial,
                state.SavedAt,
                state.HydraulicPressure),
            world,
            warnings);
    }

    private static (SimulationWorldSnapshot Snapshot, string[] Palette) EncodeSceneSnapshot(
        SimulationWorldSnapshot world,
        MaterialRegistry materialRegistry)
    {
        ValidateSnapshotSize(world);
        bool[] usedRuntimeIndices = new bool[materialRegistry.Count];
        ushort emptyRuntimeIndex = materialRegistry.GetRequiredRuntimeIndex(CoreMaterialIds.Empty);
        usedRuntimeIndices[emptyRuntimeIndex] = true;
        ReadOnlySpan<GridCell> sourceCells = MemoryMarshal.Cast<byte, GridCell>(world.Grid);
        foreach (GridCell cell in sourceCells)
        {
            if (cell.IsActive == 0)
            {
                continue;
            }
            if (cell.MaterialIndex >= materialRegistry.Count)
            {
                throw new InvalidDataException(
                    $"Снимок содержит неизвестный runtime-индекс материала {cell.MaterialIndex}.");
            }
            usedRuntimeIndices[cell.MaterialIndex] = true;
        }

        ushort[] runtimeToScene = new ushort[materialRegistry.Count];
        Array.Fill(runtimeToScene, ushort.MaxValue);
        List<string> palette = [];
        for (ushort runtimeIndex = 0; runtimeIndex < materialRegistry.Count; runtimeIndex++)
        {
            if (!usedRuntimeIndices[runtimeIndex])
            {
                continue;
            }
            runtimeToScene[runtimeIndex] = checked((ushort)palette.Count);
            palette.Add(materialRegistry[runtimeIndex].Id);
        }

        byte[] encodedGrid = (byte[])world.Grid.Clone();
        Span<GridCell> encodedCells = MemoryMarshal.Cast<byte, GridCell>(encodedGrid);
        for (int index = 0; index < encodedCells.Length; index++)
        {
            if (encodedCells[index].IsActive == 0)
            {
                encodedCells[index] = default;
                continue;
            }
            uint runtimeIndex = encodedCells[index].MaterialIndex;
            encodedCells[index].MaterialIndex = runtimeToScene[runtimeIndex];
        }
        return (new SimulationWorldSnapshot(world.Width, world.Height, encodedGrid), palette.ToArray());
    }

    private static void RemapSnapshotToRuntime(
        SimulationWorldSnapshot world,
        IReadOnlyList<string> scenePalette,
        MaterialRegistry materialRegistry,
        List<string> warnings)
    {
        ValidateSnapshotSize(world);
        if (scenePalette.Count is 0 or > MaterialRegistry.MaximumMaterials)
        {
            throw new InvalidDataException("Палитра сцены пуста или превышает допустимый размер.");
        }

        uint[] sceneToRuntime = new uint[scenePalette.Count];
        bool[] missing = new bool[scenePalette.Count];
        HashSet<string> ids = new(StringComparer.Ordinal);
        for (int sceneIndex = 0; sceneIndex < scenePalette.Count; sceneIndex++)
        {
            string id = MaterialRegistry.NormalizeId(scenePalette[sceneIndex]);
            if (!ids.Add(id))
            {
                throw new InvalidDataException($"Палитра сцены содержит дублирующий ID '{id}'.");
            }
            if (materialRegistry.TryGet(id, out MaterialDefinition definition))
            {
                sceneToRuntime[sceneIndex] = definition.RuntimeIndex;
            }
            else
            {
                missing[sceneIndex] = true;
                sceneToRuntime[sceneIndex] = materialRegistry.GetRequiredRuntimeIndex(CoreMaterialIds.Empty);
                AddWarning(warnings, $"Материал '{id}' отсутствует и заменён на core:empty.");
            }
        }

        Span<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(world.Grid);
        for (int index = 0; index < cells.Length; index++)
        {
            if (cells[index].IsActive == 0)
            {
                cells[index] = default;
                continue;
            }
            uint sceneIndex = cells[index].MaterialIndex;
            if (sceneIndex >= sceneToRuntime.Length || missing[sceneIndex])
            {
                cells[index] = default;
                continue;
            }
            cells[index].MaterialIndex = sceneToRuntime[sceneIndex];
        }
    }

    private static void AddWarning(List<string> warnings, string message)
    {
        warnings.Add(message);
        Console.Error.WriteLine($"PHYXEL_SCENE_WARNING {message}");
    }

    internal static void ValidateSnapshotSize(SimulationWorldSnapshot world)
    {
        int expected = checked(world.Width * world.Height * Marshal.SizeOf<GridCell>());
        if (world.Grid.Length != 0 && world.Grid.Length != expected)
        {
            throw new InvalidDataException("Размер секции снимка мира некорректен.");
        }
    }

    private static byte[] ReadBuffer(DeviceContext context, Buffer buffer)
    {
        int length = buffer.Description.SizeInBytes;
        byte[] bytes = new byte[length];
        DataBox mapping = context.MapSubresource(buffer, 0, MapMode.Read, MapFlags.None);
        Marshal.Copy(mapping.DataPointer, bytes, 0, length);
        context.UnmapSubresource(buffer, 0);
        return bytes;
    }

    private static void UploadBuffer(DeviceContext context, Buffer staging, byte[] bytes, Buffer[] destinations)
    {
        if (bytes.Length != staging.Description.SizeInBytes)
        {
            throw new InvalidDataException("Размер секции снимка мира некорректен.");
        }
        DataBox mapping = context.MapSubresource(staging, 0, MapMode.Write, MapFlags.None);
        Marshal.Copy(bytes, 0, mapping.DataPointer, bytes.Length);
        context.UnmapSubresource(staging, 0);
        foreach (Buffer destination in destinations)
        {
            context.CopyResource(staging, destination);
        }
    }

    private static async Task WriteWorldAsync(
        string path,
        int version,
        SimulationWorldSnapshot world,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
        await stream.WriteAsync(BitConverter.GetBytes(WorldFileMagic), cancellationToken);
        await stream.WriteAsync(BitConverter.GetBytes(version), cancellationToken);
        await stream.WriteAsync(BitConverter.GetBytes(world.Width), cancellationToken);
        await stream.WriteAsync(BitConverter.GetBytes(world.Height), cancellationToken);
        await stream.WriteAsync(BitConverter.GetBytes(world.Grid.Length), cancellationToken);
        await stream.WriteAsync(world.Grid, cancellationToken);
    }

    private static async Task<WorldFileData?> ReadWorldAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
        uint magic = await ReadUInt32Async(stream, cancellationToken);
        int version = await ReadInt32Async(stream, cancellationToken);
        if (magic != WorldFileMagic || version is not (3 or CurrentVersion))
        {
            throw new InvalidDataException("Формат снимка мира не поддерживается.");
        }
        int width = await ReadInt32Async(stream, cancellationToken);
        int height = await ReadInt32Async(stream, cancellationToken);
        int length = await ReadInt32Async(stream, cancellationToken);
        int expected = checked(width * height * Marshal.SizeOf<GridCell>());
        if (length != 0 && length != expected)
        {
            throw new InvalidDataException("Размер секции снимка мира некорректен.");
        }
        byte[] grid = new byte[length];
        await stream.ReadExactlyAsync(grid, cancellationToken);
        return new WorldFileData(version, new SimulationWorldSnapshot(width, height, grid));
    }

    private static async Task<int> ReadInt32Async(FileStream stream, CancellationToken cancellationToken)
    {
        byte[] bytes = new byte[4];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return BitConverter.ToInt32(bytes);
    }

    private static async Task<uint> ReadUInt32Async(FileStream stream, CancellationToken cancellationToken)
    {
        byte[] bytes = new byte[4];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return BitConverter.ToUInt32(bytes);
    }
}
