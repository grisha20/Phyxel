using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

public static class MaterialRegressionVerifier
{
    public static bool ValidateGranularPile(
        SimulationWorldSnapshot snapshot,
        uint runtimeIndex,
        string artifactDirectory,
        string imageName,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        int granular = 0;
        int resting = 0;
        int moving = 0;
        int settled = 0;
        int minimumX = snapshot.Width;
        int maximumX = 0;
        int minimumY = snapshot.Height;
        int maximumY = 0;
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive == 0 || cell.MaterialIndex != runtimeIndex)
                {
                    continue;
                }
                granular++;
                resting += cell.RestFrames >= 30 ? 1 : 0;
                moving += Math.Abs(cell.VelocityX) + Math.Abs(cell.VelocityY) > 0.02f ? 1 : 0;
                settled += y >= 205 ? 1 : 0;
                minimumX = Math.Min(minimumX, x);
                maximumX = Math.Max(maximumX, x);
                minimumY = Math.Min(minimumY, y);
                maximumY = Math.Max(maximumY, y);
            }
        }

        bool image = File.Exists(Path.Combine(artifactDirectory, imageName));
        bool passed = granular >= 700 && settled >= granular * 0.85 &&
            resting == granular && moving == 0 && maximumX - minimumX >= 35 && image;
        report = $"PHYXEL_GRANULAR_PILE cells={granular} settled={settled} resting={resting} moving={moving} bounds={minimumX},{minimumY}-{maximumX},{maximumY}";
        return passed;
    }

    public static bool ValidateSlope(
        SimulationWorldSnapshot snapshot,
        uint runtimeIndex,
        string artifactDirectory,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        int sand = 0;
        int resting = 0;
        int moving = 0;
        int upper = 0;
        int settled = 0;
        int minimumX = snapshot.Width;
        int maximumX = 0;
        int minimumY = snapshot.Height;
        int maximumY = 0;
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive == 0 || cell.MaterialIndex != runtimeIndex)
                {
                    continue;
                }
                sand++;
                resting += cell.RestFrames >= 30 ? 1 : 0;
                moving += Math.Abs(cell.VelocityX) + Math.Abs(cell.VelocityY) > 0.02f ? 1 : 0;
                upper += x >= 330 && y <= 180 ? 1 : 0;
                settled += y >= 205 ? 1 : 0;
                minimumX = Math.Min(minimumX, x);
                maximumX = Math.Max(maximumX, x);
                minimumY = Math.Min(minimumY, y);
                maximumY = Math.Max(maximumY, y);
            }
        }
        bool images = File.Exists(Path.Combine(artifactDirectory, "E_slope_fall.png")) &&
            File.Exists(Path.Combine(artifactDirectory, "E_slope_rest.png"));
        bool passed = sand >= 700 && upper <= sand / 20 && settled >= sand * 0.85 &&
            resting == sand && moving == 0 && maximumX - minimumX >= 35 && images;
        report = $"PHYXEL_E sand={sand} upper={upper} settled={settled} resting={resting} moving={moving} bounds={minimumX},{minimumY}-{maximumX},{maximumY}";
        return passed;
    }

    internal static bool ValidateGas(
        SimulationWorldSnapshot snapshot,
        uint runtimeIndex,
        string artifactDirectory,
        string riseImageName,
        string spreadImageName,
        out string report,
        bool heavyGas = true,
        IReadOnlyList<ThermalAcceptanceCheckpoint>? checkpoints = null)
    {
        ReadOnlySpan<GridCell> grid = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        int gas = 0;
        int resting = 0;
        int moving = 0;
        int dense = 0;
        int fractional = 0;
        int minimumX = snapshot.Width;
        int maximumX = 0;
        int minimumY = snapshot.Height;
        int maximumY = 0;
        double mass = 0;
        double weightedY = 0;
        double weightedX = 0;
        double weightedXX = 0;
        int blockedDebt = 0;
        int floorGas = 0;
        int floorUpward = 0;
        ReadOnlySpan<GasMotionState> motion = snapshot.GasMotion is null
            ? [] : MemoryMarshal.Cast<byte, GasMotionState>(snapshot.GasMotion);
        bool hasMotion = motion.Length == grid.Length;
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive == 0 || cell.MaterialIndex != runtimeIndex)
                {
                    continue;
                }
                gas++;
                mass += cell.Mass;
                weightedY += y * cell.Mass;
                dense += cell.Mass >= 0.8f ? 1 : 0;
                fractional += cell.Mass > 0.0005f && cell.Mass < 0.9995f ? 1 : 0;
                resting += cell.RestFrames >= 60 ? 1 : 0;
                if (hasMotion)
                {
                    GasMotionState state = motion[y * snapshot.Width + x];
                    moving += Math.Abs(state.VelocityX) + Math.Abs(state.VelocityY) > 0.02f ? 1 : 0;
                    bool solidBelow = y + 1 == snapshot.Height ||
                        (grid[(y + 1) * snapshot.Width + x].IsActive != 0 &&
                            grid[(y + 1) * snapshot.Width + x].MaterialIndex != runtimeIndex);
                    if (solidBelow)
                    {
                        floorGas++;
                        floorUpward += state.VelocityY < -0.02f ? 1 : 0;
                        blockedDebt += state.OffsetY >= 0.5f ? 1 : 0;
                    }
                    bool solidSide = state.OffsetX <= -0.5f && (x == 0 ||
                        (grid[y * snapshot.Width + x - 1].IsActive != 0 &&
                            grid[y * snapshot.Width + x - 1].MaterialIndex != runtimeIndex)) ||
                        state.OffsetX >= 0.5f && (x + 1 == snapshot.Width ||
                        (grid[y * snapshot.Width + x + 1].IsActive != 0 &&
                            grid[y * snapshot.Width + x + 1].MaterialIndex != runtimeIndex));
                    blockedDebt += solidSide ? 1 : 0;
                }
                weightedX += x * cell.Mass;
                weightedXX += x * x * cell.Mass;
                minimumX = Math.Min(minimumX, x);
                maximumX = Math.Max(maximumX, x);
                minimumY = Math.Min(minimumY, y);
                maximumY = Math.Max(maximumY, y);
            }
        }
        double averageY = weightedY / Math.Max(0.001, mass);
        bool images = File.Exists(Path.Combine(artifactDirectory, riseImageName)) &&
            File.Exists(Path.Combine(artifactDirectory, spreadImageName));
        double meanX = weightedX / Math.Max(0.001, mass);
        double sigmaX = Math.Sqrt(Math.Max(0, weightedXX / Math.Max(0.001, mass) - meanX * meanX));
        double initialMass = mass;
        if (checkpoints is { Count: > 0 })
        {
            initialMass = 0;
            foreach (GridCell initial in MemoryMarshal.Cast<byte, GridCell>(checkpoints[0].Snapshot.Grid))
            {
                if (initial.IsActive != 0 && initial.MaterialIndex == runtimeIndex) initialMass += initial.Mass;
            }
        }
        // The source disk has sigmaX~12.5. Test actual mass-weighted spreading,
        // not the range of a lone outlier or obsolete fractional concentrations.
        bool passed = mass >= 1000 && Math.Abs(mass - gas) < 0.01 &&
            Math.Abs(mass - initialMass) < 0.01 &&
            averageY >= (heavyGas ? 160 : 35) && averageY <= 245 &&
            sigmaX >= (heavyGas ? 22 : 13) &&
            hasMotion && moving >= gas * 0.9 && blockedDebt == 0 &&
            (!heavyGas || (floorGas > 0 && floorUpward >= floorGas * 0.1)) && images;
        report = $"PHYXEL_F gas={gas} fractional={fractional} mass={mass:0.0} initialMass={initialMass:0.0} averageY={averageY:0.0} sigmaX={sigmaX:0.000} dense={dense} resting={resting} moving={moving} floorGas={floorGas} floorUpward={floorUpward} blockedDebt={blockedDebt} bounds={minimumX},{minimumY}-{maximumX},{maximumY}";
        return passed;
    }
}
