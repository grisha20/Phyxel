using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phyxel.Core;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class WorldCellCodecRegressionVerifier
{
    private const uint WorldFileMagic = 0x5058594C;
    private const int LegacyHeaderSize = 20;

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
            await VerifyV3Async(directory, serializer, materials);
            await VerifyV4MigrationsAsync(directory, serializer, materials);
            await VerifyV4RoundTripAsync(directory, serializer, materials);
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
            Marshal.SizeOf<GridCell>() == WorldCellCodec.CurrentCellStride,
            "GridCell must remain 32 bytes in this commit.");
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
            CreateCurrentCell(stone, stoneIndex),
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
            CreateCurrentCell(goldSand, sandIndex),
            CreateCurrentCell(concrete, stoneIndex),
            default);
    }

    private static async Task VerifyV4RoundTripAsync(
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
            RestFrames = 11
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
            RestFrames = 19
        };
        SimulationWorldSnapshot source = CreateSnapshot(3, 1, sand, stone, default);
        string scenePath = Path.Combine(directory, "roundtrip-v4.json");
        SimulationSettings settings = new();
        await serializer.SaveAsync(
            scenePath,
            settings,
            checked((ushort)sandIndex),
            source,
            materials);

        string worldPath = Path.ChangeExtension(scenePath, ".world");
        RawWorldFile raw = await SimulationStateSerializer.ReadWorldAsync(worldPath, CancellationToken.None) ??
            throw new InvalidOperationException("Saved v4 world file is missing.");
        Require(raw.Version == 4, "CurrentVersion changed from 4.");
        Require(raw.StoredCellStride == 32, "v4 reader did not report the fixed 32-byte stride.");
        Require(new FileInfo(worldPath).Length == LegacyHeaderSize + raw.CellBytes.Length, "v4 world header format changed.");

        LoadedSimulationScene loaded = await serializer.LoadAsync(scenePath, materials) ??
            throw new InvalidOperationException("Saved v4 scene did not reload.");
        AssertCells(loaded.World, sand, stone, default);
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

        ExpectInvalid(() => WorldCellCodec.Decode(new RawWorldFile(4, 1, 1, 31, oneCell)));
        ExpectInvalid(() => WorldCellCodec.Decode(new RawWorldFile(4, 1, 1, 36, oneCell)));
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

    private static GridCell CreateCurrentCell(LegacyGridCellV3V4 source, uint materialIndex) =>
        new()
        {
            MaterialIndex = materialIndex,
            Mass = source.Mass,
            VelocityX = source.VelocityX,
            VelocityY = source.VelocityY,
            Pressure = source.Pressure,
            IsActive = source.IsActive,
            BodyId = source.BodyId,
            RestFrames = source.RestFrames
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

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
