using System;
using System.IO;
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

    public static bool ValidateGas(
        SimulationWorldSnapshot snapshot,
        uint runtimeIndex,
        string artifactDirectory,
        string riseImageName,
        string spreadImageName,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        int gas = 0;
        int resting = 0;
        int moving = 0;
        int dense = 0;
        int minimumX = snapshot.Width;
        int maximumX = 0;
        int minimumY = snapshot.Height;
        int maximumY = 0;
        double mass = 0;
        double weightedY = 0;
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
                resting += cell.RestFrames >= 60 ? 1 : 0;
                moving += Math.Abs(cell.VelocityX) + Math.Abs(cell.VelocityY) > 0.02f ? 1 : 0;
                minimumX = Math.Min(minimumX, x);
                maximumX = Math.Max(maximumX, x);
                minimumY = Math.Min(minimumY, y);
                maximumY = Math.Max(maximumY, y);
            }
        }
        double averageY = weightedY / Math.Max(0.001, mass);
        bool images = File.Exists(Path.Combine(artifactDirectory, riseImageName)) &&
            File.Exists(Path.Combine(artifactDirectory, spreadImageName));
        bool passed = gas >= 1000 && mass >= 800 && averageY <= 105 &&
            maximumX - minimumX >= 240 && maximumY - minimumY >= 20 &&
            dense <= gas / 3 && resting == gas && moving == 0 && images;
        report = $"PHYXEL_F gas={gas} mass={mass:0.0} averageY={averageY:0.0} dense={dense} resting={resting} moving={moving} bounds={minimumX},{minimumY}-{maximumX},{maximumY}";
        return passed;
    }
}
