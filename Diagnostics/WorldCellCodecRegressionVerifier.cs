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
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class WorldCellCodecRegressionVerifier
{
    private const uint WorldFileMagic = 0x5058594C;
    private const int LegacyHeaderSize = 20;
    private const int CurrentHeaderSize = 24;

    public static int Run()
    {
        try
        {
            RunAsync().GetAwaiter().GetResult();
            Console.WriteLine("PHYXEL_WORLD_CODEC_SUCCESS");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"PHYXEL_WORLD_CODEC_FAILED {exception}");
            return 1;
        }
    }

    private static async Task RunAsync()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"phyxel-world-codec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string externalMaterials = Path.Combine(directory, "external-materials");
            Directory.CreateDirectory(externalMaterials);
            MaterialRegistry materials = new(externalMaterials);
            SimulationStateSerializer serializer = new();

            VerifyLayoutContracts();
            VerifyGasThermalMixingContract();
            VerifyGasSchedulerContract();
            await VerifyV3Async(directory, serializer, materials);
            await VerifyV4MigrationsAsync(directory, serializer, materials);
            await VerifyV5RoundTripAsync(directory, serializer, materials);
            VerifyV5Migration();
            await VerifyV5RuntimeRemapAsync(directory);
            await VerifyCorruptWorldsAsync(directory);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void VerifyLayoutContracts()
    {
        WorldCellCodec.ValidateLayoutContracts();
        Require(
            Marshal.SizeOf<LegacyGridCellV3V4>() == WorldCellCodec.LegacyCellStride,
            "LegacyGridCellV3V4 must remain 32 bytes.");
        Require(
            Marshal.SizeOf<LegacyGridCellV5>() == WorldCellCodec.V5CellStride,
            "LegacyGridCellV5 must remain 36 bytes.");
        Require(
            Marshal.SizeOf<GridCell>() == WorldCellCodec.CurrentCellStride,
            "GridCell must be 40 bytes.");
        string shaderPath = Path.Combine(AppContext.BaseDirectory, "Content", "Shaders", "PhysicsShared.hlsli");
        string shader = File.ReadAllText(shaderPath);
        int layoutStart = shader.IndexOf("struct GridCell", StringComparison.Ordinal);
        int layoutEnd = shader.IndexOf("};", layoutStart, StringComparison.Ordinal);
        Require(layoutStart >= 0 && layoutEnd > layoutStart, "HLSL GridCell declaration is missing.");
        string layout = shader[layoutStart..layoutEnd];
        string[] fields =
        [
            "uint MaterialIndex;", "float Mass;", "float VelocityX;", "float VelocityY;",
            "float Pressure;", "uint IsActive;", "uint BodyId;", "uint RestFrames;",
            "float Temperature;", "float Lifetime;"
        ];
        int previous = -1;
        foreach (string field in fields)
        {
            int position = layout.IndexOf(field, StringComparison.Ordinal);
            Require(position > previous, $"HLSL GridCell field '{field}' is missing or out of order.");
            previous = position;
        }
    }

    private static void VerifyGasThermalMixingContract()
    {
        string shaderPath = Path.Combine(
            AppContext.BaseDirectory,
            "Content",
            "Shaders",
            "GasRedistribution.hlsl");
        string shader = File.ReadAllText(shaderPath);
        Require(
            shader.Contains("void ResolvePacketPair(", StringComparison.Ordinal) &&
            shader.Contains("void MovePacket(", StringComparison.Ordinal),
            "Local gas packet movement contract is missing.");
        Require(
            !shader.Contains("first.MaterialIndex != second.MaterialIndex", StringComparison.Ordinal) &&
            shader.Contains("first.MaterialIndex == second.MaterialIndex", StringComparison.Ordinal) &&
            shader.Contains("firstDensity > secondDensity", StringComparison.Ordinal),
            "Different gases are not kept distinct and sorted by density.");
        Require(
            shader.Contains("StorePair(emptyIndex, gas, gasIndex, empty)", StringComparison.Ordinal) &&
            shader.Contains("StorePair(gasIndex, empty, emptyIndex, gas)", StringComparison.Ordinal),
            "Gas movement does not transfer the intact GridCell packet.");
        Require(
            shader.Contains("(material.Flags & MaterialFlagFlame) == 0", StringComparison.Ordinal),
            "Flame exclusion is missing from ordinary gas movement.");
        Require(
            !shader.Contains("GasMinimumRepresentableMass", StringComparison.Ordinal) &&
            !shader.Contains("mixedTemperature", StringComparison.Ordinal) &&
            !shader.Contains("HorizontalPathAllowsGas", StringComparison.Ordinal),
            "Legacy mass splitting, temperature mixing or long-range movement remains enabled.");
    }

    private static void VerifyGasSchedulerContract()
    {
        foreach ((int framesPerSecond, int frames) in new[]
        {
            (30, 30), (60, 60), (100, 100), (144, 144)
        })
        {
            FixedStepGasScheduler scheduler = new();
            int dispatches = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                dispatches += scheduler.Advance(1d / framesPerSecond, false, true);
            }
            Require(scheduler.TotalTicks == 60 && dispatches == 60,
                $"Gas fixed-step schedule differs at {framesPerSecond} FPS: " +
                $"ticks/dispatches={scheduler.TotalTicks}/{dispatches}.");
        }

        FixedStepGasScheduler paused = new();
        Require(paused.Advance(0.01, false, true) == 0,
            "Gas scheduler unexpectedly ticked before one fixed step.");
        for (int frame = 0; frame < 120; frame++)
        {
            Require(paused.Advance(1d / 30d, true, true) == 0,
                "Gas scheduler advanced while paused.");
        }
        Require(paused.Advance(0.007, false, true) == 1 && paused.TotalTicks == 1,
            "Gas scheduler accumulated paused time or lost its pre-pause fraction.");
        paused.Reset();
        Require(paused.TotalTicks == 0,
            "Gas scheduler Reset did not clear total ticks.");
    }

    private static async Task VerifyV3Async(
        string directory,
        SimulationStateSerializer serializer,
        MaterialRegistry materials)
    {
        string scenePath = Path.Combine(directory, "legacy-v3.json");
        await WriteJsonAsync(scenePath, new
        {
            Version = 3,
            Scale = 0.25f,
            Gravity = 980f,
            BrushRadius = 18,
            SpawnDensity = 0.82f,
            SolidGravity = true,
            SelectedMaterial = 4u,
            SavedAt = DateTimeOffset.UnixEpoch,
            HydraulicPressure = false
        });
        LegacyGridCellV3V4 stone = CreateLegacyCell(4, 9.2f, -3.5f, 7.25f, 2.75f, 1, 41, 12);
        LegacyGridCellV3V4 dirtyInactive = CreateLegacyCell(2, 4, 1, 2, 3, 0, 99, 8);
        await WriteLegacyWorldAsync(
            Path.ChangeExtension(scenePath, ".world"),
            3,
            2,
            1,
            EncodeLegacyCells(stone, dirtyInactive));

        LoadedSimulationScene loaded = await serializer.LoadAsync(scenePath, materials) ??
            throw new InvalidOperationException("Synthetic v3 scene did not load.");
        uint stoneIndex = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Stone);
        Require(loaded.State.SelectedMaterial == stoneIndex, "v3 selected material index 4 did not map to core:stone.");
        AssertCells(
            loaded.World,
            CreateCurrentCell(stone, stoneIndex, 20f),
            default);
    }

    private static async Task VerifyV4MigrationsAsync(
        string directory,
        SimulationStateSerializer serializer,
        MaterialRegistry materials)
    {
        string scenePath = Path.Combine(directory, "migrations-v4.json");
        await WriteJsonAsync(scenePath, new
        {
            Version = 4,
            Scale = 0.25f,
            Gravity = 980f,
            BrushRadius = 18,
            SpawnDensity = 0.82f,
            SolidGravity = false,
            SelectedMaterialId = "core:concrete",
            SavedAt = DateTimeOffset.UnixEpoch,
            HydraulicPressure = false,
            MaterialPalette = new[] { "core:empty", "core:gold_sand", "core:concrete" }
        });
        LegacyGridCellV3V4 goldSand = CreateLegacyCell(1, 1.5f, 4.25f, -8.5f, 0, 1, 0, 3);
        LegacyGridCellV3V4 concrete = CreateLegacyCell(2, 9.2f, 0.5f, 1.5f, 6.5f, 1, 52, 14);
        LegacyGridCellV3V4 dirtyInactive = CreateLegacyCell(1, 2, 3, 4, 5, 0, 6, 7);
        await WriteLegacyWorldAsync(
            Path.ChangeExtension(scenePath, ".world"),
            4,
            3,
            1,
            EncodeLegacyCells(goldSand, concrete, dirtyInactive));

        LoadedSimulationScene loaded = await serializer.LoadAsync(scenePath, materials) ??
            throw new InvalidOperationException("Synthetic v4 scene did not load.");
        uint sandIndex = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Sand);
        uint stoneIndex = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Stone);
        Require(loaded.State.SelectedMaterial == stoneIndex, "core:concrete selected material did not migrate to core:stone.");
        Require(ContainsWarning(loaded.Warnings, "core:gold_sand"), "core:gold_sand migration warning is missing.");
        Require(ContainsWarning(loaded.Warnings, "core:concrete"), "core:concrete migration warning is missing.");
        AssertCells(
            loaded.World,
            CreateCurrentCell(goldSand, sandIndex, 20f),
            CreateCurrentCell(concrete, stoneIndex, 20f),
            default);
    }

    private static async Task VerifyV5RoundTripAsync(
        string directory,
        SimulationStateSerializer serializer,
        MaterialRegistry materials)
    {
        uint sandIndex = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Sand);
        uint stoneIndex = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Stone);
        GridCell sand = new()
        {
            MaterialIndex = sandIndex,
            Mass = 1.5f,
            VelocityX = -12.25f,
            VelocityY = 28.5f,
            Pressure = 0.75f,
            IsActive = 1,
            BodyId = 0,
            RestFrames = 11,
            Temperature = -120.5f,
            Lifetime = 0.75f
        };
        GridCell stone = new()
        {
            MaterialIndex = stoneIndex,
            Mass = 9.2f,
            VelocityX = 2.5f,
            VelocityY = 3.75f,
            Pressure = 1.25f,
            IsActive = 1,
            BodyId = 701,
            RestFrames = 19,
            Temperature = 1450.25f,
            Lifetime = 2.25f
        };
        GridCell dirtyInactive = new()
        {
            MaterialIndex = uint.MaxValue,
            Mass = 9,
            VelocityX = 8,
            VelocityY = 7,
            Pressure = 6,
            IsActive = 0,
            BodyId = 5,
            RestFrames = 4,
            Temperature = float.NaN
        };
        SimulationWorldSnapshot source = CreateSnapshot(3, 1, sand, stone, dirtyInactive);
        string scenePath = Path.Combine(directory, "roundtrip-v5.json");
        SimulationSettings settings = new();
        await serializer.SaveAsync(
            scenePath,
            settings,
            checked((ushort)sandIndex),
            source,
            materials);

        string worldPath = Path.ChangeExtension(scenePath, ".world");
        RawWorldFile raw = await SimulationStateSerializer.ReadWorldAsync(worldPath, CancellationToken.None) ??
            throw new InvalidOperationException("Saved v5 world file is missing.");
        Require(raw.Version == 6, "CurrentVersion is not 6.");
        Require(raw.StoredCellStride == 40, "v6 did not store the explicit 40-byte stride.");
        Require(new FileInfo(worldPath).Length == CurrentHeaderSize + raw.CellBytes.Length,
            "v6 world header is not 24 bytes.");

        LoadedSimulationScene loaded = await serializer.LoadAsync(scenePath, materials) ??
            throw new InvalidOperationException("Saved v5 scene did not reload.");
        AssertCells(loaded.World, sand, stone, default);
    }

    private static void VerifyV5Migration()
    {
        LegacyGridCellV5 legacy = new()
        {
            MaterialIndex = 4,
            Mass = 0.04f,
            VelocityY = -8,
            IsActive = 1,
            RestFrames = 2,
            Temperature = 650
        };
        SimulationWorldSnapshot migrated = WorldCellCodec.Decode(
            new RawWorldFile(5, 1, 1, WorldCellCodec.V5CellStride, EncodeV5Cells(legacy)));
        ReadOnlySpan<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(migrated.Grid);
        Require(cells.Length == 1 && cells[0].MaterialIndex == legacy.MaterialIndex &&
            SameFloat(cells[0].Temperature, legacy.Temperature) && cells[0].Lifetime == 0,
            "v5 world did not migrate to v6 GridCell with zero transient lifetime.");
    }

    private static async Task VerifyV5RuntimeRemapAsync(string directory)
    {
        string firstMaterials = Path.Combine(directory, "runtime-order-a");
        string secondMaterials = Path.Combine(directory, "runtime-order-b");
        Directory.CreateDirectory(firstMaterials);
        Directory.CreateDirectory(secondMaterials);
        string zMaterial = CreateExternalMaterialJson("test:z_material");
        await File.WriteAllTextAsync(Path.Combine(firstMaterials, "z.json"), zMaterial);
        await File.WriteAllTextAsync(Path.Combine(secondMaterials, "a.json"), CreateExternalMaterialJson("test:a_material"));
        await File.WriteAllTextAsync(Path.Combine(secondMaterials, "z.json"), zMaterial);

        MaterialRegistry firstRegistry = new(firstMaterials);
        MaterialRegistry secondRegistry = new(secondMaterials);
        ushort firstIndex = firstRegistry["test:z_material"].RuntimeIndex;
        ushort secondIndex = secondRegistry["test:z_material"].RuntimeIndex;
        Require(firstIndex != secondIndex, "Runtime-order test did not move the material index.");
        GridCell sourceCell = new()
        {
            MaterialIndex = firstIndex,
            Mass = 2.5f,
            VelocityX = 3,
            VelocityY = 4,
            IsActive = 1,
            RestFrames = 5,
            Temperature = 777.25f
        };
        string scenePath = Path.Combine(directory, "runtime-remap-v5.json");
        SimulationStateSerializer serializer = new();
        await serializer.SaveAsync(
            scenePath,
            new SimulationSettings(),
            firstIndex,
            CreateSnapshot(1, 1, sourceCell),
            firstRegistry);

        LoadedSimulationScene loaded = await serializer.LoadAsync(scenePath, secondRegistry) ??
            throw new InvalidOperationException("Runtime-remap v5 scene did not load.");
        sourceCell.MaterialIndex = secondIndex;
        AssertCells(loaded.World, sourceCell);
    }

    private static async Task VerifyCorruptWorldsAsync(string directory)
    {
        byte[] oneCell = EncodeLegacyCells(default(LegacyGridCellV3V4));
        await ExpectInvalidWorldAsync(directory, "zero-width", 4, 0, 1, 0, []);
        await ExpectInvalidWorldAsync(directory, "zero-height", 4, 1, 0, 0, []);
        await ExpectInvalidWorldAsync(directory, "overflow", 4, int.MaxValue, int.MaxValue, 0, []);
        await ExpectInvalidWorldAsync(directory, "negative-length", 4, 1, 1, -1, []);
        await ExpectInvalidWorldAsync(directory, "wrong-length", 4, 1, 1, 31, new byte[31]);
        await ExpectInvalidWorldAsync(directory, "truncated-cells", 4, 1, 1, 32, new byte[31]);
        await ExpectInvalidWorldAsync(directory, "trailing-bytes", 4, 1, 1, 32, [.. oneCell, 0x7f]);

        string truncatedHeader = Path.Combine(directory, "truncated-header.world");
        await File.WriteAllBytesAsync(truncatedHeader, new byte[LegacyHeaderSize - 1]);
        await ExpectInvalidAsync(() => SimulationStateSerializer.ReadWorldAsync(truncatedHeader, CancellationToken.None));

        string truncatedV5Header = Path.Combine(directory, "truncated-v5-header.world");
        byte[] v5Header = new byte[CurrentHeaderSize - 1];
        BinaryPrimitives.WriteUInt32LittleEndian(v5Header.AsSpan(0, 4), WorldFileMagic);
        BinaryPrimitives.WriteInt32LittleEndian(v5Header.AsSpan(4, 4), 5);
        await File.WriteAllBytesAsync(truncatedV5Header, v5Header);
        await ExpectInvalidAsync(() =>
            SimulationStateSerializer.ReadWorldAsync(truncatedV5Header, CancellationToken.None));

        ExpectInvalid(() => WorldCellCodec.Decode(new RawWorldFile(4, 1, 1, 31, oneCell)));
        ExpectInvalid(() => WorldCellCodec.Decode(new RawWorldFile(4, 1, 1, 36, oneCell)));

        GridCell valid = new() { MaterialIndex = 0, Mass = 1, IsActive = 1, Temperature = 20 };
        byte[] currentCell = EncodeCurrentCells(valid);
        await ExpectInvalidCurrentWorldAsync(directory, "v6-wrong-stride", 39, 40, currentCell);
        await ExpectInvalidCurrentWorldAsync(directory, "v6-truncated", 40, 40, currentCell[..^1]);
        await ExpectInvalidCurrentWorldAsync(directory, "v6-trailing", 40, 40, [.. currentCell, 0x7f]);
        await ExpectInvalidTemperatureAsync(directory, "v6-nan", float.NaN);
        await ExpectInvalidTemperatureAsync(directory, "v6-infinity", float.PositiveInfinity);
        await ExpectInvalidTemperatureAsync(directory, "v6-too-cold", -273.16f);
        await ExpectInvalidTemperatureAsync(directory, "v6-too-hot", 5000.01f);
        WorldCellCodec.Decode(new RawWorldFile(
            6,
            2,
            1,
            WorldCellCodec.CurrentCellStride,
            EncodeCurrentCells(
                new GridCell { IsActive = 1, Temperature = -273.15f },
                new GridCell { IsActive = 1, Temperature = 5000f })));
        Require(
            WorldCellCodec.Decode(new RawWorldFile(6, 1, 1, 40, [])).Grid.Length == 0,
            "v6 zero-length unallocated world was rejected.");

        GridCell dirtyInactive = new()
        {
            MaterialIndex = uint.MaxValue,
            Mass = 9,
            VelocityX = 8,
            VelocityY = 7,
            Pressure = 6,
            IsActive = 0,
            BodyId = 5,
            RestFrames = 4,
            Temperature = float.NaN
        };
        SimulationWorldSnapshot normalized = WorldCellCodec.Decode(
            new RawWorldFile(6, 1, 1, 40, EncodeCurrentCells(dirtyInactive)));
        AssertCells(normalized, default(GridCell));
    }

    private static async Task ExpectInvalidWorldAsync(
        string directory,
        string name,
        int version,
        int width,
        int height,
        int declaredLength,
        byte[] actualBytes)
    {
        string path = Path.Combine(directory, $"{name}.world");
        await WriteRawWorldAsync(path, version, width, height, declaredLength, actualBytes);
        await ExpectInvalidAsync(() => SimulationStateSerializer.ReadWorldAsync(path, CancellationToken.None));
    }

    private static async Task ExpectInvalidCurrentWorldAsync(
        string directory,
        string name,
        int stride,
        int declaredLength,
        byte[] actualBytes)
    {
        string path = Path.Combine(directory, $"{name}.world");
        await WriteCurrentWorldAsync(path, 1, 1, stride, declaredLength, actualBytes);
        await ExpectInvalidAsync(() => SimulationStateSerializer.ReadWorldAsync(path, CancellationToken.None));
    }

    private static async Task ExpectInvalidTemperatureAsync(
        string directory,
        string name,
        float temperature)
    {
        GridCell cell = new() { MaterialIndex = 0, Mass = 1, IsActive = 1, Temperature = temperature };
        byte[] cells = EncodeCurrentCells(cell);
        string path = Path.Combine(directory, $"{name}.world");
        await WriteCurrentWorldAsync(path, 1, 1, WorldCellCodec.CurrentCellStride, cells.Length, cells);
        await ExpectInvalidAsync(async () =>
        {
            RawWorldFile raw = await SimulationStateSerializer.ReadWorldAsync(path, CancellationToken.None) ??
                throw new InvalidOperationException("Invalid temperature world is missing.");
            WorldCellCodec.Decode(raw);
            return raw;
        });
    }

    private static async Task ExpectInvalidAsync(Func<Task<RawWorldFile?>> action)
    {
        try
        {
            await action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException("Corrupt world file was accepted.");
    }

    private static void ExpectInvalid(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException("Unsupported world cell stride was accepted.");
    }

    private static async Task WriteLegacyWorldAsync(
        string path,
        int version,
        int width,
        int height,
        byte[] cells)
    {
        await WriteRawWorldAsync(path, version, width, height, cells.Length, cells);
    }

    private static async Task WriteRawWorldAsync(
        string path,
        int version,
        int width,
        int height,
        int declaredLength,
        byte[] actualBytes)
    {
        byte[] header = new byte[LegacyHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), WorldFileMagic);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), version);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), height);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), declaredLength);
        await using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(header);
        await stream.WriteAsync(actualBytes);
    }

    private static async Task WriteCurrentWorldAsync(
        string path,
        int width,
        int height,
        int stride,
        int declaredLength,
        byte[] actualBytes)
    {
        byte[] header = new byte[CurrentHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), WorldFileMagic);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), 6);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), height);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), stride);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20, 4), declaredLength);
        await using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(header);
        await stream.WriteAsync(actualBytes);
    }

    private static byte[] EncodeLegacyCells(params LegacyGridCellV3V4[] cells)
    {
        byte[] bytes = new byte[checked(cells.Length * WorldCellCodec.LegacyCellStride)];
        for (int index = 0; index < cells.Length; index++)
        {
            LegacyGridCellV3V4 cell = cells[index];
            Span<byte> destination = bytes.AsSpan(index * WorldCellCodec.LegacyCellStride, WorldCellCodec.LegacyCellStride);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[0..4], cell.MaterialIndex);
            WriteSingle(destination[4..8], cell.Mass);
            WriteSingle(destination[8..12], cell.VelocityX);
            WriteSingle(destination[12..16], cell.VelocityY);
            WriteSingle(destination[16..20], cell.Pressure);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[20..24], cell.IsActive);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[24..28], cell.BodyId);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[28..32], cell.RestFrames);
        }
        return bytes;
    }

    private static byte[] EncodeCurrentCells(params GridCell[] cells)
    {
        byte[] bytes = new byte[checked(cells.Length * WorldCellCodec.CurrentCellStride)];
        cells.AsSpan().CopyTo(MemoryMarshal.Cast<byte, GridCell>(bytes));
        return bytes;
    }

    private static byte[] EncodeV5Cells(params LegacyGridCellV5[] cells)
    {
        byte[] bytes = new byte[checked(cells.Length * WorldCellCodec.V5CellStride)];
        cells.AsSpan().CopyTo(MemoryMarshal.Cast<byte, LegacyGridCellV5>(bytes));
        return bytes;
    }

    private static void WriteSingle(Span<byte> destination, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(destination, BitConverter.SingleToInt32Bits(value));

    private static LegacyGridCellV3V4 CreateLegacyCell(
        uint materialIndex,
        float mass,
        float velocityX,
        float velocityY,
        float pressure,
        uint isActive,
        uint bodyId,
        uint restFrames) =>
        new()
        {
            MaterialIndex = materialIndex,
            Mass = mass,
            VelocityX = velocityX,
            VelocityY = velocityY,
            Pressure = pressure,
            IsActive = isActive,
            BodyId = bodyId,
            RestFrames = restFrames
        };

    private static GridCell CreateCurrentCell(
        LegacyGridCellV3V4 source,
        uint materialIndex,
        float temperature) =>
        new()
        {
            MaterialIndex = materialIndex,
            Mass = source.Mass,
            VelocityX = source.VelocityX,
            VelocityY = source.VelocityY,
            Pressure = source.Pressure,
            IsActive = source.IsActive,
            BodyId = source.BodyId,
            RestFrames = source.RestFrames,
            Temperature = temperature
        };

    private static SimulationWorldSnapshot CreateSnapshot(int width, int height, params GridCell[] cells)
    {
        byte[] bytes = new byte[checked(cells.Length * WorldCellCodec.CurrentCellStride)];
        cells.AsSpan().CopyTo(MemoryMarshal.Cast<byte, GridCell>(bytes));
        return new SimulationWorldSnapshot(width, height, bytes);
    }

    private static void AssertCells(SimulationWorldSnapshot? snapshot, params GridCell[] expected)
    {
        Require(snapshot is not null, "Loaded world snapshot is missing.");
        ReadOnlySpan<GridCell> actual = MemoryMarshal.Cast<byte, GridCell>(snapshot!.Grid);
        Require(actual.Length == expected.Length, "Loaded cell count changed.");
        for (int index = 0; index < expected.Length; index++)
        {
            GridCell left = actual[index];
            GridCell right = expected[index];
            Require(left.MaterialIndex == right.MaterialIndex, $"Cell {index} MaterialIndex changed.");
            Require(SameFloat(left.Mass, right.Mass), $"Cell {index} Mass changed.");
            Require(SameFloat(left.VelocityX, right.VelocityX), $"Cell {index} VelocityX changed.");
            Require(SameFloat(left.VelocityY, right.VelocityY), $"Cell {index} VelocityY changed.");
            Require(SameFloat(left.Pressure, right.Pressure), $"Cell {index} Pressure changed.");
            Require(left.IsActive == right.IsActive, $"Cell {index} IsActive changed.");
            Require(left.BodyId == right.BodyId, $"Cell {index} BodyId changed.");
            Require(left.RestFrames == right.RestFrames, $"Cell {index} RestFrames changed.");
            Require(SameFloat(left.Temperature, right.Temperature), $"Cell {index} Temperature changed.");
            Require(SameFloat(left.Lifetime, right.Lifetime), $"Cell {index} Lifetime changed.");
        }
    }

    private static bool SameFloat(float left, float right) =>
        BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);

    private static bool ContainsWarning(IReadOnlyList<string> warnings, string value)
    {
        foreach (string warning in warnings)
        {
            if (warning.Contains(value, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static Task WriteJsonAsync(string path, object value) =>
        File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(value));

    private static string CreateExternalMaterialJson(string id) => $$"""
        {
          "schema": 1,
          "id": "{{id}}",
          "name": "Runtime order probe",
          "kind": "granular",
          "color": "#ABCDEF",
          "physics": { "density": 2.5, "friction": 0.4, "flowRate": 0.2 },
          "thermal": { "initialTemperature": 123.0, "conductivity": 0.2, "heatCapacity": 1.5 },
          "ui": { "hidden": false }
        }
        """;

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
