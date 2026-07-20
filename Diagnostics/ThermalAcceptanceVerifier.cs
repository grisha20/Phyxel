using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal readonly record struct ThermalAcceptanceCheckpoint(
    uint Frame,
    ulong ThermalTicks,
    SimulationWorldSnapshot Snapshot);

internal static class ThermalAcceptanceVerifier
{
    private readonly record struct Metrics(
        int Active,
        float Minimum,
        float Maximum,
        double Average,
        double Energy,
        bool Valid);

    private readonly record struct RegionThermalMetrics(
        int Cells,
        double Mass,
        double MassWeightedTemperature,
        float Minimum,
        float Maximum);

    public static bool Validate(
        AcceptanceScenarioMode mode,
        MaterialRegistry materials,
        SimulationWorldSnapshot snapshot,
        ulong thermalTicks,
        TemperatureProbeResult? probe,
        IReadOnlyList<ThermalAcceptanceCheckpoint> checkpoints,
        TemperatureProbeAcceptanceTrace probeTrace,
        out string report)
    {
        if (mode == AcceptanceScenarioMode.TemperatureTool)
        {
            return ValidateTemperatureTool(
                materials,
                snapshot,
                checkpoints,
                probeTrace,
                out report);
        }
        if (mode == AcceptanceScenarioMode.TemperatureProbeGpu)
        {
            return ValidateTemperatureProbe(materials, probeTrace, out report);
        }
        if (mode == AcceptanceScenarioMode.SteamSelfCooling)
        {
            return ValidateSteamSelfCooling(materials, snapshot, checkpoints, out report);
        }

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
                string checkpointDetail = string.Empty;
                if (mode == AcceptanceScenarioMode.ThermalContact)
                {
                    bool dynamics = ValidateContactCheckpoints(
                        initial,
                        snapshot,
                        checkpoints,
                        materials,
                        out checkpointDetail);
                    passed &= dynamics && VerifyV5RoundTrip(snapshot, materials);
                }
                detail = $"hot={hot:0.000} cold={cold:0.000} transfer={400 - hot:0.000}" +
                    (mode == AcceptanceScenarioMode.ThermalContact ? $" checkpoints={checkpointDetail}" : string.Empty);
                break;
            case AcceptanceScenarioMode.ThermalCapacity:
                GridCell lowCell = CellAt(snapshot, 239, 135);
                GridCell highCell = CellAt(snapshot, 240, 135);
                double expectedEquilibrium = before.Energy / TotalCapacity(initial, materials);
                passed = common && thermalTicks >= 250 && lowCell.Temperature < 400 &&
                    highCell.Temperature > 0 &&
                    Math.Abs(lowCell.Temperature - expectedEquilibrium) <= 0.1 &&
                    Math.Abs(highCell.Temperature - expectedEquilibrium) <= 0.1;
                detail = $"lowCapacity={lowCell.Temperature:0.000} " +
                    $"highCapacity={highCell.Temperature:0.000} equilibrium={expectedEquilibrium:0.000}";
                break;
            case AcceptanceScenarioMode.ThermalConductivityCompare:
                double fastTransfer = RegionEnergy(initial, materials, 140, 171, 60, 91) -
                    RegionEnergy(snapshot, materials, 140, 171, 60, 91);
                double slowTransfer = RegionEnergy(initial, materials, 140, 171, 160, 191) -
                    RegionEnergy(snapshot, materials, 140, 171, 160, 191);
                passed = common && fastTransfer > 100 && slowTransfer > 0 &&
                    fastTransfer >= slowTransfer * 2;
                detail = $"fastTransfer={fastTransfer:0.000} slowTransfer={slowTransfer:0.000} " +
                    $"ratio={fastTransfer / Math.Max(slowTransfer, 1e-9):0.000}";
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
                RegionThermalMetrics beforeMixed = MeasureMaterialRegion(
                    initial, gas, 151, 238, 81, 189);
                RegionThermalMetrics afterMixed = MeasureMaterialRegion(
                    snapshot, gas, 151, 238, 81, 189);
                int foreignLeft = CountMaterialInRegion(snapshot, otherGas, 151, 238, 81, 189);
                int foreignRight = CountMaterialInRegion(snapshot, gas, 242, 329, 81, 189);
                int mixedCells = CountTemperatureBetween(
                    snapshot, gas, 151, 238, 81, 189, 1, 399);
                double expectedMixedTemperature = beforeMixed.MassWeightedTemperature;
                passed = common &&
                    RelativeError(beforeMass, afterMass) <= 0.0005 &&
                    RelativeError(beforeGasEnergy, afterGasEnergy) <= 0.0005 &&
                    RelativeError(beforeOtherMass, afterOtherMass) <= 0.0005 &&
                    RelativeError(beforeOtherEnergy, afterOtherEnergy) <= 0.0005 &&
                    afterMixed.Cells > 0 &&
                    RelativeError(beforeMixed.Mass, afterMixed.Mass) <= 0.0005 &&
                    RelativeError(expectedMixedTemperature, afterMixed.MassWeightedTemperature) <= 0.0005 &&
                    mixedCells > 0 &&
                    foreignLeft == 0 && foreignRight == 0;
                detail = $"gasMass={beforeMass:0.000}/{afterMass:0.000} gasEnergy={beforeGasEnergy:0.000}/{afterGasEnergy:0.000} " +
                    $"mixedTemperature={afterMixed.MassWeightedTemperature:0.000}/{expectedMixedTemperature:0.000} " +
                    $"mixedRange={beforeMixed.Minimum:0.000}-{beforeMixed.Maximum:0.000}/" +
                    $"{afterMixed.Minimum:0.000}-{afterMixed.Maximum:0.000} mixedCells={mixedCells} " +
                    $"mixedMass={beforeMixed.Mass:0.000}/{afterMixed.Mass:0.000} " +
                    $"otherMass={beforeOtherMass:0.000}/{afterOtherMass:0.000} foreign={foreignLeft}/{foreignRight}";
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

    private static bool ValidateTemperatureTool(
        MaterialRegistry materials,
        SimulationWorldSnapshot final,
        IReadOnlyList<ThermalAcceptanceCheckpoint> checkpoints,
        TemperatureProbeAcceptanceTrace probeTrace,
        out string report)
    {
        if (checkpoints.Count != 2)
        {
            report = $"PHYXEL_TEMPERATURE_TOOL checkpoints={checkpoints.Count}";
            return false;
        }

        SimulationWorldSnapshot initial = ThermalAcceptanceScenario.Create(
            AcceptanceScenarioMode.TemperatureTool,
            final.Width,
            final.Height,
            materials) ?? throw new InvalidOperationException("Temperature tool fixture is missing.");
        ThermalAcceptanceCheckpoint checkpoint = checkpoints[0];
        ThermalAcceptanceCheckpoint diffusedCheckpoint = checkpoints[1];
        ReadOnlySpan<GridCell> initialCells = MemoryMarshal.Cast<byte, GridCell>(initial.Grid);
        ReadOnlySpan<GridCell> pausedCells = MemoryMarshal.Cast<byte, GridCell>(checkpoint.Snapshot.Grid);
        bool exactMutation = initialCells.Length == pausedCells.Length;
        int heated = 0;
        int unchanged = 0;
        int erased = 0;
        for (int y = 0; exactMutation && y < checkpoint.Snapshot.Height; y++)
        {
            for (int x = 0; x < checkpoint.Snapshot.Width; x++)
            {
                int index = y * checkpoint.Snapshot.Width + x;
                GridCell before = initialCells[index];
                GridCell after = pausedCells[index];
                int dx = x - 240;
                int dy = y - 135;
                int distanceSquared = dx * dx + dy * dy;
                if (distanceSquared <= 4 * 4)
                {
                    exactMutation &= IsDefault(after);
                    erased++;
                }
                else if (distanceSquared <= 15 * 15 && before.IsActive != 0)
                {
                    exactMutation &= SameExceptTemperature(before, after) && after.Temperature == 500;
                    heated++;
                }
                else
                {
                    exactMutation &= SameCell(before, after);
                    unchanged++;
                }
            }
        }

        GridCell emptyTarget = CellAt(checkpoint.Snapshot, 80, 60);
        GridCell preserved = CellAt(checkpoint.Snapshot, 245, 135);
        GridCell pausedHot = CellAt(checkpoint.Snapshot, 252, 135);
        GridCell pausedCold = CellAt(checkpoint.Snapshot, 256, 135);
        GridCell finalHot = CellAt(diffusedCheckpoint.Snapshot, 252, 135);
        GridCell finalCold = CellAt(diffusedCheckpoint.Snapshot, 256, 135);
        Metrics pausedMetrics = Measure(checkpoint.Snapshot, materials);
        Metrics finalMetrics = Measure(diffusedCheckpoint.Snapshot, materials);
        double energyError = RelativeError(pausedMetrics.Energy, finalMetrics.Energy);
        uint sand = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Sand);
        bool probeValid = probeTrace.TemperatureTool is
            { IsActive: 1, MaterialIndex: var probeMaterial, Temperature: 500 } &&
            probeMaterial == sand;
        bool preservedFields = preserved.MaterialIndex == sand && preserved.Mass == 0.75f &&
            preserved.VelocityX == 3.25f && preserved.VelocityY == -1.5f &&
            preserved.Pressure == 2.5f && preserved.IsActive == 1 &&
            preserved.BodyId == 4242 && preserved.RestFrames == 17 &&
            preserved.Temperature == 500;
        bool diffusion = diffusedCheckpoint.ThermalTicks > 0 &&
            finalHot.IsActive != 0 && finalCold.IsActive != 0 &&
            finalHot.Temperature < pausedHot.Temperature &&
            finalCold.Temperature > pausedCold.Temperature &&
            energyError <= 0.0005;
        bool roundTrip = VerifyV5RoundTrip(diffusedCheckpoint.Snapshot, materials);
        bool passed = checkpoint.Frame >= 3 && checkpoint.ThermalTicks == 0 &&
            diffusedCheckpoint.Frame >= 120 && diffusedCheckpoint.ThermalTicks > 0 &&
            exactMutation && heated > 0 && erased > 0 && IsDefault(emptyTarget) &&
            preservedFields && probeValid && diffusion && roundTrip && final.Grid.Length == 0;
        report = $"PHYXEL_TEMPERATURE_TOOL checkpoints={checkpoint.Frame}/{checkpoint.ThermalTicks}," +
            $"{diffusedCheckpoint.Frame}/{diffusedCheckpoint.ThermalTicks} " +
            $"heated={heated} unchanged={unchanged} erased={erased} exact={exactMutation} " +
            $"paused={pausedHot.Temperature:0.000}/{pausedCold.Temperature:0.000} " +
            $"final={finalHot.Temperature:0.000}/{finalCold.Temperature:0.000} " +
            $"probe={FormatProbe(probeTrace.TemperatureTool)} emptyDefault={IsDefault(emptyTarget)} " +
            $"preservedFields={preservedFields} energyError={energyError:P5} " +
            $"roundTrip={roundTrip} clearedEmpty={final.Grid.Length == 0}";
        return passed;
    }

    private static bool ValidateSteamSelfCooling(
        MaterialRegistry materials,
        SimulationWorldSnapshot final,
        IReadOnlyList<ThermalAcceptanceCheckpoint> checkpoints,
        out string report)
    {
        const double massTolerance = 0.0005;
        uint steam = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Steam);
        uint water = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        SimulationWorldSnapshot initial = ThermalAcceptanceScenario.Create(
            AcceptanceScenarioMode.SteamSelfCooling,
            final.Width,
            final.Height,
            materials) ?? throw new InvalidOperationException("Steam cooling fixture is missing.");
        bool valid = checkpoints.Count == 6;
        StringBuilder stages = new();
        double initialMass = PhaseFamilyMetrics(initial, steam, water).Mass;
        double previousTemperature = materials[CoreMaterialIds.Steam].Properties.InitialTemperature;
        ulong previousTick = 0;
        for (int index = 0; index < checkpoints.Count; index++)
        {
            ThermalAcceptanceCheckpoint checkpoint = checkpoints[index];
            (double mass, double temperature, int steamCells, int waterCells) =
                PhaseFamilyMetrics(checkpoint.Snapshot, steam, water);
            bool paused = index == 0;
            valid &= RelativeError(initialMass, mass) <= massTolerance &&
                (paused
                    ? checkpoint.ThermalTicks == 0 &&
                        checkpoint.Snapshot.Grid.AsSpan().SequenceEqual(initial.Grid)
                    : checkpoint.ThermalTicks > previousTick &&
                        temperature < previousTemperature && temperature > 20);
            if (index is >= 1 and <= 5)
            {
                valid &= steamCells > 0 && waterCells == 0;
            }
            if (index == 5)
            {
                valid &= checkpoint.ThermalTicks is >= 80 and <= 81 &&
                    temperature > 110;
            }
            if (stages.Length > 0) stages.Append(';');
            stages.Append($"{checkpoint.Frame}/{checkpoint.ThermalTicks}:" +
                $"{temperature:0.000}/{mass:0.000}/{steamCells}/{waterCells}");
            previousTemperature = temperature;
            previousTick = checkpoint.ThermalTicks;
        }
        (double finalMass, double finalTemperature, int finalSteam, int finalWater) =
            PhaseFamilyMetrics(final, steam, water);
        valid &= RelativeError(initialMass, finalMass) <= massTolerance &&
            finalTemperature <= previousTemperature + 1e-5 &&
            finalWater == 0 && finalSteam > 0;
        report = $"PHYXEL_STEAM_SELF_COOLING initialMass={initialMass:0.000} " +
            $"finalMass={finalMass:0.000} finalTemperature={finalTemperature:0.000} " +
            $"externalAmbientLoss=true neighbourExchange=false stages={stages}";
        return valid;
    }

    private static (double Mass, double Temperature, int SteamCells, int WaterCells) PhaseFamilyMetrics(
        SimulationWorldSnapshot snapshot,
        uint steam,
        uint water)
    {
        double mass = 0;
        double weightedTemperature = 0;
        int steamCells = 0;
        int waterCells = 0;
        foreach (GridCell cell in MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid))
        {
            if (cell.IsActive == 0 || (cell.MaterialIndex != steam && cell.MaterialIndex != water))
            {
                continue;
            }
            mass += cell.Mass;
            weightedTemperature += cell.Mass * cell.Temperature;
            if (cell.MaterialIndex == steam) steamCells++;
            else waterCells++;
        }
        return (mass, weightedTemperature / Math.Max(mass, 1e-9), steamCells, waterCells);
    }

    private static bool SameExceptTemperature(GridCell first, GridCell second) =>
        first.MaterialIndex == second.MaterialIndex &&
        BitConverter.SingleToInt32Bits(first.Mass) == BitConverter.SingleToInt32Bits(second.Mass) &&
        BitConverter.SingleToInt32Bits(first.VelocityX) == BitConverter.SingleToInt32Bits(second.VelocityX) &&
        BitConverter.SingleToInt32Bits(first.VelocityY) == BitConverter.SingleToInt32Bits(second.VelocityY) &&
        BitConverter.SingleToInt32Bits(first.Pressure) == BitConverter.SingleToInt32Bits(second.Pressure) &&
        first.IsActive == second.IsActive && first.BodyId == second.BodyId &&
        first.RestFrames == second.RestFrames;

    private static bool SameCell(GridCell first, GridCell second) =>
        SameExceptTemperature(first, second) &&
        BitConverter.SingleToInt32Bits(first.Temperature) ==
        BitConverter.SingleToInt32Bits(second.Temperature);

    private static bool ValidateContactCheckpoints(
        SimulationWorldSnapshot initial,
        SimulationWorldSnapshot final,
        IReadOnlyList<ThermalAcceptanceCheckpoint> checkpoints,
        MaterialRegistry materials,
        out string detail)
    {
        const double tolerance = 1e-5;
        Metrics initialMetrics = Measure(initial, materials);
        double previousHot = 400;
        double previousCold = 0;
        ulong previousTick = 0;
        bool valid = checkpoints.Count == 4;
        StringBuilder stages = new();

        foreach (ThermalAcceptanceCheckpoint checkpoint in checkpoints)
        {
            Metrics metrics = Measure(checkpoint.Snapshot, materials);
            (double hot, double cold) = RegionAverages(
                checkpoint.Snapshot, 160, 239, 240, 319, 100, 169);
            double energyError = RelativeError(initialMetrics.Energy, metrics.Energy);
            valid &= checkpoint.ThermalTicks > previousTick && metrics.Valid &&
                hot <= previousHot + tolerance && cold >= previousCold - tolerance &&
                hot > cold && energyError <= 0.0005;
            if (stages.Length > 0) stages.Append(';');
            stages.Append($"{checkpoint.ThermalTicks}:{hot:0.000}/{cold:0.000}/{energyError:P5}");
            previousTick = checkpoint.ThermalTicks;
            previousHot = hot;
            previousCold = cold;
        }

        Metrics finalMetrics = Measure(final, materials);
        (double finalHot, double finalCold) = RegionAverages(
            final, 160, 239, 240, 319, 100, 169);
        double finalEnergyError = RelativeError(initialMetrics.Energy, finalMetrics.Energy);
        valid &= finalHot <= previousHot + tolerance && finalCold >= previousCold - tolerance &&
            finalHot > finalCold && finalEnergyError <= 0.0005;
        stages.Append($";final:{finalHot:0.000}/{finalCold:0.000}/{finalEnergyError:P5}");
        detail = stages.ToString();
        return valid;
    }

    private static bool ValidateTemperatureProbe(
        MaterialRegistry materials,
        TemperatureProbeAcceptanceTrace trace,
        out string report)
    {
        uint sand = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Sand);
        uint acceptanceProbe = materials.GetRequiredRuntimeIndex("acceptance:temperature_probe");
        uint fast = materials.GetRequiredRuntimeIndex("acceptance:thermal_fast");
        bool sandValid = IsProbe(trace.Sand, sand, 20, 0.01f);
        bool materialValid = IsProbe(trace.AcceptanceMaterial, acceptanceProbe, 123.5f, 0.01f);
        bool hotValid = trace.Hot is { IsActive: 1, MaterialIndex: var hotMaterial } hot &&
            hotMaterial == fast && hot.Temperature is > 200 and < 400;
        bool coldValid = trace.Cold is { IsActive: 1, MaterialIndex: var coldMaterial } cold &&
            coldMaterial == fast && cold.Temperature is > 0 and < 200;
        bool ordered = hotValid && coldValid &&
            trace.Hot!.Value.Temperature > trace.Cold!.Value.Temperature;
        bool emptyValid = trace.Empty is
            { IsActive: 0, MaterialIndex: 0, Temperature: 0 };
        bool passed = sandValid && materialValid && hotValid && coldValid && ordered &&
            emptyValid && trace.ResetAfterClear && trace.ResetAfterScale;
        report = $"PHYXEL_TEMPERATURE_PROBE_GPU sand={FormatProbe(trace.Sand)} " +
            $"material={FormatProbe(trace.AcceptanceMaterial)} hot={FormatProbe(trace.Hot)} " +
            $"cold={FormatProbe(trace.Cold)} empty={FormatProbe(trace.Empty)} " +
            $"clearReset={trace.ResetAfterClear} scaleReset={trace.ResetAfterScale}";
        return passed;
    }

    private static bool IsProbe(
        TemperatureProbeResult? result,
        uint material,
        float temperature,
        float tolerance) =>
        result is { IsActive: 1, MaterialIndex: var actualMaterial } value &&
        actualMaterial == material && Math.Abs(value.Temperature - temperature) <= tolerance;

    private static string FormatProbe(TemperatureProbeResult? result) => result is { } value
        ? $"{value.IsActive}/{value.MaterialIndex}/{value.Temperature:0.000}"
        : "null";

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

    private static GridCell CellAt(SimulationWorldSnapshot snapshot, int x, int y) =>
        MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid)[y * snapshot.Width + x];

    private static double TotalCapacity(
        SimulationWorldSnapshot snapshot,
        MaterialRegistry materials)
    {
        double capacity = 0;
        foreach (GridCell cell in MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid))
        {
            if (cell.IsActive == 0) continue;
            capacity += materials[cell.MaterialIndex].Properties.HeatCapacity *
                Math.Max(cell.Mass, ThermalAcceptanceScenario.MinimumThermalMass);
        }
        return capacity;
    }

    private static double RegionEnergy(
        SimulationWorldSnapshot snapshot,
        MaterialRegistry materials,
        int left,
        int right,
        int top,
        int bottom)
    {
        ReadOnlySpan<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        double energy = 0;
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                GridCell cell = cells[y * snapshot.Width + x];
                if (cell.IsActive == 0) continue;
                energy += materials[cell.MaterialIndex].Properties.HeatCapacity *
                    Math.Max(cell.Mass, ThermalAcceptanceScenario.MinimumThermalMass) *
                    cell.Temperature;
            }
        }
        return energy;
    }

    private static RegionThermalMetrics MeasureMaterialRegion(
        SimulationWorldSnapshot snapshot,
        uint material,
        int left,
        int right,
        int top,
        int bottom)
    {
        ReadOnlySpan<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        int count = 0;
        double mass = 0;
        double massTemperature = 0;
        float minimum = float.PositiveInfinity;
        float maximum = float.NegativeInfinity;
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                GridCell cell = cells[y * snapshot.Width + x];
                if (cell.IsActive == 0 || cell.MaterialIndex != material) continue;
                count++;
                mass += cell.Mass;
                massTemperature += cell.Mass * cell.Temperature;
                minimum = Math.Min(minimum, cell.Temperature);
                maximum = Math.Max(maximum, cell.Temperature);
            }
        }
        return new RegionThermalMetrics(
            count,
            mass,
            massTemperature / Math.Max(mass, ThermalAcceptanceScenario.MinimumThermalMass),
            minimum,
            maximum);
    }

    private static int CountMaterialInRegion(
        SimulationWorldSnapshot snapshot,
        uint material,
        int left,
        int right,
        int top,
        int bottom)
    {
        ReadOnlySpan<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        int count = 0;
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                GridCell cell = cells[y * snapshot.Width + x];
                if (cell.IsActive != 0 && cell.MaterialIndex == material) count++;
            }
        }
        return count;
    }

    private static int CountTemperatureBetween(
        SimulationWorldSnapshot snapshot,
        uint material,
        int left,
        int right,
        int top,
        int bottom,
        float minimum,
        float maximum)
    {
        ReadOnlySpan<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        int count = 0;
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                GridCell cell = cells[y * snapshot.Width + x];
                if (cell.IsActive != 0 && cell.MaterialIndex == material &&
                    cell.Temperature > minimum && cell.Temperature < maximum)
                {
                    count++;
                }
            }
        }
        return count;
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
