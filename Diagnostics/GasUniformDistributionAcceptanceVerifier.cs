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

        uint co2Material = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Co2);
        uint gas = materials.GetRequiredRuntimeIndex("acceptance:gas");
        GasMetrics single = Measure(
            snapshot, co2Material,
            GasUniformDistributionAcceptanceScenario.SingleLeft + 4,
            GasUniformDistributionAcceptanceScenario.SingleTop + 4,
            GasUniformDistributionAcceptanceScenario.SingleRight - 4,
            GasUniformDistributionAcceptanceScenario.SingleBottom - 4);
        Require(Math.Abs(single.Mass - GasUniformDistributionAcceptanceScenario.SingleMass) <= 0.05,
            $"single gas mass changed={single.Mass:F4}", errors);
        Require(single.Cells == GasUniformDistributionAcceptanceScenario.SingleMass && single.FractionalCells == 0,
            $"single gas particles split or disappeared={single}", errors);
        Require(single.SigmaX >= 8 && single.AverageY >= 110,
            $"CO2 did not form a broad, gently descending cloud={single}", errors);
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
        Require(obstacle.Cells == GasUniformDistributionAcceptanceScenario.ObstacleMass && obstacle.FractionalCells == 0,
            $"obstacle gas particles split or disappeared={obstacle}", errors);
        // A 20-cell-wide uniform source starts at sigmaX=sqrt((20^2-1)/12).
        // This neutral fixture has weaker diffusion than CO2; require measurable
        // growth from its own source rather than a CO2-derived width.
        Require(obstacle.SigmaX >= Math.Sqrt(399.0 / 12) * 1.05 && obstacle.MinimumY < 150,
            $"neutral gas did not diffuse near the divider={obstacle}", errors);

        GasMetrics steam = default;
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
            ordinary = Measure(layered, gas, left, top, right, bottom);
            co2 = Measure(layered, materials.GetRequiredRuntimeIndex(CoreMaterialIds.Co2),
                left, top, right, bottom);
            foreach ((string name, GasMetrics metrics) in new[]
            {
                ("steam", steam), ("gas", ordinary), ("co2", co2)
            })
            {
                Require(Math.Abs(metrics.Mass - GasUniformDistributionAcceptanceScenario.MultiMass) <= 0.05,
                    $"{name} mass changed={metrics.Mass:F4}", errors);
                Require(metrics.Cells == GasUniformDistributionAcceptanceScenario.MultiMass && metrics.FractionalCells == 0,
                    $"{name} particles split or disappeared={metrics}", errors);
                Require(metrics.HorizontalSpan >= 45,
                    $"{name} did not diffuse from the mixed gas cloud={metrics}", errors);
            }
            Require(steam.AverageY + 2 < co2.AverageY &&
                ordinary.AverageY + 2 < co2.AverageY,
                $"light gases did not stay above CO2 steam={steam.AverageY:F2} " +
                $"gas={ordinary.AverageY:F2} co2={co2.AverageY:F2}",
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
            $"layers=steam({steam}) gas({ordinary}) co2({co2})";
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
        double weightedX = 0;
        double weightedXX = 0;
        double sumSquares = 0;
        double evenMass = 0;
        double oddMass = 0;
        int fractionalCells = 0;
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
                if (Math.Abs(localMass - 1) > 0.0001) fractionalCells++;
                mass += localMass;
                weightedY += y * localMass;
                weightedX += x * localMass;
                weightedXX += x * x * localMass;
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
            fractionalCells,
            mass,
            mass > 0 ? weightedY / mass : 0,
            count > 0 ? minX : 0,
            count > 0 ? maxX : 0,
            count > 0 ? minY : 0,
            count > 0 ? maxY : 0,
            count > 0 ? maxX - minX + 1 : 0,
            count > 0 ? maxY - minY + 1 : 0,
            mean > 0 ? Math.Sqrt(variance) / mean : double.PositiveInfinity,
            parity,
            mass > 0 ? Math.Sqrt(Math.Max(0, weightedXX / mass - Math.Pow(weightedX / mass, 2))) : 0);
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
        int FractionalCells,
        double Mass,
        double AverageY,
        int MinimumX,
        int MaximumX,
        int MinimumY,
        int MaximumY,
        int HorizontalSpan,
        int Rows,
        double CoefficientOfVariation,
        double ParityImbalance,
        double SigmaX)
    {
        public override string ToString() =>
            $"cells={Cells} fractional={FractionalCells} mass={Mass:F3} " +
            $"y={AverageY:F1} sigmaX={SigmaX:F3} span={HorizontalSpan} " +
            $"rows={Rows} bounds={MinimumX},{MinimumY}-{MaximumX},{MaximumY} " +
            $"cv={CoefficientOfVariation:F2} parity={ParityImbalance:F3}";
    }
}
