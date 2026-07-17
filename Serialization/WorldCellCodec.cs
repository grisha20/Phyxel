using System;
using System.IO;
using System.Runtime.InteropServices;
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
    public const int CurrentCellStride = 32;

    public static void ValidateLayoutContracts()
    {
        int legacySize = Marshal.SizeOf<LegacyGridCellV3V4>();
        if (legacySize != LegacyCellStride)
        {
            throw new InvalidOperationException(
                $"LegacyGridCellV3V4 layout changed: expected {LegacyCellStride} bytes, got {legacySize}.");
        }

        int currentSize = Marshal.SizeOf<GridCell>();
        if (currentSize != CurrentCellStride)
        {
            throw new InvalidOperationException(
                $"GridCell layout changed: expected {CurrentCellStride} bytes, got {currentSize}.");
        }
    }

    public static long ValidateStoredWorld(
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
        if (storedCellStride != LegacyCellStride)
        {
            throw new InvalidDataException($"Unsupported world cell stride {storedCellStride}.");
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
        // v3/v4 use a zero-length section for a world that never allocated a simulation grid.
        if (storedDataLength != 0 && storedDataLength != expectedLength)
        {
            throw new InvalidDataException(
                $"World cell section length {storedDataLength} does not match expected length {expectedLength}.");
        }
        return expectedLength;
    }

    public static SimulationWorldSnapshot Decode(RawWorldFile world)
    {
        if (world.Version is not (3 or 4))
        {
            throw new InvalidDataException($"Unsupported world version {world.Version}.");
        }

        long expectedLength = ValidateStoredWorld(
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
                RestFrames = source.RestFrames
            };
        }

        return new SimulationWorldSnapshot(world.Width, world.Height, currentBytes);
    }
}
