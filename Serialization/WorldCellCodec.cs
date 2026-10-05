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
    byte[] CellBytes,
    byte[]? Oxidizer = null,
    byte[]? AirThermal = null,
    byte[]? ReactionPending = null,byte[]? ReactionPulse = null,byte[]? Air = null,byte[]? GasMotion = null,byte[]? Filters = null);

internal static class WorldCellCodec
{
    public const int LegacyCellStride = 32;
    public const int V5CellStride = 36;
    public const int V6CellStride = 40;
    public const int V12CellStride = 48;
    public const int V14CellStride = 52;
    public const int CurrentCellStride = 56;

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
            6 or 7 or 8 or 9 or 10 or 11 => V6CellStride,
            12 or 13 => V12CellStride,
            14 => V14CellStride,
            15 or 16 or 17 => CurrentCellStride,
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
        ValidateFilters(world.Width,world.Height,world.Filters);
        long expectedLength = ValidateStoredWorld(
            world.Version,
            world.Width,
            world.Height,
            world.StoredCellStride,
            world.CellBytes.Length);
        if (world.CellBytes.Length == 0)
        {
            ValidateReactionState(world.Width,world.Height,world.ReactionPending,world.ReactionPulse,world.Air,world.GasMotion);
            ValidateOxidizer(world.Width, world.Height, world.Oxidizer, world.Version >= 8);
            ValidateAirThermal(world.Width,world.Height,world.AirThermal);
            return new SimulationWorldSnapshot(world.Width, world.Height, [],
                Air:world.Air,GasMotion:world.GasMotion,Oxidizer: world.Oxidizer is null ? null : (byte[])world.Oxidizer.Clone(),
                AirThermal: world.AirThermal is null ? null : (byte[])world.AirThermal.Clone(),ReactionPending:world.ReactionPending,ReactionPulse:world.ReactionPulse,Filters:world.Filters);
        }
        if (world.CellBytes.Length != expectedLength)
        {
            throw new InvalidDataException("World cell section length is invalid.");
        }

        return world.Version switch
        {
            3 or 4 => DecodeLegacy(world),
            5 => DecodeV5(world),
            6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15 or 16 or 17 => DecodeCurrent(world),
            _ => throw new InvalidDataException($"Unsupported world version {world.Version}.")
        };
    }

    private static SimulationWorldSnapshot DecodeLegacy(RawWorldFile world)
    {
        ReadOnlySpan<LegacyGridCellV3V4> legacyCells =
            MemoryMarshal.Cast<byte, LegacyGridCellV3V4>(world.CellBytes);
        byte[] currentBytes = new byte[checked(legacyCells.Length * CurrentCellStride)];
        Span<GridCell> currentCells = MemoryMarshal.Cast<byte, GridCell>(currentBytes.AsSpan());
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

    internal static void ValidateFilters(int width,int height,byte[]? bytes)
    {
        if(bytes is not {Length:>0})return;
        if(bytes.LongLength!=(long)width*height*4)throw new InvalidDataException("Filter dimensions do not match world.");
        foreach(uint rule in MemoryMarshal.Cast<byte,uint>(bytes))Phyxel.Core.FilterRules.Validate(rule,MaterialRegistry.MaximumMaterials);
    }

    internal static void ValidateOxidizer(int width, int height, byte[]? bytes, bool compressed = true)
    {
        if (bytes is null || bytes.Length == 0) return;
        if (bytes.Length != (long)width * height * sizeof(float))
            throw new InvalidDataException("Oxidizer section does not match world dimensions.");
        foreach (float value in MemoryMarshal.Cast<byte, float>(bytes))
            if (!float.IsFinite(value) || value < 0 || (!compressed && value > 1))
                throw new InvalidDataException("Oxidizer amount must be finite and nonnegative.");
    }

    internal static void ValidateAirThermal(int width,int height,byte[]? bytes)
    {
        if (bytes is null || bytes.Length==0) return;
        if (bytes.Length!=(long)((width+3)/4)*((height+3)/4)*8)
            throw new InvalidDataException("Air heat section does not match carrier dimensions.");
        // Carrier transport can leave finite energy in a nearly empty node.
        // The particle temperature ceiling is not an invariant of this field;
        // retain the exact state so a problematic scene can be reproduced.
        int index=0;
        foreach(var state in MemoryMarshal.Cast<byte,System.Numerics.Vector2>(bytes))
        {
            if (!float.IsFinite(state.X) || !float.IsFinite(state.Y) || state.X<0 || state.Y<0 ||
                (state.Y==0 && state.X!=0) ||
                (state.Y>0 && !float.IsFinite(state.X/state.Y)))
                throw new InvalidDataException(FormattableString.Invariant($"Invalid air energy/capacity at node {index}: E={state.X:R}, C={state.Y:R}."));
            index++;
        }
    }

    internal static void ValidateReactionState(int width,int height,byte[]? pending,byte[]? pulse,byte[]? air,byte[]? motion)
    {
        long fine=(long)width*height*16,coarse=(long)((width+3)/4)*((height+3)/4)*16;
        void Size(byte[]? bytes,long expected)
        { if(bytes is not null && bytes.LongLength!=expected) throw new InvalidDataException("Invalid reaction/motion state dimensions."); }
        Size(pending,fine);Size(pulse,coarse);Size(air,coarse);Size(motion,fine);
        if(pending is not null) foreach(var s in MemoryMarshal.Cast<byte,System.Numerics.Vector4>(pending))
            if(!float.IsFinite(s.X)||!float.IsFinite(s.Y)||!float.IsFinite(s.Z)||s.X<0||s.Y<0||s.Z<0||s.W!=0 ||
               (s.Z==0&&s.Y!=0)||(s.Z>0&&(!float.IsFinite(s.Y/s.Z)||s.Y/s.Z>5273.16f)))
                throw new InvalidDataException("Invalid pending reaction quantity/energy/capacity.");
        if(pulse is not null) foreach(var s in MemoryMarshal.Cast<byte,System.Numerics.Vector4>(pulse))
            if(!float.IsFinite(s.X)||!float.IsFinite(s.Y)||!float.IsFinite(s.Z)||Math.Abs(s.X)>256||Math.Abs(s.Y)>64||Math.Abs(s.Z)>64||!float.IsFinite(s.W)||s.W<0)
                throw new InvalidDataException("Invalid reaction pressure/face velocity.");
        if(air is not null) foreach(var s in MemoryMarshal.Cast<byte,AirCell>(air))
            if(!float.IsFinite(s.Pressure)||!float.IsFinite(s.VelocityX)||!float.IsFinite(s.VelocityY)||!float.IsFinite(s.Blocked)||
               Math.Abs(s.Pressure)>256||Math.Abs(s.VelocityX)>64||Math.Abs(s.VelocityY)>64||s.Blocked<0||s.Blocked>1)
                throw new InvalidDataException("Invalid saved air state.");
        if(motion is not null) foreach(float f in MemoryMarshal.Cast<byte,float>(motion))
            if(!float.IsFinite(f)) throw new InvalidDataException("Invalid saved particle motion.");
    }

    private static SimulationWorldSnapshot DecodeCurrent(RawWorldFile world)
    {
        ValidateReactionState(world.Width,world.Height,world.ReactionPending,world.ReactionPulse,world.Air,world.GasMotion);
        ValidateOxidizer(world.Width, world.Height, world.Oxidizer, world.Version >= 8);
        ValidateAirThermal(world.Width,world.Height,world.AirThermal);
        byte[] currentBytes;
        if (world.Version < 15)
        {
            int stride=world.Version<12 ? V6CellStride : world.Version<14 ? V12CellStride : V14CellStride;
            int count=world.CellBytes.Length / stride;
            currentBytes=new byte[checked(count*CurrentCellStride)];
            for(int i=0;i<count;i++)
                world.CellBytes.AsSpan(i*stride,stride).CopyTo(currentBytes.AsSpan(i*CurrentCellStride));
        }
        else currentBytes = (byte[])world.CellBytes.Clone();
        Span<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(currentBytes.AsSpan());
        for (int index = 0; index < cells.Length; index++)
        {
            if (cells[index].IsActive == 0)
            {
                cells[index] = default;
                continue;
            }
            float temperature = cells[index].Temperature;
            if (!float.IsFinite(cells[index].FuelMass) || cells[index].FuelMass < 0)
                throw new InvalidDataException($"Invalid absorbed fuel mass in cell {index}.");
            if (!float.IsFinite(temperature) ||
                temperature < MaterialRegistry.MinimumInitialTemperature ||
                temperature > MaterialRegistry.MaximumInitialTemperature)
            {
                throw new InvalidDataException(
                    $"World cell {index} contains invalid temperature {temperature}.");
            }
            float lifetime = cells[index].Lifetime;
            if (!float.IsFinite(cells[index].MoistureMass) || cells[index].MoistureMass < 0 ||
                !float.IsFinite(cells[index].MoistureEnergy) || cells[index].MoistureEnergy < 0 ||
                (cells[index].MoistureMass == 0 && cells[index].MoistureEnergy != 0))
                throw new InvalidDataException($"World cell {index} has invalid moisture.");
            if (!float.IsFinite(lifetime) || lifetime < (world.Version >= 11 ? -MaterialRegistry.MaximumLifetime : 0) ||
                lifetime > MaterialRegistry.MaximumLifetime)
            {
                throw new InvalidDataException(
                    $"World cell {index} contains invalid lifetime {lifetime}.");
            }
        }
        return new SimulationWorldSnapshot(world.Width,world.Height,currentBytes,
            world.Air is null?null:(byte[])world.Air.Clone(),world.GasMotion is null?null:(byte[])world.GasMotion.Clone(),
            world.Oxidizer is null?null:(byte[])world.Oxidizer.Clone(),world.AirThermal is null?null:(byte[])world.AirThermal.Clone(),
            world.ReactionPending is null?null:(byte[])world.ReactionPending.Clone(),world.ReactionPulse is null?null:(byte[])world.ReactionPulse.Clone(),world.Filters is null?null:(byte[])world.Filters.Clone());
    }

    private static SimulationWorldSnapshot DecodeV5(RawWorldFile world)
    {
        ReadOnlySpan<LegacyGridCellV5> oldCells =
            MemoryMarshal.Cast<byte, LegacyGridCellV5>(world.CellBytes);
        byte[] currentBytes = new byte[checked(oldCells.Length * CurrentCellStride)];
        Span<GridCell> currentCells = MemoryMarshal.Cast<byte, GridCell>(currentBytes.AsSpan());
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
