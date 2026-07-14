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
    int BrushRadius,
    float SpawnDensity,
    bool SolidGravity,
    MaterialId SelectedMaterial,
    DateTimeOffset SavedAt);

public sealed record SimulationWorldSnapshot(int Width, int Height, byte[] Grid);

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
        MaterialId selectedMaterial,
        SimulationWorldSnapshot world,
        CancellationToken cancellationToken = default)
    {
        SimulationSceneState state = new(
            3,
            settings.Scale,
            settings.Gravity,
            settings.BrushRadius,
            settings.SpawnDensity,
            settings.SolidGravity,
            selectedMaterial,
            DateTimeOffset.UtcNow);
        string directory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(directory);
        await using (FileStream stream = File.Create(path))
        {
            await JsonSerializer.SerializeAsync(stream, state, options, cancellationToken);
        }
        await WriteWorldAsync(Path.ChangeExtension(path, ".world"), world, cancellationToken);
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
        if (state is null || state.Version != 3)
        {
            return null;
        }
        SimulationWorldSnapshot? world = await ReadWorldAsync(
            Path.ChangeExtension(path, ".world"),
            cancellationToken);
        return new LoadedSimulationScene(state, world);
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
        SimulationWorldSnapshot world,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
        await stream.WriteAsync(BitConverter.GetBytes(WorldFileMagic), cancellationToken);
        await stream.WriteAsync(BitConverter.GetBytes(3), cancellationToken);
        await stream.WriteAsync(BitConverter.GetBytes(world.Width), cancellationToken);
        await stream.WriteAsync(BitConverter.GetBytes(world.Height), cancellationToken);
        await stream.WriteAsync(BitConverter.GetBytes(world.Grid.Length), cancellationToken);
        await stream.WriteAsync(world.Grid, cancellationToken);
    }

    private static async Task<SimulationWorldSnapshot?> ReadWorldAsync(
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
        if (magic != WorldFileMagic || version != 3)
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
        return new SimulationWorldSnapshot(width, height, grid);
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
