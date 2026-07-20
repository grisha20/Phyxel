using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class SteamDistributionAndCoolingAcceptanceVerifier
{
    public static bool Validate(
        SimulationWorldSnapshot snapshot,
        MaterialRegistry materials,
        IReadOnlyList<ThermalAcceptanceCheckpoint> checkpoints,
        out string report)
    {
        List<string> errors = [];
        Require(checkpoints.Count == 4,
            $"steam distribution checkpoints expected=4 actual={checkpoints.Count}", errors);
        uint steam = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Steam);
        uint water = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        List<SteamStage> stages = checkpoints
            .Select(checkpoint => Measure(checkpoint.Snapshot, steam, water))
            .ToList();
        SteamStage final = Measure(snapshot, steam, water);

        foreach ((SteamStage stage, int index) in stages.Select((stage, index) => (stage, index)))
        {
            Require(Math.Abs(stage.TotalMass -
                SteamDistributionAndCoolingAcceptanceScenario.InitialMass) <= 0.05,
                $"steam/water mass changed at stage {index}={stage.TotalMass:F4}", errors);
        }
        Require(Math.Abs(final.TotalMass -
            SteamDistributionAndCoolingAcceptanceScenario.InitialMass) <= 0.05,
            $"final steam/water mass changed={final.TotalMass:F4}", errors);

        if (stages.Count >= 4)
        {
            Require(stages[0].SteamMass > 255 && stages[0].WaterMass < 0.01 &&
                stages[0].SteamAverageY < 180 &&
                stages[0].SteamHorizontalSpan is >= 45 and <= 100 &&
                stages[0].SteamRows >= 35,
                $"steam did not begin a local buoyant spread={stages[0]}", errors);
            Require(stages[1].SteamMass > 255 && stages[1].WaterMass < 0.01 &&
                stages[1].SteamAverageY < stages[0].SteamAverageY &&
                stages[1].SteamHorizontalSpan >= stages[0].SteamHorizontalSpan,
                $"steam did not continue rising and spreading={stages[0]} -> {stages[1]}", errors);
            Require(stages[2].SteamMass > 255 && stages[2].WaterMass < 0.01 &&
                stages[2].SteamAverageY < 50 &&
                stages[2].SteamHorizontalSpan is >= 85 and <= 160,
                $"steam moved non-locally or failed to reach the ceiling={stages[2]}", errors);
            Require(stages[0].SteamFractionalCells == 0 &&
                stages[1].SteamFractionalCells == 0 &&
                stages[2].SteamFractionalCells == 0,
                "steam packets were split during gas redistribution", errors);
            Require(stages[1].SteamTemperature < stages[0].SteamTemperature &&
                stages[2].SteamTemperature < stages[1].SteamTemperature &&
                stages[3].SteamTemperature < stages[2].SteamTemperature,
                $"steam did not cool monotonically={string.Join(" -> ", stages)}", errors);
            Require(stages[3].SteamMass > 50 && stages[3].WaterMass > 50 &&
                stages[3].WaterAverageY > 220 && stages[3].WaterHorizontalSpan >= 80,
                $"steam did not condense gradually into falling droplets={stages[3]}", errors);
        }
        Require(final.WaterMass > 240 && final.SteamMass < 16 &&
            final.WaterAverageY > 235 && final.WaterHorizontalSpan >= 100,
            $"steam did not finish settling after gradual condensation={final}", errors);

        report = "PHYXEL_STEAM_DISTRIBUTION " +
            string.Join(" | ", stages.Select((stage, index) =>
                $"stage{index + 1}={stage}")) + $" | final={final}";
        if (errors.Count == 0)
        {
            return true;
        }
        report += Environment.NewLine + "PHYXEL_STEAM_DISTRIBUTION_FAILURE " +
            string.Join("; ", errors.Take(12));
        return false;
    }

    private static SteamStage Measure(
        SimulationWorldSnapshot snapshot,
        uint steam,
        uint water)
    {
        ReadOnlySpan<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        double steamMass = 0;
        double waterMass = 0;
        double steamWeightedY = 0;
        double waterWeightedY = 0;
        double steamWeightedTemperature = 0;
        int steamCells = 0;
        int steamFractionalCells = 0;
        int steamMinX = int.MaxValue;
        int steamMaxX = int.MinValue;
        int steamMinY = int.MaxValue;
        int steamMaxY = int.MinValue;
        int waterMinX = int.MaxValue;
        int waterMaxX = int.MinValue;
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = cells[y * snapshot.Width + x];
                if (cell.IsActive == 0)
                {
                    continue;
                }
                if (cell.MaterialIndex == steam)
                {
                    steamCells++;
                    if (Math.Abs(cell.Mass - 1) > 0.0001) steamFractionalCells++;
                    steamMass += cell.Mass;
                    steamWeightedY += y * cell.Mass;
                    steamWeightedTemperature += cell.Temperature * cell.Mass;
                    steamMinX = Math.Min(steamMinX, x);
                    steamMaxX = Math.Max(steamMaxX, x);
                    steamMinY = Math.Min(steamMinY, y);
                    steamMaxY = Math.Max(steamMaxY, y);
                }
                else if (cell.MaterialIndex == water)
                {
                    waterMass += cell.Mass;
                    waterWeightedY += y * cell.Mass;
                    waterMinX = Math.Min(waterMinX, x);
                    waterMaxX = Math.Max(waterMaxX, x);
                }
            }
        }
        return new SteamStage(
            steamMass,
            waterMass,
            steamCells,
            steamFractionalCells,
            steamMass > 0 ? steamWeightedY / steamMass : 0,
            waterMass > 0 ? waterWeightedY / waterMass : 0,
            steamMass > 0 ? steamWeightedTemperature / steamMass : 0,
            steamMass > 0 ? steamMaxX - steamMinX + 1 : 0,
            steamMass > 0 ? steamMaxY - steamMinY + 1 : 0,
            waterMass > 0 ? waterMaxX - waterMinX + 1 : 0);
    }

    private static void Require(bool condition, string message, List<string> errors)
    {
        if (!condition)
        {
            errors.Add(message);
        }
    }

    private readonly record struct SteamStage(
        double SteamMass,
        double WaterMass,
        int SteamCells,
        int SteamFractionalCells,
        double SteamAverageY,
        double WaterAverageY,
        double SteamTemperature,
        int SteamHorizontalSpan,
        int SteamRows,
        int WaterHorizontalSpan)
    {
        public double TotalMass => SteamMass + WaterMass;

        public override string ToString() =>
            $"steam={SteamMass:F3}/{SteamCells} fractional={SteamFractionalCells} " +
            $"water={WaterMass:F3} temp={SteamTemperature:F2} " +
            $"steamY={SteamAverageY:F1} steamSpan={SteamHorizontalSpan} rows={SteamRows} " +
            $"waterY={WaterAverageY:F1} waterSpan={WaterHorizontalSpan}";
    }
}
