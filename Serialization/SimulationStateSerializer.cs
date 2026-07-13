using System;
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
    int SolverIterations,
    int BrushRadius,
    float SpawnDensity,
    bool StressView,
    MaterialId SelectedMaterial,
    DateTimeOffset SavedAt);

public sealed record SimulationWorldSnapshot(
    int Width,
    int Height,
    byte[] Grid,
    byte[] Particles,
    byte[] Bonds);

public sealed record LoadedSimulationScene(
    SimulationSceneState State,
    SimulationWorldSnapshot? World);

public sealed class SimulationStateSerializer
{
    private const uint WorldFileMagic = 0x5058594C;
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
            emptySnapshot = new SimulationWorldSnapshot(resources.Width, resources.Height, [], [], []);
            capturePending = true;
            return;
        }

        resources.Context.CopyResource(resources.Grid.ReadBuffer, resources.GridStaging);
        resources.Context.CopyResource(resources.Particles.ReadBuffer, resources.ParticlesStaging);
        resources.Context.CopyResource(resources.Bonds.ReadBuffer, resources.BondsStaging);
        resources.Context.End(resources.SceneTransferQuery);
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
            ReadBuffer(resources.Context, resources.GridStaging),
            ReadBuffer(resources.Context, resources.ParticlesStaging),
            ReadBuffer(resources.Context, resources.BondsStaging));
        capturePending = false;
        return true;
    }

    public async Task SaveAsync(
        string path,
        SimulationSettings settings,
        MaterialId selectedMaterial,
        SimulationWorldSnapshot world,
        CancellationToken cancellationToken = default)
    {
        SimulationSceneState state = new(
            2,
            settings.Scale,
            settings.Gravity,
            settings.SolverIterations,
            settings.BrushRadius,
            settings.SpawnDensity,
            settings.StressView,
            selectedMaterial,
            DateTimeOffset.UtcNow);
        string directory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(directory);
        await using (FileStream stream = File.Create(path))
        {
            await JsonSerializer.SerializeAsync(stream, state, options, cancellationToken);
        }

        await WriteWorldAsync(CreateWorldPath(path), world, cancellationToken);
    }

    public async Task<LoadedSimulationScene?> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        SimulationSceneState? state;
        await using (FileStream stream = File.OpenRead(path))
        {
            state = await JsonSerializer.DeserializeAsync<SimulationSceneState>(stream, options, cancellationToken);
        }

        if (state is null)
        {
            return null;
        }

        SimulationWorldSnapshot? world = await ReadWorldAsync(CreateWorldPath(path), cancellationToken);
        return new LoadedSimulationScene(state, world);
    }

    public void ApplyWorldSnapshot(GpuSimulationResources resources, SimulationWorldSnapshot world)
    {
        if (resources.Width != world.Width || resources.Height != world.Height)
        {
            throw new InvalidDataException("Размер снимка мира не совпадает с размером GPU-ресурсов.");
        }

        if (world.Grid.Length == 0 && world.Particles.Length == 0 && world.Bonds.Length == 0)
        {
            return;
        }

        UploadBuffer(resources.Context, resources.GridStaging, world.Grid, resources.Grid.Buffers);
        UploadBuffer(resources.Context, resources.ParticlesStaging, world.Particles, resources.Particles.Buffers);
        UploadBuffer(resources.Context, resources.BondsStaging, world.Bonds, resources.Bonds.Buffers);
    }

    public static void Apply(SimulationSceneState state, SimulationSettings settings)
    {
        settings.ApplyScale(state.Scale);
        settings.Gravity = Math.Clamp(state.Gravity, 0f, 4000f);
        settings.SolverIterations = Math.Clamp(state.SolverIterations, 1, 8);
        settings.BrushRadius = Math.Clamp(state.BrushRadius, 1, 96);
        settings.SpawnDensity = Math.Clamp(state.SpawnDensity, 0.05f, 1f);
        settings.StressView = state.StressView;
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
            throw new InvalidDataException("Размер секции снимка мира повреждён.");
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
        SimulationWorldSnapshot world,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
        await WriteUInt32Async(stream, WorldFileMagic, cancellationToken);
        await WriteInt32Async(stream, 2, cancellationToken);
        await WriteInt32Async(stream, world.Width, cancellationToken);
        await WriteInt32Async(stream, world.Height, cancellationToken);
        await WriteSectionAsync(stream, world.Grid, cancellationToken);
        await WriteSectionAsync(stream, world.Particles, cancellationToken);
        await WriteSectionAsync(stream, world.Bonds, cancellationToken);
    }

    private static async Task<SimulationWorldSnapshot?> ReadWorldAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
        uint magic = await ReadUInt32Async(stream, cancellationToken);
        int version = await ReadInt32Async(stream, cancellationToken);
        if (magic != WorldFileMagic || version != 2)
        {
            throw new InvalidDataException("Формат снимка мира не поддерживается.");
        }

        int width = await ReadInt32Async(stream, cancellationToken);
        int height = await ReadInt32Async(stream, cancellationToken);
        int cellCount = checked(width * height);
        byte[] grid = await ReadSectionAsync(stream, checked(cellCount * Marshal.SizeOf<GridCell>()), cancellationToken);
        byte[] particles = await ReadSectionAsync(stream, checked(cellCount * Marshal.SizeOf<LatticeParticle>()), cancellationToken);
        byte[] bonds = await ReadSectionAsync(stream, checked(cellCount * Marshal.SizeOf<LatticeBond>()), cancellationToken);
        bool emptyWorld = grid.Length == 0 && particles.Length == 0 && bonds.Length == 0;
        bool completeWorld = grid.Length > 0 && particles.Length > 0 && bonds.Length > 0;
        if (!emptyWorld && !completeWorld)
        {
            throw new InvalidDataException("Секции снимка мира имеют несовместимые размеры.");
        }

        return new SimulationWorldSnapshot(width, height, grid, particles, bonds);
    }

    private static async Task WriteSectionAsync(FileStream stream, byte[] bytes, CancellationToken cancellationToken)
    {
        await WriteInt32Async(stream, bytes.Length, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static async Task<byte[]> ReadSectionAsync(
        FileStream stream,
        int expectedLength,
        CancellationToken cancellationToken)
    {
        int length = await ReadInt32Async(stream, cancellationToken);
        if ((length != expectedLength && length != 0) || length < 0)
        {
            throw new InvalidDataException("Размер секции снимка мира некорректен.");
        }

        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return bytes;
    }

    private static Task WriteInt32Async(FileStream stream, int value, CancellationToken cancellationToken)
    {
        return stream.WriteAsync(BitConverter.GetBytes(value), cancellationToken).AsTask();
    }

    private static Task WriteUInt32Async(FileStream stream, uint value, CancellationToken cancellationToken)
    {
        return stream.WriteAsync(BitConverter.GetBytes(value), cancellationToken).AsTask();
    }

    private static async Task<int> ReadInt32Async(FileStream stream, CancellationToken cancellationToken)
    {
        byte[] bytes = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return BitConverter.ToInt32(bytes);
    }

    private static async Task<uint> ReadUInt32Async(FileStream stream, CancellationToken cancellationToken)
    {
        byte[] bytes = new byte[sizeof(uint)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return BitConverter.ToUInt32(bytes);
    }

    private static string CreateWorldPath(string metadataPath)
    {
        return Path.ChangeExtension(metadataPath, ".world");
    }
}
