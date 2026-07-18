using System;
using System.IO;
using System.Runtime.InteropServices;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Serialization;

internal sealed record RawWorldFile(
    int Version,
    int Width,
    int Height,
    int StoredCellStride,
    byte[] CellBytes);

internal static class WorldCellCodec
{
    public const int LegacyCellStride = 32;
    public const int V5CellStride = 36;
    public const int CurrentCellStride = 40;

    public static void ValidateLayoutContracts()
    {
        int legacySize = Marshal.SizeOf<LegacyGridCellV3V4>();
        if (legacySize != LegacyCellStride)
        {
            throw new InvalidOperationException(
                $"LegacyGridCellV3V4 layout changed: expected {LegacyCellStride} bytes, got {legacySize}.");
        }
        int v5Size = Marshal.SizeOf<LegacyGridCellV5>();
        if (v5Size != V5CellStride)
        {
            throw new InvalidOperationException(
                $"LegacyGridCellV5 layout changed: expected {V5CellStride} bytes, got {v5Size}.");
        }

        int currentSize = Marshal.SizeOf<GridCell>();
        if (currentSize != CurrentCellStride)
        {
            throw new InvalidOperationException(
                $"GridCell layout changed: expected {CurrentCellStride} bytes, got {currentSize}.");
        }
    }

    public static long ValidateStoredWorld(
        int version,
        int width,
        int height,
        int storedCellStride,
        int storedDataLength)
    {
        ValidateLayoutContracts();
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("World dimensions must be positive.");
        }
        int expectedStride = version switch
        {
            3 or 4 => LegacyCellStride,
            5 => V5CellStride,
            6 => CurrentCellStride,
            _ => throw new InvalidDataException($"Unsupported world version {version}.")
        };
        if (storedCellStride != expectedStride)
        {
            throw new InvalidDataException(
                $"Unsupported world cell stride {storedCellStride} for version {version}; expected {expectedStride}.");
        }
        if (storedDataLength < 0)
        {
            throw new InvalidDataException("World cell section length cannot be negative.");
        }

        long expectedLength;
        try
        {
            expectedLength = checked((long)width * height * storedCellStride);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("World dimensions overflow the cell section size.", exception);
        }
        if (expectedLength > int.MaxValue)
        {
            throw new InvalidDataException("World cell section exceeds the supported size.");
        }
        // A zero-length section represents a world that never allocated a simulation grid.
        if (storedDataLength != 0 && storedDataLength != expectedLength)
        {
            throw new InvalidDataException(
                $"World cell section length {storedDataLength} does not match expected length {expectedLength}.");
        }
        return expectedLength;
    }

    public static SimulationWorldSnapshot Decode(RawWorldFile world)
    {
        long expectedLength = ValidateStoredWorld(
            world.Version,
            world.Width,
            world.Height,
            world.StoredCellStride,
            world.CellBytes.Length);
        if (world.CellBytes.Length == 0)
        {
            return new SimulationWorldSnapshot(world.Width, world.Height, []);
        }
        if (world.CellBytes.Length != expectedLength)
        {
            throw new InvalidDataException("World cell section length is invalid.");
        }

        return world.Version switch
        {
            3 or 4 => DecodeLegacy(world),
            5 => DecodeV5(world),
            6 => DecodeCurrent(world),
            _ => throw new InvalidDataException($"Unsupported world version {world.Version}.")
        };
    }

    private static SimulationWorldSnapshot DecodeLegacy(RawWorldFile world)
    {
        ReadOnlySpan<LegacyGridCellV3V4> legacyCells =
            MemoryMarshal.Cast<byte, LegacyGridCellV3V4>(world.CellBytes);
        byte[] currentBytes = new byte[checked(legacyCells.Length * CurrentCellStride)];
        Span<GridCell> currentCells = MemoryMarshal.Cast<byte, GridCell>(currentBytes);
        for (int index = 0; index < legacyCells.Length; index++)
        {
            LegacyGridCellV3V4 source = legacyCells[index];
            currentCells[index] = new GridCell
            {
                MaterialIndex = source.MaterialIndex,
                Mass = source.Mass,
                VelocityX = source.VelocityX,
                VelocityY = source.VelocityY,
                Pressure = source.Pressure,
                IsActive = source.IsActive,
                BodyId = source.BodyId,
                RestFrames = source.RestFrames,
                Temperature = 0
            };
        }

        return new SimulationWorldSnapshot(world.Width, world.Height, currentBytes);
    }

    private static SimulationWorldSnapshot DecodeCurrent(RawWorldFile world)
    {
        byte[] currentBytes = (byte[])world.CellBytes.Clone();
        Span<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(currentBytes);
        for (int index = 0; index < cells.Length; index++)
        {
            if (cells[index].IsActive == 0)
            {
                cells[index] = default;
                continue;
            }
            float temperature = cells[index].Temperature;
            if (!float.IsFinite(temperature) ||
                temperature < MaterialRegistry.MinimumInitialTemperature ||
                temperature > MaterialRegistry.MaximumInitialTemperature)
            {
                throw new InvalidDataException(
                    $"World cell {index} contains invalid temperature {temperature}.");
            }
            float lifetime = cells[index].Lifetime;
            if (!float.IsFinite(lifetime) || lifetime < 0 || lifetime > MaterialRegistry.MaximumLifetime)
            {
                throw new InvalidDataException(
                    $"World cell {index} contains invalid lifetime {lifetime}.");
            }
        }
        return new SimulationWorldSnapshot(world.Width, world.Height, currentBytes);
    }

    private static SimulationWorldSnapshot DecodeV5(RawWorldFile world)
    {
        ReadOnlySpan<LegacyGridCellV5> oldCells =
            MemoryMarshal.Cast<byte, LegacyGridCellV5>(world.CellBytes);
        byte[] currentBytes = new byte[checked(oldCells.Length * CurrentCellStride)];
        Span<GridCell> currentCells = MemoryMarshal.Cast<byte, GridCell>(currentBytes);
        for (int index = 0; index < oldCells.Length; index++)
        {
            LegacyGridCellV5 source = oldCells[index];
            if (source.IsActive == 0)
            {
                currentCells[index] = default;
                continue;
            }
            if (!float.IsFinite(source.Temperature) ||
                source.Temperature < MaterialRegistry.MinimumInitialTemperature ||
                source.Temperature > MaterialRegistry.MaximumInitialTemperature)
            {
                throw new InvalidDataException(
                    $"World cell {index} contains invalid temperature {source.Temperature}.");
            }
            currentCells[index] = new GridCell
            {
                MaterialIndex = source.MaterialIndex,
                Mass = source.Mass,
                VelocityX = source.VelocityX,
                VelocityY = source.VelocityY,
                Pressure = source.Pressure,
                IsActive = source.IsActive,
                BodyId = source.BodyId,
                RestFrames = source.RestFrames,
                Temperature = source.Temperature,
                Lifetime = 0
            };
        }
        return new SimulationWorldSnapshot(world.Width, world.Height, currentBytes);
    }
}
