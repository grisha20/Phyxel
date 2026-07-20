using System;
using System.Buffers.Binary;
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

    private const uint WorldFileMagic = 0x5058594C;
    private const int LegacyWorldHeaderSize = 20;
    private const int CurrentWorldHeaderSize = 24;
    private const int CurrentVersion = 6;
    private const string RemovedGoldSandId = "core:gold_sand";
    private const string RenamedConcreteId = "core:concrete";
    private const string RenamedGasId = "core:gas";
    private readonly JsonSerializerOptions options = new() { WriteIndented = true };
    private bool capturePending;
    private SimulationWorldSnapshot? emptySnapshot;

    public SimulationStateSerializer()
    {
        WorldCellCodec.ValidateLayoutContracts();
    }

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

        RawWorldFile? rawWorld = await ReadWorldAsync(
            Path.ChangeExtension(path, ".world"),
            cancellationToken);
        if (rawWorld is not null && rawWorld.Version != version)
        {
            throw new InvalidDataException(
                $"Версии scene.json ({version}) и .world ({rawWorld.Version}) не совпадают.");
        }
        SimulationWorldSnapshot? world = rawWorld is null ? null : WorldCellCodec.Decode(rawWorld);

        List<string> warnings = [];
        return version switch
        {
            3 => LegacySceneV3Loader.Load(
                sceneJson,
                world,
                materialRegistry,
                warnings,
                options),
            4 => LoadPaletteScene(sceneJson, world, materialRegistry, warnings, true),
            5 or CurrentVersion => LoadPaletteScene(sceneJson, world, materialRegistry, warnings, false),
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

    public static bool ContainsContactTransitionSource(
        SimulationWorldSnapshot world,
        MaterialRegistry materialRegistry)
    {
        ReadOnlySpan<GridCell> grid = MemoryMarshal.Cast<byte, GridCell>(world.Grid);
        foreach (GridCell cell in grid)
        {
            if (cell.IsActive != 0 && cell.MaterialIndex < materialRegistry.Count &&
                materialRegistry[cell.MaterialIndex].LiquidContactTransition is not null)
            {
                return true;
            }
        }
        return false;
    }

    private LoadedSimulationScene LoadPaletteScene(
        byte[] sceneJson,
        SimulationWorldSnapshot? world,
        MaterialRegistry materialRegistry,
        List<string> warnings,
        bool initializeLegacyTemperature)
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

        string selectedMaterialId = MigrateV4MaterialId(
            MaterialRegistry.NormalizeId(state.SelectedMaterialId),
            warnings);
        ushort selectedMaterial;
        if (!materialRegistry.TryGet(selectedMaterialId, out MaterialDefinition selectedDefinition) ||
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
            RemapSnapshotToRuntime(
                world,
                state.MaterialPalette,
                materialRegistry,
                warnings,
                initializeLegacyTemperature);
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
        List<string> warnings,
        bool initializeLegacyTemperature)
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
            string storedId = MaterialRegistry.NormalizeId(scenePalette[sceneIndex]);
            if (!ids.Add(storedId))
            {
                throw new InvalidDataException($"Палитра сцены содержит дублирующий ID '{storedId}'.");
            }
            string id = MigrateV4MaterialId(storedId, warnings);
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
            uint runtimeIndex = sceneToRuntime[sceneIndex];
            cells[index].MaterialIndex = runtimeIndex;
            if (initializeLegacyTemperature)
            {
                cells[index].Temperature = materialRegistry[runtimeIndex].Properties.InitialTemperature;
            }
        }
    }

    private static string MigrateV4MaterialId(string id, List<string> warnings)
    {
        string replacementId;
        string warning;
        if (id == RemovedGoldSandId)
        {
            replacementId = CoreMaterialIds.Sand;
            warning = "Материал 'core:gold_sand' удалён и мигрирован в 'core:sand'.";
        }
        else if (id == RenamedConcreteId)
        {
            replacementId = CoreMaterialIds.Stone;
            warning = "Материал 'core:concrete' переименован и мигрирован в 'core:stone'.";
        }
        else if (id == RenamedGasId)
        {
            replacementId = CoreMaterialIds.Co2;
            warning = "Материал 'core:gas' переименован и мигрирован в 'core:co2'.";
        }
        else
        {
            return id;
        }

        if (!warnings.Contains(warning))
        {
            AddWarning(warnings, warning);
        }
        return replacementId;
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
        if (version != CurrentVersion)
        {
            throw new InvalidDataException($"Запись world v{version} не поддерживается.");
        }
        ValidateSnapshotSize(world);
        byte[] header = new byte[CurrentWorldHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), WorldFileMagic);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), version);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), world.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), world.Height);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), WorldCellCodec.CurrentCellStride);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20, 4), world.Grid.Length);
        await using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(world.Grid, cancellationToken);
    }

    internal static async Task<RawWorldFile?> ReadWorldAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
        byte[] prefix = new byte[8];
        try
        {
            await stream.ReadExactlyAsync(prefix, cancellationToken);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("Заголовок файла мира обрезан.", exception);
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(0, 4));
        int version = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(4, 4));
        if (magic != WorldFileMagic || version is not (3 or 4 or 5 or CurrentVersion))
        {
            throw new InvalidDataException("Формат снимка мира не поддерживается.");
        }

        bool extendedHeader = version >= 5;
        int headerSize = extendedHeader ? CurrentWorldHeaderSize : LegacyWorldHeaderSize;
        byte[] remainder = new byte[headerSize - prefix.Length];
        try
        {
            await stream.ReadExactlyAsync(remainder, cancellationToken);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("Заголовок файла мира обрезан.", exception);
        }

        int width = BinaryPrimitives.ReadInt32LittleEndian(remainder.AsSpan(0, 4));
        int height = BinaryPrimitives.ReadInt32LittleEndian(remainder.AsSpan(4, 4));
        int storedCellStride = extendedHeader
            ? BinaryPrimitives.ReadInt32LittleEndian(remainder.AsSpan(8, 4))
            : WorldCellCodec.LegacyCellStride;
        int length = BinaryPrimitives.ReadInt32LittleEndian(
            remainder.AsSpan(extendedHeader ? 12 : 8, 4));
        WorldCellCodec.ValidateStoredWorld(version, width, height, storedCellStride, length);

        long expectedFileLength = checked((long)headerSize + length);
        if (stream.Length < expectedFileLength)
        {
            throw new InvalidDataException("Секция клеток файла мира обрезана.");
        }
        if (stream.Length > expectedFileLength)
        {
            throw new InvalidDataException("После секции клеток файла мира обнаружены лишние байты.");
        }

        byte[] grid = new byte[length];
        try
        {
            await stream.ReadExactlyAsync(grid, cancellationToken);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("Секция клеток файла мира обрезана.", exception);
        }
        return new RawWorldFile(version, width, height, storedCellStride, grid);
    }
}
