using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class GasUniformDistributionAcceptanceVerifier
{
    public static bool Validate(
        SimulationWorldSnapshot snapshot,
        MaterialRegistry materials,
        IReadOnlyList<ThermalAcceptanceCheckpoint> checkpoints,
        out string report)
    {
        List<string> errors = [];
        Require(checkpoints.Count == 1,
            $"gas checkpoints expected=1 actual={checkpoints.Count}", errors);

        uint gas = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Gas);
        GasMetrics single = Measure(
            snapshot, gas,
            GasUniformDistributionAcceptanceScenario.SingleLeft + 4,
            GasUniformDistributionAcceptanceScenario.SingleTop + 4,
            GasUniformDistributionAcceptanceScenario.SingleRight - 4,
            GasUniformDistributionAcceptanceScenario.SingleBottom - 4);
        Require(Math.Abs(single.Mass - GasUniformDistributionAcceptanceScenario.SingleMass) <= 0.05,
            $"single gas mass changed={single.Mass:F4}", errors);
        Require(single.HorizontalSpan >= 135 && single.Rows >= 85,
            $"single gas did not fill the chamber={single}", errors);
        // Buoyancy intentionally keeps a vertical concentration gradient when
        // there is not enough gas to fill the room. The production solver must
        // still improve the compact baseline (CV 3.46) by a clear margin.
        Require(single.CoefficientOfVariation <= 2.15,
            $"single gas concentration remained uneven={single}", errors);
        Require(single.ParityImbalance <= 0.18,
            $"single gas has vertical parity bands={single}", errors);

        GasMetrics obstacle = Measure(
            snapshot,
            materials.GetRequiredRuntimeIndex("acceptance:gas"),
            GasUniformDistributionAcceptanceScenario.ObstacleLeft + 4,
            GasUniformDistributionAcceptanceScenario.ObstacleTop + 4,
            GasUniformDistributionAcceptanceScenario.ObstacleRight - 4,
            GasUniformDistributionAcceptanceScenario.ObstacleBottom - 4);
        Require(Math.Abs(obstacle.Mass - GasUniformDistributionAcceptanceScenario.ObstacleMass) <= 0.05,
            $"obstacle gas mass changed={obstacle.Mass:F4}", errors);
        Require(obstacle.MinimumX < 80 && obstacle.MaximumX > 400 && obstacle.MinimumY < 168,
            $"gas did not traverse openings and ceiling pockets={obstacle}", errors);

        GasMetrics steam = default;
        GasMetrics smoke = default;
        GasMetrics ordinary = default;
        GasMetrics co2 = default;
        if (checkpoints.Count > 0)
        {
            SimulationWorldSnapshot layered = checkpoints[0].Snapshot;
            int left = GasUniformDistributionAcceptanceScenario.MultiLeft + 4;
            int top = GasUniformDistributionAcceptanceScenario.MultiTop + 4;
            int right = GasUniformDistributionAcceptanceScenario.MultiRight - 4;
            int bottom = GasUniformDistributionAcceptanceScenario.MultiBottom - 4;
            steam = Measure(layered, materials.GetRequiredRuntimeIndex(CoreMaterialIds.Steam),
                left, top, right, bottom);
            smoke = Measure(layered, materials.GetRequiredRuntimeIndex(CoreMaterialIds.Smoke),
                left, top, right, bottom);
            ordinary = Measure(layered, gas, left, top, right, bottom);
            co2 = Measure(layered, materials.GetRequiredRuntimeIndex(CoreMaterialIds.Co2),
                left, top, right, bottom);
            foreach ((string name, GasMetrics metrics) in new[]
            {
                ("steam", steam), ("smoke", smoke), ("gas", ordinary), ("co2", co2)
            })
            {
                Require(Math.Abs(metrics.Mass - GasUniformDistributionAcceptanceScenario.MultiMass) <= 0.05,
                    $"{name} mass changed={metrics.Mass:F4}", errors);
                Require(metrics.HorizontalSpan >= 180,
                    $"{name} remained a compact gas pile={metrics}", errors);
            }
            Require(steam.AverageY + 1 < smoke.AverageY &&
                smoke.AverageY + 1 < ordinary.AverageY &&
                ordinary.AverageY + 1 < co2.AverageY,
                $"gases are not density-layered steam={steam.AverageY:F2} " +
                $"smoke={smoke.AverageY:F2} gas={ordinary.AverageY:F2} co2={co2.AverageY:F2}",
                errors);
        }

        uint sand = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Sand);
        uint water = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        Require(Math.Abs(TotalMass(snapshot, sand) - 200) <= 0.01,
            "gas solver changed sand mass", errors);
        // Condensed steam can legitimately add core:water elsewhere, but the
        // original liquid fixture must never be removed by gas redistribution.
        Require(TotalMass(snapshot, water) >= 199.99,
            "gas solver removed liquid mass", errors);

        report = $"PHYXEL_GAS_UNIFORM single={single} obstacle={obstacle} " +
            $"layers=steam({steam}) smoke({smoke}) gas({ordinary}) co2({co2})";
        if (errors.Count == 0)
        {
            return true;
        }
        report += Environment.NewLine + "PHYXEL_GAS_UNIFORM_FAILURE " +
            string.Join("; ", errors.Take(16));
        return false;
    }

    private static GasMetrics Measure(
        SimulationWorldSnapshot snapshot,
        uint material,
        int left,
        int top,
        int right,
        int bottom)
    {
        ReadOnlySpan<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        int count = 0;
        int minX = int.MaxValue;
        int maxX = int.MinValue;
        int minY = int.MaxValue;
        int maxY = int.MinValue;
        double mass = 0;
        double weightedY = 0;
        double sumSquares = 0;
        double evenMass = 0;
        double oddMass = 0;
        int area = checked((right - left + 1) * (bottom - top + 1));
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                GridCell cell = cells[y * snapshot.Width + x];
                float localMass = cell.IsActive != 0 && cell.MaterialIndex == material
                    ? cell.Mass
                    : 0;
                sumSquares += localMass * localMass;
                if (localMass <= 0)
                {
                    continue;
                }
                count++;
                mass += localMass;
                weightedY += y * localMass;
                if ((x & 1) == 0)
                {
                    evenMass += localMass;
                }
                else
                {
                    oddMass += localMass;
                }
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }
        double mean = mass / area;
        double variance = Math.Max(0, sumSquares / area - mean * mean);
        double parity = Math.Abs(evenMass - oddMass) / Math.Max(0.001, mass);
        return new GasMetrics(
            count,
            mass,
            mass > 0 ? weightedY / mass : 0,
            count > 0 ? minX : 0,
            count > 0 ? maxX : 0,
            count > 0 ? minY : 0,
            count > 0 ? maxY : 0,
            count > 0 ? maxX - minX + 1 : 0,
            count > 0 ? maxY - minY + 1 : 0,
            mean > 0 ? Math.Sqrt(variance) / mean : double.PositiveInfinity,
            parity);
    }

    private static double TotalMass(SimulationWorldSnapshot snapshot, uint material)
    {
        double mass = 0;
        foreach (GridCell cell in MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid))
        {
            mass += cell.IsActive != 0 && cell.MaterialIndex == material ? cell.Mass : 0;
        }
        return mass;
    }

    private static void Require(bool condition, string message, List<string> errors)
    {
        if (!condition)
        {
            errors.Add(message);
        }
    }

    private readonly record struct GasMetrics(
        int Cells,
        double Mass,
        double AverageY,
        int MinimumX,
        int MaximumX,
        int MinimumY,
        int MaximumY,
        int HorizontalSpan,
        int Rows,
        double CoefficientOfVariation,
        double ParityImbalance)
    {
        public override string ToString() =>
            $"cells={Cells} mass={Mass:F3} y={AverageY:F1} span={HorizontalSpan} " +
            $"rows={Rows} bounds={MinimumX},{MinimumY}-{MaximumX},{MaximumY} " +
            $"cv={CoefficientOfVariation:F2} parity={ParityImbalance:F3}";
    }
}
