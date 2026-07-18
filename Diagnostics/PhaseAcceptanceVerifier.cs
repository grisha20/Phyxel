using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class PhaseAcceptanceVerifier
{
    public static bool Validate(
        AcceptanceScenarioMode mode,
        MaterialRegistry materials,
        SimulationWorldSnapshot snapshot,
        IReadOnlyList<PhaseAcceptanceCheckpoint> checkpoints,
        ThermalGpuTimingStatistics thermalTiming,
        ThermalGpuTimingStatistics phaseTiming,
        ulong dispatches,
        ulong summaryReadbacks,
        ulong fallbackWakeUps,
        int maximumDispatchesPerFrame,
        PhaseTransitionSummaryFlags summary,
        bool presentationIsCurrent,
        out string report)
    {
        List<string> errors = [];
        int transitions = mode switch
        {
            AcceptanceScenarioMode.PhaseThresholds => ValidateThresholds(materials, checkpoints, errors),
            AcceptanceScenarioMode.PhaseHysteresis => ValidateHysteresis(materials, checkpoints, errors),
            AcceptanceScenarioMode.PhaseSingleTransition => ValidateSingleTransition(materials, checkpoints, errors),
            AcceptanceScenarioMode.PhaseNormalizationMatrix => ValidateNormalizationMatrix(materials, checkpoints, errors),
            AcceptanceScenarioMode.PhaseSummaryLiquidGas => ValidateIsolatedSummary(
                mode, materials, checkpoints, "acceptance:norm_liquid_to_gas", "acceptance:norm_gas",
                PhaseTransitionSummaryFlags.PhaseOccurred |
                PhaseTransitionSummaryFlags.TargetCellular |
                PhaseTransitionSummaryFlags.TargetGas |
                PhaseTransitionSummaryFlags.TouchesLiquid, errors),
            AcceptanceScenarioMode.PhaseSummarySolidLiquid => ValidateIsolatedSummary(
                mode, materials, checkpoints, "acceptance:norm_fixed_to_liquid", "acceptance:norm_liquid",
                PhaseTransitionSummaryFlags.PhaseOccurred |
                PhaseTransitionSummaryFlags.TargetCellular |
                PhaseTransitionSummaryFlags.TargetLiquid |
                PhaseTransitionSummaryFlags.TouchesLiquid |
                PhaseTransitionSummaryFlags.TouchesSolid, errors),
            AcceptanceScenarioMode.PhaseSummaryGasMovable => ValidateIsolatedSummary(
                mode, materials, checkpoints, "acceptance:norm_gas_to_movable", "acceptance:norm_movable",
                PhaseTransitionSummaryFlags.PhaseOccurred |
                PhaseTransitionSummaryFlags.TouchesSolid |
                PhaseTransitionSummaryFlags.TargetMovableSolid, errors),
            AcceptanceScenarioMode.PhaseSummaryLiquidFixed => ValidateIsolatedSummary(
                mode, materials, checkpoints, "acceptance:norm_liquid_to_fixed", "acceptance:norm_fixed",
                PhaseTransitionSummaryFlags.PhaseOccurred |
                PhaseTransitionSummaryFlags.TouchesLiquid |
                PhaseTransitionSummaryFlags.TouchesSolid, errors),
            AcceptanceScenarioMode.PhasePauseContinue => ValidatePause(materials, checkpoints, errors),
            AcceptanceScenarioMode.PhaseWakeGas => ValidateWake(mode, materials, checkpoints,
                "acceptance:wake_gas_source", "acceptance:wake_gas_target", errors),
            AcceptanceScenarioMode.PhaseWakeLiquid => ValidateWake(mode, materials, checkpoints,
                "acceptance:wake_liquid_source", "acceptance:wake_liquid_target", errors),
            AcceptanceScenarioMode.PhaseReadbackFallback => ValidateFallback(materials, snapshot, errors),
            AcceptanceScenarioMode.PhaseExternalReorder => ValidateExternalReorder(materials, checkpoints, errors),
            AcceptanceScenarioMode.PhaseDisabledRegistry => ValidateDisabledRegistry(
                materials, snapshot, phaseTiming, dispatches, fallbackWakeUps,
                maximumDispatchesPerFrame, summary, errors),
            AcceptanceScenarioMode.PhaseEnergyContract => ValidateEnergy(materials, checkpoints, errors),
            AcceptanceScenarioMode.PhaseV5RoundTrip => ValidateRoundTrip(materials, snapshot, checkpoints, errors),
            AcceptanceScenarioMode.PhasePerformanceSteady or AcceptanceScenarioMode.PhasePerformanceBurst =>
                ValidatePerformance(mode, materials, snapshot, thermalTiming, phaseTiming, errors),
            _ => 0
        };

        bool disabled = mode == AcceptanceScenarioMode.PhaseDisabledRegistry;
        bool fallback = mode == AcceptanceScenarioMode.PhaseReadbackFallback;
        int checkedMaximumDispatches = mode == AcceptanceScenarioMode.PhaseV5RoundTrip && checkpoints.Count > 0
            ? checkpoints[0].MaximumDispatchesPerFrame
            : maximumDispatchesPerFrame;
        ulong checkedFallbackWakeUps = mode == AcceptanceScenarioMode.PhaseV5RoundTrip && checkpoints.Count > 0
            ? checkpoints[0].FallbackWakeUps
            : fallbackWakeUps;
        if (fallback)
        {
            Require(summaryReadbacks >= 5,
                $"readback fallback completed too few real summaries={summaryReadbacks}", errors);
        }
        PhaseTransitionSummaryFlags observedSummary = mode == AcceptanceScenarioMode.PhaseV5RoundTrip && checkpoints.Count > 0
            ? checkpoints[0].Summary
            : summary;
        PhaseTransitionSummaryFlags expectedSummary = ExpectedSummary(mode);
        Require(observedSummary == expectedSummary,
            $"summary expected=0x{(uint)expectedSummary:X} actual=0x{(uint)observedSummary:X}", errors);
        if (!disabled)
        {
            Require(checkedMaximumDispatches == 1,
                $"maximumDispatchesPerFrame expected=1 actual={checkedMaximumDispatches}", errors);
            Require(presentationIsCurrent, "phase composition is stale", errors);
        }
        Require(checkedFallbackWakeUps == (fallback ? 2UL : 0UL),
            $"fallbackWakeUps expected={(fallback ? 2 : 0)} actual={checkedFallbackWakeUps}", errors);

        ulong reportedDispatches = dispatches;
        int reportedMaximum = maximumDispatchesPerFrame;
        PhaseTransitionSummaryFlags reportedSummary = summary;
        ulong reportedFallback = fallbackWakeUps;
        ulong reportedSummaryReadbacks = summaryReadbacks;
        if (mode == AcceptanceScenarioMode.PhaseV5RoundTrip && checkpoints.Count > 0)
        {
            reportedDispatches = checkpoints[0].PhaseDispatches;
            reportedMaximum = checkpoints[0].MaximumDispatchesPerFrame;
            reportedSummary = ExpectedNormalizationSummary;
            reportedFallback = checkpoints[0].FallbackWakeUps;
            reportedSummaryReadbacks = checkpoints[0].SummaryReadbacks;
        }

        bool passed = errors.Count == 0;
        string scenario = ToScenarioName(mode);
        string massTemperature = mode == AcceptanceScenarioMode.PhaseEnergyContract
            ? "MODEL_A_MASS_TEMPERATURE_PRESERVED"
            : passed ? "PRESERVED" : "FAILED";
        report = $"PHASE_ACCEPTANCE_RESULT scenario={scenario} passed={passed.ToString().ToLowerInvariant()} " +
            $"transitions={transitions} dispatches={reportedDispatches} maxPerFrame={reportedMaximum} " +
            $"summary=0x{(uint)reportedSummary:X} fallbackWakeUps={reportedFallback} " +
            $"summaryReadbacks={reportedSummaryReadbacks} timingSamples={phaseTiming.Samples} " +
            $"massTemperature={massTemperature}";
        if (!passed)
        {
            report += Environment.NewLine + "PHASE_ACCEPTANCE_FAILURE " + string.Join("; ", errors);
        }
        return passed;
    }

    private static int ValidateThresholds(
        MaterialRegistry materials,
        IReadOnlyList<PhaseAcceptanceCheckpoint> checkpoints,
        List<string> errors)
    {
        Require(checkpoints.Count == 1, $"threshold checkpoints expected=1 actual={checkpoints.Count}", errors);
        if (checkpoints.Count == 0) return 0;
        SimulationWorldSnapshot initial = Initial(AcceptanceScenarioMode.PhaseThresholds, checkpoints[0].Snapshot, materials);
        ReadOnlySpan<GridCell> before = Cells(initial);
        ReadOnlySpan<GridCell> after = Cells(checkpoints[0].Snapshot);
        string[] expectedIds =
        [
            "acceptance:threshold_below",
            "acceptance:threshold_source",
            "acceptance:threshold_source",
            "acceptance:threshold_source",
            "acceptance:threshold_above"
        ];
        int transitions = 0;
        for (int index = 0; index < expectedIds.Length; index++)
        {
            int cellIndex = 130 * initial.Width + 220 + index * 10;
            GridCell source = before[cellIndex];
            GridCell actual = after[cellIndex];
            uint expectedMaterial = materials.GetRequiredRuntimeIndex(expectedIds[index]);
            if (expectedMaterial == source.MaterialIndex)
            {
                Require(CellEquals(source, actual), $"threshold case {index} changed at equality/safe range", errors);
            }
            else
            {
                transitions++;
                ValidateNormalized(source, actual, materials, expectedMaterial, $"threshold case {index}", errors);
            }
        }
        int inactiveIndex = 130 * initial.Width + 280;
        Require(CellEquals(before[inactiveIndex], after[inactiveIndex]), "inactive threshold cell changed", errors);
        Require(checkpoints[0].PhaseDispatches == 1,
            $"threshold dispatches expected=1 actual={checkpoints[0].PhaseDispatches}", errors);
        return transitions;
    }

    private static int ValidateHysteresis(
        MaterialRegistry materials,
        IReadOnlyList<PhaseAcceptanceCheckpoint> checkpoints,
        List<string> errors)
    {
        Require(checkpoints.Count == 2, $"hysteresis checkpoints expected=2 actual={checkpoints.Count}", errors);
        if (checkpoints.Count < 2) return 0;
        string[] expected =
        [
            "acceptance:cold_solid", "acceptance:cold_solid",
            "acceptance:cold_liquid", "acceptance:cold_liquid",
            "acceptance:cold_liquid", "acceptance:cold_solid",
            "acceptance:hot_gas", "acceptance:hot_gas",
            "acceptance:hot_liquid", "acceptance:hot_liquid",
            "acceptance:hot_liquid", "acceptance:hot_gas"
        ];
        SimulationWorldSnapshot initial = Initial(AcceptanceScenarioMode.PhaseHysteresis, checkpoints[1].Snapshot, materials);
        ReadOnlySpan<GridCell> before = Cells(initial);
        ReadOnlySpan<GridCell> first = Cells(checkpoints[0].Snapshot);
        ReadOnlySpan<GridCell> stable = Cells(checkpoints[1].Snapshot);
        int transitions = 0;
        for (int index = 0; index < expected.Length; index++)
        {
            int cellIndex = 130 * initial.Width + 180 + index * 10;
            uint target = materials.GetRequiredRuntimeIndex(expected[index]);
            bool transitioned = before[cellIndex].MaterialIndex != target;
            transitions += transitioned ? 1 : 0;
            if (transitioned)
            {
                ValidateNormalized(before[cellIndex], first[cellIndex], materials, target,
                    $"hysteresis case {index}", errors);
            }
            else
            {
                Require(CellEquals(before[cellIndex], first[cellIndex]),
                    $"hysteresis stable case {index} changed fields", errors);
            }
            Require(first[cellIndex].MaterialIndex == target,
                $"hysteresis case {index} first target expected={target} actual={first[cellIndex].MaterialIndex}", errors);
            Require(first[cellIndex].MaterialIndex == stable[cellIndex].MaterialIndex,
                $"hysteresis case {index} oscillated after first pass", errors);
            Require(SameBits(before[cellIndex].Mass, stable[cellIndex].Mass) &&
                SameBits(before[cellIndex].Temperature, stable[cellIndex].Temperature),
                $"hysteresis case {index} lost mass/temperature", errors);
        }
        Require(checkpoints[1].PhaseDispatches == 5,
            $"hysteresis dispatches expected=5 actual={checkpoints[1].PhaseDispatches}", errors);
        return transitions;
    }

    private static int ValidateSingleTransition(
        MaterialRegistry materials,
        IReadOnlyList<PhaseAcceptanceCheckpoint> checkpoints,
        List<string> errors)
    {
        Require(checkpoints.Count == 2, $"single-transition checkpoints expected=2 actual={checkpoints.Count}", errors);
        if (checkpoints.Count < 2) return 0;
        GridCell first = Cell(checkpoints[0].Snapshot, 240, 135);
        GridCell second = Cell(checkpoints[1].Snapshot, 240, 135);
        GridCell source = Cell(Initial(AcceptanceScenarioMode.PhaseSingleTransition,
            checkpoints[0].Snapshot, materials), 240, 135);
        ValidateNormalized(source, first, materials,
            materials.GetRequiredRuntimeIndex("acceptance:chain_b"), "chain_a to chain_b", errors);
        ValidateNormalized(first, second, materials,
            materials.GetRequiredRuntimeIndex("acceptance:chain_c"), "chain_b to chain_c", errors);
        Require(first.MaterialIndex == materials.GetRequiredRuntimeIndex("acceptance:chain_b"),
            $"first dispatch target is {materials[first.MaterialIndex].Id}, expected chain_b", errors);
        Require(first.MaterialIndex != materials.GetRequiredRuntimeIndex("acceptance:chain_c"),
            "chain_c appeared during first dispatch", errors);
        Require(second.MaterialIndex == materials.GetRequiredRuntimeIndex("acceptance:chain_c"),
            $"second dispatch target is {materials[second.MaterialIndex].Id}, expected chain_c", errors);
        Require(checkpoints[0].ThermalTicksInFrame == 4,
            $"catch-up thermal ticks expected=4 actual={checkpoints[0].ThermalTicksInFrame}", errors);
        Require(checkpoints[0].PhaseDispatches == 1 && checkpoints[1].PhaseDispatches == 2,
            $"phase checkpoints dispatches expected=1/2 actual={checkpoints[0].PhaseDispatches}/{checkpoints[1].PhaseDispatches}", errors);
        Require(checkpoints[0].MaximumDispatchesPerFrame == 1,
            $"catch-up phase max expected=1 actual={checkpoints[0].MaximumDispatchesPerFrame}", errors);
        Require(SameBits(first.Mass, second.Mass) && SameBits(first.Temperature, second.Temperature),
            "chain mass/temperature changed", errors);
        return 2;
    }

    private static int ValidateNormalizationMatrix(
        MaterialRegistry materials,
        IReadOnlyList<PhaseAcceptanceCheckpoint> checkpoints,
        List<string> errors)
    {
        Require(checkpoints.Count >= 1, "normalization checkpoint missing", errors);
        if (checkpoints.Count == 0) return 0;
        SimulationWorldSnapshot initial = Initial(
            AcceptanceScenarioMode.PhaseNormalizationMatrix, checkpoints[0].Snapshot, materials);
        string[] targets =
        [
            "acceptance:norm_gas", "acceptance:norm_liquid", "acceptance:norm_liquid",
            "acceptance:norm_liquid", "acceptance:norm_granular", "acceptance:norm_fixed",
            "acceptance:norm_liquid", "acceptance:norm_movable"
        ];
        for (int index = 0; index < targets.Length; index++)
        {
            GridCell source = Cell(initial, 205 + index * 10, 135);
            GridCell actual = Cell(checkpoints[0].Snapshot, 205 + index * 10, 135);
            ValidateNormalized(source, actual, materials,
                materials.GetRequiredRuntimeIndex(targets[index]), $"normalization pair {index + 1}", errors);
        }
        return targets.Length;
    }

    private static int ValidateIsolatedSummary(
        AcceptanceScenarioMode mode,
        MaterialRegistry materials,
        IReadOnlyList<PhaseAcceptanceCheckpoint> checkpoints,
        string sourceId,
        string targetId,
        PhaseTransitionSummaryFlags expectedSummary,
        List<string> errors)
    {
        Require(checkpoints.Count == 1, $"isolated summary checkpoints expected=1 actual={checkpoints.Count}", errors);
        if (checkpoints.Count == 0) return 0;
        SimulationWorldSnapshot initial = Initial(mode, checkpoints[0].Snapshot, materials);
        GridCell source = Cell(initial, 240, 135);
        Require(source.MaterialIndex == materials.GetRequiredRuntimeIndex(sourceId), "isolated summary source mismatch", errors);
        ValidateNormalized(source, Cell(checkpoints[0].Snapshot, 240, 135), materials,
            materials.GetRequiredRuntimeIndex(targetId), "isolated summary cell", errors);
        Require(checkpoints[0].PhaseDispatches == 1,
            $"isolated summary dispatches expected=1 actual={checkpoints[0].PhaseDispatches}", errors);
        // The asynchronous readback may complete after the immediate snapshot; the final summary is checked by Validate.
        Require(expectedSummary == PhaseTransitionRuntime.GetSummaryFlags(
            materials[sourceId].Properties, materials[targetId].Properties),
            "CPU expected summary definition mismatch", errors);
        return 1;
    }

    private static int ValidatePause(
        MaterialRegistry materials,
        IReadOnlyList<PhaseAcceptanceCheckpoint> checkpoints,
        List<string> errors)
    {
        Require(checkpoints.Count == 2, $"pause checkpoints expected=2 actual={checkpoints.Count}", errors);
        if (checkpoints.Count < 2) return 0;
        SimulationWorldSnapshot initial = Initial(AcceptanceScenarioMode.PhasePauseContinue,
            checkpoints[0].Snapshot, materials);
        GridCell before = Cell(initial, 240, 135);
        GridCell paused = Cell(checkpoints[0].Snapshot, 240, 135);
        GridCell transitioned = Cell(checkpoints[1].Snapshot, 240, 135);
        GridCell expectedPaused = before;
        expectedPaused.Temperature = 110;
        Require(CellEquals(expectedPaused, paused), "temperature brush changed a non-temperature field on Pause", errors);
        Require(checkpoints[0].ThermalTicks == 0 && checkpoints[0].PhaseDispatches == 0,
            $"Pause advanced thermal/phase={checkpoints[0].ThermalTicks}/{checkpoints[0].PhaseDispatches}", errors);
        ValidateNormalized(paused, transitioned, materials,
            materials.GetRequiredRuntimeIndex("acceptance:pause_target"), "pause continue target", errors);
        Require(checkpoints[1].PhaseDispatches == 1 && checkpoints[1].ThermalTicksInFrame == 1,
            $"Continue expected one thermal/phase dispatch, got ticksInFrame={checkpoints[1].ThermalTicksInFrame} phase={checkpoints[1].PhaseDispatches}", errors);
        return 1;
    }

    private static int ValidateWake(
        AcceptanceScenarioMode mode,
        MaterialRegistry materials,
        IReadOnlyList<PhaseAcceptanceCheckpoint> checkpoints,
        string sourceId,
        string targetId,
        List<string> errors)
    {
        Require(checkpoints.Count == 3, $"wake checkpoints expected=3 actual={checkpoints.Count}", errors);
        if (checkpoints.Count < 3) return 0;
        SimulationWorldSnapshot initial = Initial(mode, checkpoints[0].Snapshot, materials);
        Require(SnapshotsEqual(initial, checkpoints[0].Snapshot), "pre-transition wake snapshot changed while paused", errors);
        uint target = materials.GetRequiredRuntimeIndex(targetId);
        (int left, int top, int right, int bottom) = mode == AcceptanceScenarioMode.PhaseWakeGas
            ? (232, 150, 247, 157)
            : (232, 70, 247, 77);
        int immediateCount = 0;
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                GridCell source = Cell(initial, x, y);
                GridCell immediate = Cell(checkpoints[1].Snapshot, x, y);
                Require(source.MaterialIndex == materials.GetRequiredRuntimeIndex(sourceId),
                    $"wake source missing at {x},{y}", errors);
                ValidateNormalized(source, immediate, materials, target, $"wake immediate {x},{y}", errors);
                immediateCount += immediate.MaterialIndex == target ? 1 : 0;
            }
        }
        (int finalCells, double finalMass, int outside) = MeasureMaterial(
            checkpoints[2].Snapshot, target, left, top, right, bottom);
        Require(immediateCount == 128, $"wake immediate target count expected=128 actual={immediateCount}", errors);
        Require(finalCells > 0 && Math.Abs(finalMass - 128) <= 0.01,
            $"wake final cells/mass invalid={finalCells}/{finalMass:R}", errors);
        Require(outside > 0, "transitioned cellular material did not move outside its source region", errors);
        Require(checkpoints[1].PhaseDispatches >= 1,
            "wake transition did not dispatch", errors);
        return 128;
    }

    private static int ValidateFallback(MaterialRegistry materials, SimulationWorldSnapshot snapshot, List<string> errors)
    {
        uint target = materials.GetRequiredRuntimeIndex("acceptance:phase_target");
        int count = 0;
        foreach (GridCell cell in Cells(snapshot))
        {
            if (cell.IsActive == 0 || cell.MaterialIndex != target) continue;
            count++;
            Require(cell.Mass == 2 && cell.Temperature == 150 &&
                cell.VelocityX == 0 && cell.VelocityY == 0 && cell.Pressure == 0 &&
                cell.BodyId == 0 && cell.RestFrames == 2,
                "fallback transitioned cell normalization mismatch", errors);
        }
        Require(count == 256, $"fallback target cells expected=256 actual={count}", errors);
        return count;
    }

    private static int ValidateExternalReorder(
        MaterialRegistry materials,
        IReadOnlyList<PhaseAcceptanceCheckpoint> checkpoints,
        List<string> errors)
    {
        Require(checkpoints.Count == 1, $"external reorder checkpoints expected=1 actual={checkpoints.Count}", errors);
        if (checkpoints.Count == 0) return 0;
        SimulationWorldSnapshot initial = Initial(AcceptanceScenarioMode.PhaseExternalReorder,
            checkpoints[0].Snapshot, materials);
        uint target = materials.GetRequiredRuntimeIndex("acceptance:external_target");
        int count = 0;
        for (int y = 120; y <= 135; y++)
        {
            for (int x = 232; x <= 247; x++)
            {
                ValidateNormalized(Cell(initial, x, y), Cell(checkpoints[0].Snapshot, x, y),
                    materials, target, $"external reorder {x},{y}", errors);
                count++;
            }
        }
        Require(materials[target].Id == "acceptance:external_target", "runtime target resolved to wrong string ID", errors);
        Console.WriteLine($"PHYXEL_PHASE_REORDER targetId=acceptance:external_target targetRuntimeIndex={target}");
        return count;
    }

    private static int ValidateDisabledRegistry(
        MaterialRegistry materials,
        SimulationWorldSnapshot snapshot,
        ThermalGpuTimingStatistics phaseTiming,
        ulong dispatches,
        ulong fallbackWakeUps,
        int maximumDispatchesPerFrame,
        PhaseTransitionSummaryFlags summary,
        List<string> errors)
    {
        Require(!materials.RegistryHasPhaseTransitions, "disabled registry reports transitions", errors);
        Require(dispatches == 0 && maximumDispatchesPerFrame == 0,
            $"disabled registry phase dispatches/max={dispatches}/{maximumDispatchesPerFrame}", errors);
        Require(fallbackWakeUps == 0 && summary == PhaseTransitionSummaryFlags.None && phaseTiming.Samples == 0,
            $"disabled registry fallback/summary/timing={fallbackWakeUps}/0x{(uint)summary:X}/{phaseTiming.Samples}", errors);
        GridCell hot = Cell(snapshot, 239, 135);
        GridCell cold = Cell(snapshot, 240, 135);
        SimulationWorldSnapshot initial = Initial(AcceptanceScenarioMode.PhaseDisabledRegistry, snapshot, materials);
        GridCell initialHot = Cell(initial, 239, 135);
        GridCell initialCold = Cell(initial, 240, 135);
        Require(SameNonTemperatureFields(initialHot, hot) && SameNonTemperatureFields(initialCold, cold),
            "disabled registry thermal pass changed non-temperature fields", errors);
        Require(hot.Temperature < 400 && cold.Temperature > 0 && hot.Temperature > cold.Temperature,
            $"thermal diffusion did not continue={hot.Temperature:R}/{cold.Temperature:R}", errors);
        Require(Math.Abs(hot.Temperature + cold.Temperature - 400) <= 0.05,
            $"disabled registry thermal energy changed sum={hot.Temperature + cold.Temperature:R}", errors);
        return 0;
    }

    private static int ValidateEnergy(
        MaterialRegistry materials,
        IReadOnlyList<PhaseAcceptanceCheckpoint> checkpoints,
        List<string> errors)
    {
        Require(checkpoints.Count == 1, $"energy checkpoints expected=1 actual={checkpoints.Count}", errors);
        if (checkpoints.Count == 0) return 0;
        SimulationWorldSnapshot initial = Initial(AcceptanceScenarioMode.PhaseEnergyContract,
            checkpoints[0].Snapshot, materials);
        GridCell source = Cell(initial, 240, 135);
        GridCell target = Cell(checkpoints[0].Snapshot, 240, 135);
        uint targetIndex = materials.GetRequiredRuntimeIndex("acceptance:energy_target");
        ValidateNormalized(source, target, materials, targetIndex, "energy contract", errors);
        double sourceEnergy = source.Mass * source.Temperature * materials[source.MaterialIndex].Properties.HeatCapacity;
        double targetEnergy = target.Mass * target.Temperature * materials[target.MaterialIndex].Properties.HeatCapacity;
        Require(sourceEnergy == 275 && targetEnergy == 1100,
            $"MODEL_A sensible energy expected=275/1100 actual={sourceEnergy:R}/{targetEnergy:R}", errors);
        Require(sourceEnergy != targetEnergy, "MODEL_A test incorrectly conserved sensible energy", errors);
        return 1;
    }

    private static int ValidateRoundTrip(
        MaterialRegistry materials,
        SimulationWorldSnapshot snapshot,
        IReadOnlyList<PhaseAcceptanceCheckpoint> checkpoints,
        List<string> errors)
    {
        Require(checkpoints.Count == 1, $"round-trip checkpoints expected=1 actual={checkpoints.Count}", errors);
        if (checkpoints.Count == 0) return 0;
        Require(SnapshotsEqual(checkpoints[0].Snapshot, snapshot), "v5 GPU snapshot changed after save/clear/load", errors);
        string[] expectedTargets =
        [
            "acceptance:norm_gas", "acceptance:norm_liquid", "acceptance:norm_liquid",
            "acceptance:norm_liquid", "acceptance:norm_granular", "acceptance:norm_fixed",
            "acceptance:norm_liquid", "acceptance:norm_movable"
        ];
        for (int index = 0; index < expectedTargets.Length; index++)
        {
            GridCell cell = Cell(snapshot, 205 + index * 10, 135);
            Require(materials[cell.MaterialIndex].Id == expectedTargets[index],
                $"v5 target ID {index} expected={expectedTargets[index]} actual={materials[cell.MaterialIndex].Id}", errors);
        }
        return expectedTargets.Length;
    }

    private static int ValidatePerformance(
        AcceptanceScenarioMode mode,
        MaterialRegistry materials,
        SimulationWorldSnapshot snapshot,
        ThermalGpuTimingStatistics thermalTiming,
        ThermalGpuTimingStatistics phaseTiming,
        List<string> errors)
    {
        uint expected = materials.GetRequiredRuntimeIndex(mode == AcceptanceScenarioMode.PhasePerformanceBurst
            ? "acceptance:perf_target"
            : "acceptance:perf_source");
        int expectedCount = checked(snapshot.Width * snapshot.Height);
        int actualCount = 0;
        foreach (GridCell cell in Cells(snapshot))
        {
            bool expectedCell = cell.IsActive != 0 && cell.MaterialIndex == expected;
            actualCount += expectedCell ? 1 : 0;
            if (expectedCell)
            {
                float expectedTemperature = mode == AcceptanceScenarioMode.PhasePerformanceBurst ? 110 : 20;
                Require(cell.Mass == 1 && cell.Temperature == expectedTemperature &&
                    cell.VelocityX == 0 && cell.VelocityY == 0 && cell.Pressure == 0 &&
                    cell.IsActive == 1 && cell.BodyId == 0 && cell.RestFrames == 2,
                    "performance cell field contract mismatch", errors);
            }
        }
        Require(actualCount == expectedCount,
            $"performance material count expected={expectedCount} actual={actualCount}", errors);
        Require(phaseTiming.Samples > 0 && thermalTiming.Samples > 0,
            $"performance timing samples phase/thermal={phaseTiming.Samples}/{thermalTiming.Samples}", errors);
        double ratio = thermalTiming.AverageMilliseconds > 0
            ? phaseTiming.AverageMilliseconds / thermalTiming.AverageMilliseconds
            : 0;
        Console.WriteLine(
            $"PHYXEL_PHASE_PERFORMANCE state={(mode == AcceptanceScenarioMode.PhasePerformanceBurst ? "burst" : "steady")} " +
            $"resolution={snapshot.Width}x{snapshot.Height} samples={phaseTiming.Samples} " +
            $"average={phaseTiming.AverageMilliseconds:0.000000} minimum={phaseTiming.MinimumMilliseconds:0.000000} " +
            $"maximum={phaseTiming.MaximumMilliseconds:0.000000} " +
            $"thermalAverage={thermalTiming.AverageMilliseconds:0.000000} ratio={ratio:0.000000}");
        return mode == AcceptanceScenarioMode.PhasePerformanceBurst ? actualCount : 0;
    }

    private static void ValidateNormalized(
        GridCell source,
        GridCell actual,
        MaterialRegistry materials,
        uint targetIndex,
        string label,
        List<string> errors)
    {
        GridCell expected = PhaseTransitionRuntime.Normalize(
            source,
            materials[source.MaterialIndex].Properties,
            materials[targetIndex].Properties,
            targetIndex);
        Require(CellEquals(expected, actual), $"{label} fields expected={Describe(expected)} actual={Describe(actual)}", errors);
        Require(SameBits(source.Mass, actual.Mass), $"{label} mass bits changed", errors);
        Require(SameBits(source.Temperature, actual.Temperature), $"{label} temperature bits changed", errors);
    }

    private static SimulationWorldSnapshot Initial(
        AcceptanceScenarioMode mode,
        SimulationWorldSnapshot dimensions,
        MaterialRegistry materials) =>
        PhaseAcceptanceScenario.Create(mode, dimensions.Width, dimensions.Height, materials) ??
        throw new InvalidOperationException($"Missing initial phase fixture for {mode}.");

    private static (int Cells, double Mass, int Outside) MeasureMaterial(
        SimulationWorldSnapshot snapshot,
        uint material,
        int left,
        int top,
        int right,
        int bottom)
    {
        int count = 0;
        int outside = 0;
        double mass = 0;
        ReadOnlySpan<GridCell> cells = Cells(snapshot);
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = cells[y * snapshot.Width + x];
                if (cell.IsActive == 0 || cell.MaterialIndex != material) continue;
                count++;
                mass += cell.Mass;
                outside += x < left || x > right || y < top || y > bottom ? 1 : 0;
            }
        }
        return (count, mass, outside);
    }

    private static GridCell Cell(SimulationWorldSnapshot snapshot, int x, int y) =>
        Cells(snapshot)[y * snapshot.Width + x];

    private static ReadOnlySpan<GridCell> Cells(SimulationWorldSnapshot snapshot) =>
        MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);

    private static bool SnapshotsEqual(SimulationWorldSnapshot left, SimulationWorldSnapshot right) =>
        left.Width == right.Width && left.Height == right.Height && left.Grid.AsSpan().SequenceEqual(right.Grid);

    private static bool SameBits(float left, float right) =>
        BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);

    private static bool CellEquals(GridCell left, GridCell right) =>
        left.MaterialIndex == right.MaterialIndex &&
        SameBits(left.Mass, right.Mass) &&
        SameBits(left.VelocityX, right.VelocityX) &&
        SameBits(left.VelocityY, right.VelocityY) &&
        SameBits(left.Pressure, right.Pressure) &&
        left.IsActive == right.IsActive &&
        left.BodyId == right.BodyId &&
        left.RestFrames == right.RestFrames &&
        SameBits(left.Temperature, right.Temperature);

    private static bool SameNonTemperatureFields(GridCell left, GridCell right) =>
        left.MaterialIndex == right.MaterialIndex &&
        SameBits(left.Mass, right.Mass) &&
        SameBits(left.VelocityX, right.VelocityX) &&
        SameBits(left.VelocityY, right.VelocityY) &&
        SameBits(left.Pressure, right.Pressure) &&
        left.IsActive == right.IsActive &&
        left.BodyId == right.BodyId &&
        left.RestFrames == right.RestFrames;

    private static string Describe(GridCell cell) =>
        $"{cell.MaterialIndex}/{cell.Mass:R}/{cell.Temperature:R}/" +
        $"{cell.VelocityX:R}/{cell.VelocityY:R}/{cell.Pressure:R}/" +
        $"{cell.IsActive}/{cell.BodyId}/{cell.RestFrames}";

    private static void Require(bool condition, string error, List<string> errors)
    {
        if (!condition)
        {
            errors.Add(error);
        }
    }

    private static string ToScenarioName(AcceptanceScenarioMode mode) => mode switch
    {
        AcceptanceScenarioMode.PhaseThresholds => "phase_thresholds",
        AcceptanceScenarioMode.PhaseHysteresis => "phase_hysteresis",
        AcceptanceScenarioMode.PhaseSingleTransition => "phase_single_transition",
        AcceptanceScenarioMode.PhaseNormalizationMatrix => "phase_normalization_matrix",
        AcceptanceScenarioMode.PhaseSummaryLiquidGas => "phase_summary_liquid_gas",
        AcceptanceScenarioMode.PhaseSummarySolidLiquid => "phase_summary_solid_liquid",
        AcceptanceScenarioMode.PhaseSummaryGasMovable => "phase_summary_gas_movable",
        AcceptanceScenarioMode.PhaseSummaryLiquidFixed => "phase_summary_liquid_fixed",
        AcceptanceScenarioMode.PhasePauseContinue => "phase_pause_continue",
        AcceptanceScenarioMode.PhaseWakeGas => "phase_wake_gas",
        AcceptanceScenarioMode.PhaseWakeLiquid => "phase_wake_liquid",
        AcceptanceScenarioMode.PhaseReadbackFallback => "phase_readback_fallback",
        AcceptanceScenarioMode.PhaseExternalReorder => "phase_external_reorder",
        AcceptanceScenarioMode.PhaseDisabledRegistry => "phase_disabled_registry",
        AcceptanceScenarioMode.PhaseEnergyContract => "phase_energy_contract",
        AcceptanceScenarioMode.PhaseV5RoundTrip => "phase_v5_roundtrip",
        AcceptanceScenarioMode.PhasePerformanceSteady => "phase_performance_steady",
        AcceptanceScenarioMode.PhasePerformanceBurst => "phase_performance_burst",
        _ => mode.ToString().ToLowerInvariant()
    };

    private static PhaseTransitionSummaryFlags ExpectedSummary(AcceptanceScenarioMode mode) => mode switch
    {
        AcceptanceScenarioMode.PhaseThresholds or
        AcceptanceScenarioMode.PhaseSingleTransition or
        AcceptanceScenarioMode.PhaseExternalReorder or
        AcceptanceScenarioMode.PhaseEnergyContract or
        AcceptanceScenarioMode.PhasePerformanceBurst =>
            PhaseTransitionSummaryFlags.PhaseOccurred |
            PhaseTransitionSummaryFlags.TouchesSolid,
        AcceptanceScenarioMode.PhaseHysteresis =>
            PhaseTransitionSummaryFlags.PhaseOccurred |
            PhaseTransitionSummaryFlags.TargetCellular |
            PhaseTransitionSummaryFlags.TargetLiquid |
            PhaseTransitionSummaryFlags.TargetGas |
            PhaseTransitionSummaryFlags.TouchesLiquid |
            PhaseTransitionSummaryFlags.TouchesSolid,
        AcceptanceScenarioMode.PhaseNormalizationMatrix or AcceptanceScenarioMode.PhaseV5RoundTrip =>
            ExpectedNormalizationSummary,
        AcceptanceScenarioMode.PhaseSummaryLiquidGas =>
            PhaseTransitionSummaryFlags.PhaseOccurred |
            PhaseTransitionSummaryFlags.TargetCellular |
            PhaseTransitionSummaryFlags.TargetGas |
            PhaseTransitionSummaryFlags.TouchesLiquid,
        AcceptanceScenarioMode.PhaseSummarySolidLiquid or AcceptanceScenarioMode.PhaseWakeLiquid =>
            PhaseTransitionSummaryFlags.PhaseOccurred |
            PhaseTransitionSummaryFlags.TargetCellular |
            PhaseTransitionSummaryFlags.TargetLiquid |
            PhaseTransitionSummaryFlags.TouchesLiquid |
            PhaseTransitionSummaryFlags.TouchesSolid,
        AcceptanceScenarioMode.PhaseSummaryGasMovable =>
            PhaseTransitionSummaryFlags.PhaseOccurred |
            PhaseTransitionSummaryFlags.TouchesSolid |
            PhaseTransitionSummaryFlags.TargetMovableSolid,
        AcceptanceScenarioMode.PhaseSummaryLiquidFixed or AcceptanceScenarioMode.PhaseReadbackFallback =>
            PhaseTransitionSummaryFlags.PhaseOccurred |
            PhaseTransitionSummaryFlags.TouchesLiquid |
            PhaseTransitionSummaryFlags.TouchesSolid,
        AcceptanceScenarioMode.PhasePauseContinue or AcceptanceScenarioMode.PhaseWakeGas =>
            PhaseTransitionSummaryFlags.PhaseOccurred |
            PhaseTransitionSummaryFlags.TargetCellular |
            PhaseTransitionSummaryFlags.TargetGas |
            PhaseTransitionSummaryFlags.TouchesSolid,
        _ => PhaseTransitionSummaryFlags.None
    };

    private const PhaseTransitionSummaryFlags ExpectedNormalizationSummary =
        PhaseTransitionSummaryFlags.PhaseOccurred |
        PhaseTransitionSummaryFlags.TargetCellular |
        PhaseTransitionSummaryFlags.TargetLiquid |
        PhaseTransitionSummaryFlags.TargetGas |
        PhaseTransitionSummaryFlags.TouchesLiquid |
        PhaseTransitionSummaryFlags.TouchesSolid |
        PhaseTransitionSummaryFlags.TargetMovableSolid;
}
