using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class ThermalAcceptanceVerifier
{
    private readonly record struct Metrics(
        int Active,
        float Minimum,
        float Maximum,
        double Average,
        double Energy,
        bool Valid);

    public static bool Validate(
        AcceptanceScenarioMode mode,
        MaterialRegistry materials,
        SimulationWorldSnapshot snapshot,
        ulong thermalTicks,
        TemperatureProbeResult? probe,
        out string report)
    {
        SimulationWorldSnapshot initial = ThermalAcceptanceScenario.Create(
            mode,
            snapshot.Width,
            snapshot.Height,
            materials) ?? throw new InvalidOperationException("Thermal acceptance fixture is missing.");
        Metrics before = Measure(initial, materials);
        Metrics after = Measure(snapshot, materials);
        double energyError = RelativeError(before.Energy, after.Energy);
        bool common = thermalTicks > 0 && before.Valid && after.Valid && energyError <= 0.0005;
        bool passed;
        string detail;

        switch (mode)
        {
            case AcceptanceScenarioMode.ThermalUniform:
                passed = common && after.Minimum == 400 && after.Maximum == 400 &&
                    probe is { IsActive: 1, Temperature: 400 };
                detail = $"uniform={after.Minimum:R}/{after.Maximum:R} probe={probe?.Temperature:R}";
                break;
            case AcceptanceScenarioMode.ThermalContact:
            case AcceptanceScenarioMode.ThermalFast:
            case AcceptanceScenarioMode.ThermalSlow:
                (double hot, double cold) = RegionAverages(snapshot, 160, 239, 240, 319, 100, 169);
                passed = common && hot is > 200 and < 400 && cold is > 0 and < 200 && hot > cold &&
                    after.Minimum >= 0 && after.Maximum <= 400;
                if (mode == AcceptanceScenarioMode.ThermalContact)
                {
                    passed &= VerifyV5RoundTrip(snapshot, materials);
                }
                detail = $"hot={hot:0.000} cold={cold:0.000} transfer={400 - hot:0.000}";
                break;
            case AcceptanceScenarioMode.ThermalCapacity:
                (double low, double high) = RegionAverages(snapshot, 160, 239, 240, 319, 100, 169);
                passed = common && low < 400 && high > 0 && (400 - low) > high;
                detail = $"lowCapacity={low:0.000} highCapacity={high:0.000}";
                break;
            case AcceptanceScenarioMode.ThermalInsulator:
            case AcceptanceScenarioMode.ThermalVacuum:
                passed = common && SameTemperatures(initial, snapshot);
                detail = $"unchanged={SameTemperatures(initial, snapshot)}";
                break;
            case AcceptanceScenarioMode.ThermalGas:
                uint gas = materials.GetRequiredRuntimeIndex("acceptance:thermal_gas");
                uint otherGas = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Gas);
                (double beforeMass, double beforeGasEnergy) = MaterialTotals(initial, materials, gas);
                (double afterMass, double afterGasEnergy) = MaterialTotals(snapshot, materials, gas);
                (double beforeOtherMass, double beforeOtherEnergy) = MaterialTotals(initial, materials, otherGas);
                (double afterOtherMass, double afterOtherEnergy) = MaterialTotals(snapshot, materials, otherGas);
                passed = common &&
                    RelativeError(beforeMass, afterMass) <= 0.0005 &&
                    RelativeError(beforeGasEnergy, afterGasEnergy) <= 0.0005 &&
                    RelativeError(beforeOtherMass, afterOtherMass) <= 0.0005 &&
                    RelativeError(beforeOtherEnergy, afterOtherEnergy) <= 0.0005;
                detail = $"gasMass={beforeMass:0.000}/{afterMass:0.000} gasEnergy={beforeGasEnergy:0.000}/{afterGasEnergy:0.000} " +
                    $"otherMass={beforeOtherMass:0.000}/{afterOtherMass:0.000}";
                break;
            default:
                passed = false;
                detail = "unsupported";
                break;
        }

        report = $"PHYXEL_THERMAL_{mode.ToString().ToUpperInvariant()} ticks={thermalTicks} " +
            $"active={after.Active} range={after.Minimum:0.000}/{after.Maximum:0.000} " +
            $"energyError={energyError:P5} {detail}";
        return passed;
    }

    private static Metrics Measure(SimulationWorldSnapshot snapshot, MaterialRegistry materials)
    {
        ReadOnlySpan<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        int active = 0;
        float minimum = float.PositiveInfinity;
        float maximum = float.NegativeInfinity;
        double sum = 0;
        double energy = 0;
        bool valid = true;
        foreach (GridCell cell in cells)
        {
            if (cell.IsActive == 0)
            {
                valid &= IsDefault(cell);
                continue;
            }
            if (!float.IsFinite(cell.Temperature) || cell.Temperature is < -273.15f or > 5000f ||
                cell.MaterialIndex >= materials.Count)
            {
                valid = false;
                continue;
            }
            MaterialProperties material = materials[cell.MaterialIndex].Properties;
            double capacity = material.HeatCapacity *
                Math.Max(cell.Mass, ThermalAcceptanceScenario.MinimumThermalMass);
            valid &= double.IsFinite(capacity) && capacity > 0;
            active++;
            minimum = Math.Min(minimum, cell.Temperature);
            maximum = Math.Max(maximum, cell.Temperature);
            sum += cell.Temperature;
            energy += capacity * cell.Temperature;
        }
        return new Metrics(active, minimum, maximum, sum / Math.Max(1, active), energy, valid);
    }

    private static (double Left, double Right) RegionAverages(
        SimulationWorldSnapshot snapshot,
        int leftStart,
        int leftEnd,
        int rightStart,
        int rightEnd,
        int top,
        int bottom)
    {
        ReadOnlySpan<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        double left = 0;
        double right = 0;
        int leftCount = 0;
        int rightCount = 0;
        for (int y = top; y <= bottom; y++)
        {
            for (int x = leftStart; x <= leftEnd; x++)
            {
                GridCell cell = cells[y * snapshot.Width + x];
                if (cell.IsActive != 0) { left += cell.Temperature; leftCount++; }
            }
            for (int x = rightStart; x <= rightEnd; x++)
            {
                GridCell cell = cells[y * snapshot.Width + x];
                if (cell.IsActive != 0) { right += cell.Temperature; rightCount++; }
            }
        }
        return (left / Math.Max(1, leftCount), right / Math.Max(1, rightCount));
    }

    private static (double Mass, double Energy) MaterialTotals(
        SimulationWorldSnapshot snapshot,
        MaterialRegistry materials,
        uint materialIndex)
    {
        double mass = 0;
        double energy = 0;
        MaterialProperties material = materials[materialIndex].Properties;
        foreach (GridCell cell in MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid))
        {
            if (cell.IsActive == 0 || cell.MaterialIndex != materialIndex) continue;
            mass += cell.Mass;
            energy += material.HeatCapacity *
                Math.Max(cell.Mass, ThermalAcceptanceScenario.MinimumThermalMass) * cell.Temperature;
        }
        return (mass, energy);
    }

    private static bool SameTemperatures(
        SimulationWorldSnapshot first,
        SimulationWorldSnapshot second)
    {
        ReadOnlySpan<GridCell> left = MemoryMarshal.Cast<byte, GridCell>(first.Grid);
        ReadOnlySpan<GridCell> right = MemoryMarshal.Cast<byte, GridCell>(second.Grid);
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
        {
            if (left[index].IsActive != right[index].IsActive ||
                BitConverter.SingleToInt32Bits(left[index].Temperature) !=
                BitConverter.SingleToInt32Bits(right[index].Temperature))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsDefault(GridCell cell) =>
        cell.MaterialIndex == 0 && cell.Mass == 0 && cell.VelocityX == 0 &&
        cell.VelocityY == 0 && cell.Pressure == 0 && cell.IsActive == 0 &&
        cell.BodyId == 0 && cell.RestFrames == 0 && cell.Temperature == 0;

    private static bool VerifyV5RoundTrip(
        SimulationWorldSnapshot snapshot,
        MaterialRegistry materials)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"phyxel-thermal-roundtrip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string scenePath = Path.Combine(directory, "thermal-v5.json");
            SimulationStateSerializer serializer = new();
            LoadedSimulationScene loaded = Task.Run(async () =>
            {
                await serializer.SaveAsync(
                    scenePath,
                    new Core.SimulationSettings(),
                    materials.GetRequiredRuntimeIndex(CoreMaterialIds.Sand),
                    snapshot,
                    materials);
                return await serializer.LoadAsync(scenePath, materials);
            }).GetAwaiter().GetResult() ??
                throw new InvalidDataException("Thermal v5 scene did not reload.");
            if (loaded.World is null || loaded.World.Grid.Length != snapshot.Grid.Length)
            {
                return false;
            }
            ReadOnlySpan<GridCell> before = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
            ReadOnlySpan<GridCell> after = MemoryMarshal.Cast<byte, GridCell>(loaded.World.Grid);
            for (int index = 0; index < before.Length; index++)
            {
                if (BitConverter.SingleToInt32Bits(before[index].Temperature) !=
                    BitConverter.SingleToInt32Bits(after[index].Temperature))
                {
                    return false;
                }
            }
            return true;
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static double RelativeError(double expected, double actual) =>
        Math.Abs(actual - expected) / Math.Max(1, Math.Abs(expected));
}
