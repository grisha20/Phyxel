using System;
using System.IO;
using System.Runtime.InteropServices;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

internal static class PhaseTransitionRuntimeRegressionVerifier
{
    public static int Run()
    {
        try
        {
            VerifyLayoutsAndDeclarations();
            VerifyPredicatesAndPriority();
            VerifyNormalizationMatrix();
            VerifySummaryFlags();
            VerifyDispatchPolicyAndFallback();
            Console.WriteLine("PHYXEL_PHASE_RUNTIME_SUCCESS");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"PHYXEL_PHASE_RUNTIME_FAILED {exception}");
            return 1;
        }
    }

    private static void VerifyLayoutsAndDeclarations()
    {
        Require(Marshal.SizeOf<GridCell>() == 40, "GridCell must be 40 bytes.");
        Require(Marshal.SizeOf<MaterialProperties>() == 128, "MaterialProperties must be 128 bytes.");
        Require(Marshal.SizeOf<PhaseTransitionConstants>() == 32,
            "PhaseTransitionConstants must be 32 bytes.");
        Require((uint)PhaseTransitionSummaryFlags.PhaseOccurred == 1u << 0 &&
            (uint)PhaseTransitionSummaryFlags.TargetCellular == 1u << 1 &&
            (uint)PhaseTransitionSummaryFlags.TargetLiquid == 1u << 2 &&
            (uint)PhaseTransitionSummaryFlags.TargetGas == 1u << 3 &&
            (uint)PhaseTransitionSummaryFlags.TouchesLiquid == 1u << 4 &&
            (uint)PhaseTransitionSummaryFlags.TouchesSolid == 1u << 5 &&
            (uint)PhaseTransitionSummaryFlags.TargetMovableSolid == 1u << 6,
            "C# phase-summary bit layout changed.");

        string shaderDirectory = Path.Combine(AppContext.BaseDirectory, "Content", "Shaders");
        string shared = File.ReadAllText(Path.Combine(shaderDirectory, "PhysicsShared.hlsli"));
        string shader = File.ReadAllText(Path.Combine(shaderDirectory, "PhaseTransitions.hlsl"));
        RequireOrdered(shader, "cbuffer PhaseConstants", "uint PhaseWidth;", "uint PhaseHeight;",
            "uint MaterialCount;", "uint PhaseTickIndex;", "uint PhaseTickCount;");
        string[] sharedFlags =
        [
            "PhaseSummaryPhaseOccurred = 1u << 0",
            "PhaseSummaryTargetCellular = 1u << 1",
            "PhaseSummaryTargetLiquid = 1u << 2",
            "PhaseSummaryTargetGas = 1u << 3",
            "PhaseSummaryTouchesLiquid = 1u << 4",
            "PhaseSummaryTouchesSolid = 1u << 5",
            "PhaseSummaryTargetMovableSolid = 1u << 6"
        ];
        foreach (string declaration in sharedFlags)
        {
            Require(shared.Contains(declaration, StringComparison.Ordinal),
                $"HLSL summary declaration '{declaration}' is missing.");
        }
        Require(shader.Contains("RWStructuredBuffer<GridCell> Grid", StringComparison.Ordinal) &&
            shader.Contains("InterlockedOr(PhaseSummary[0]", StringComparison.Ordinal),
            "Phase shader is not an in-place pass with one summary atomic.");
        Require(!shader.Contains("Swap", StringComparison.Ordinal),
            "Phase shader must not swap cells.");
        Require(shader.Contains("HasCoherentGasForCondensation", StringComparison.Ordinal) &&
            shader.Contains("GasCondensationNeighborhoodMass", StringComparison.Ordinal) &&
            shader.Contains("source.SimulationKind == SimulationKindGas", StringComparison.Ordinal) &&
            shader.Contains("target.SimulationKind == SimulationKindLiquid", StringComparison.Ordinal),
            "Gas-to-liquid transitions do not reject isolated dilute gas tails.");
        Require(shader.Contains("cell.Temperature < source.TransitionBelowTemperature", StringComparison.Ordinal) &&
            shader.Contains("cell.Temperature > source.TransitionAboveTemperature", StringComparison.Ordinal),
            "Phase shader thresholds are not strict.");
    }

    private static void VerifyPredicatesAndPriority()
    {
        MaterialProperties source = Properties(MaterialSimulationKind.Granular);
        source.TransitionBelowTemperature = 0;
        source.TransitionBelowMaterialIndex = 2;
        source.TransitionAboveTemperature = 100;
        source.TransitionAboveMaterialIndex = 3;
        Require(PhaseTransitionRuntime.SelectTarget(-1, source) == 2, "Below predicate failed.");
        Require(PhaseTransitionRuntime.SelectTarget(0, source) == uint.MaxValue,
            "Exact below threshold transitioned.");
        Require(PhaseTransitionRuntime.SelectTarget(100, source) == uint.MaxValue,
            "Exact above threshold transitioned.");
        Require(PhaseTransitionRuntime.SelectTarget(101, source) == 3, "Above predicate failed.");

        source.TransitionBelowTemperature = 10;
        source.TransitionAboveTemperature = 0;
        Require(PhaseTransitionRuntime.SelectTarget(5, source) == 2,
            "Below-first priority no longer limits a cell to one transition per pass.");

        MaterialProperties[] materials =
        [
            Properties(MaterialSimulationKind.None),
            source,
            Properties(MaterialSimulationKind.Liquid),
            Properties(MaterialSimulationKind.Gas),
            Properties(MaterialSimulationKind.Tool)
        ];
        GridCell inactive = Cell(1, 5);
        inactive.IsActive = 0;
        Require(!PhaseTransitionRuntime.TryApply(ref inactive, materials, out _),
            "Inactive cell transitioned.");
        GridCell invalidSource = Cell(99, 5);
        Require(!PhaseTransitionRuntime.TryApply(ref invalidSource, materials, out _),
            "Out-of-range source transitioned.");
        source.TransitionBelowMaterialIndex = 0;
        materials[1] = source;
        GridCell emptyTarget = Cell(1, -1);
        Require(!PhaseTransitionRuntime.TryApply(ref emptyTarget, materials, out _),
            "core:empty target was accepted.");
        source.TransitionBelowMaterialIndex = 4;
        materials[1] = source;
        GridCell toolTarget = Cell(1, -1);
        Require(!PhaseTransitionRuntime.TryApply(ref toolTarget, materials, out _),
            "Tool target was accepted.");
        source.TransitionBelowMaterialIndex = 99;
        materials[1] = source;
        GridCell outOfRangeTarget = Cell(1, -1);
        Require(!PhaseTransitionRuntime.TryApply(ref outOfRangeTarget, materials, out _),
            "Out-of-range target was accepted.");
    }

    private static void VerifyNormalizationMatrix()
    {
        MaterialSimulationKind[] sourceKinds =
        [
            MaterialSimulationKind.Granular,
            MaterialSimulationKind.Liquid,
            MaterialSimulationKind.Gas,
            MaterialSimulationKind.Solid
        ];
        (MaterialSimulationKind Kind, MaterialFlags Flags)[] targets =
        [
            (MaterialSimulationKind.Granular, MaterialFlags.None),
            (MaterialSimulationKind.Liquid, MaterialFlags.None),
            (MaterialSimulationKind.Gas, MaterialFlags.None),
            (MaterialSimulationKind.Solid, MaterialFlags.None),
            (MaterialSimulationKind.Solid, MaterialFlags.MovableSolid)
        ];
        foreach (MaterialSimulationKind sourceKind in sourceKinds)
        {
            foreach ((MaterialSimulationKind targetKind, MaterialFlags targetFlags) in targets)
            {
                GridCell input = Cell(1, 12.5f);
                input.Mass = BitConverter.Int32BitsToSingle(unchecked((int)0x3f812345));
                input.VelocityX = 11;
                input.VelocityY = -7;
                input.Pressure = 19;
                input.BodyId = 777;
                input.RestFrames = 91;
                int massBits = BitConverter.SingleToInt32Bits(input.Mass);
                int temperatureBits = BitConverter.SingleToInt32Bits(input.Temperature);
                MaterialProperties source = Properties(sourceKind);
                MaterialProperties target = Properties(targetKind, targetFlags);
                GridCell output = PhaseTransitionRuntime.Normalize(input, source, target, 8);

                bool preserveVelocity = PhaseTransitionRuntime.IsCellular(sourceKind) &&
                    PhaseTransitionRuntime.IsCellular(targetKind);
                bool preservePressure = sourceKind == MaterialSimulationKind.Liquid &&
                    targetKind == MaterialSimulationKind.Liquid;
                bool fixedSolid = targetKind == MaterialSimulationKind.Solid &&
                    targetFlags != MaterialFlags.MovableSolid;
                Require(output.MaterialIndex == 8 && output.IsActive == 1 && output.BodyId == 0,
                    $"Identity normalization failed for {sourceKind}->{targetKind}/{targetFlags}.");
                Require(BitConverter.SingleToInt32Bits(output.Mass) == massBits &&
                    BitConverter.SingleToInt32Bits(output.Temperature) == temperatureBits,
                    $"Mass or temperature bits changed for {sourceKind}->{targetKind}/{targetFlags}.");
                Require((output.VelocityX == 11 && output.VelocityY == -7) == preserveVelocity,
                    $"Velocity normalization failed for {sourceKind}->{targetKind}/{targetFlags}.");
                Require((output.Pressure == 19) == preservePressure,
                    $"Pressure normalization failed for {sourceKind}->{targetKind}/{targetFlags}.");
                Require(output.RestFrames == (fixedSolid ? 2u : 0u),
                    $"RestFrames normalization failed for {sourceKind}->{targetKind}/{targetFlags}.");
            }
        }
    }

    private static void VerifySummaryFlags()
    {
        PhaseTransitionSummaryFlags liquidToGas = PhaseTransitionRuntime.GetSummaryFlags(
            Properties(MaterialSimulationKind.Liquid), Properties(MaterialSimulationKind.Gas));
        Require(liquidToGas == (PhaseTransitionSummaryFlags.PhaseOccurred |
            PhaseTransitionSummaryFlags.TargetCellular |
            PhaseTransitionSummaryFlags.TargetGas |
            PhaseTransitionSummaryFlags.TouchesLiquid),
            "Liquid-to-gas summary flags are incorrect.");
        PhaseTransitionSummaryFlags gasToMovable = PhaseTransitionRuntime.GetSummaryFlags(
            Properties(MaterialSimulationKind.Gas),
            Properties(MaterialSimulationKind.Solid, MaterialFlags.MovableSolid));
        Require(gasToMovable == (PhaseTransitionSummaryFlags.PhaseOccurred |
            PhaseTransitionSummaryFlags.TouchesSolid |
            PhaseTransitionSummaryFlags.TargetMovableSolid),
            "Gas-to-movable-solid summary flags are incorrect.");
        PhaseTransitionSummaryFlags solidToLiquid = PhaseTransitionRuntime.GetSummaryFlags(
            Properties(MaterialSimulationKind.Solid), Properties(MaterialSimulationKind.Liquid));
        Require((solidToLiquid & (PhaseTransitionSummaryFlags.TargetCellular |
            PhaseTransitionSummaryFlags.TargetLiquid |
            PhaseTransitionSummaryFlags.TouchesLiquid |
            PhaseTransitionSummaryFlags.TouchesSolid)) ==
            (PhaseTransitionSummaryFlags.TargetCellular |
            PhaseTransitionSummaryFlags.TargetLiquid |
            PhaseTransitionSummaryFlags.TouchesLiquid |
            PhaseTransitionSummaryFlags.TouchesSolid),
            "Solid-to-liquid summary flags are incomplete.");
    }

    private static void VerifyDispatchPolicyAndFallback()
    {
        Require(PhaseTransitionDispatchPolicy.GetDispatchCount(true, true, true, false, 1) == 1 &&
            PhaseTransitionDispatchPolicy.GetDispatchCount(true, true, true, false, 4) == 1,
            "Thermal batches must produce exactly one phase pass.");
        Require(PhaseTransitionDispatchPolicy.GetDispatchCount(true, true, true, true, 4) == 0,
            "Paused simulation dispatched phase transitions.");
        Require(PhaseTransitionDispatchPolicy.GetDispatchCount(true, true, true, false, 0) == 0,
            "Frame without thermal ticks dispatched phase transitions.");
        Require(PhaseTransitionDispatchPolicy.GetDispatchCount(false, true, true, false, 4) == 0,
            "Registry without transitions dispatched phase transitions.");

        PhaseTransitionWakeUpGate gate = new();
        PhaseSummaryReadbackScheduleResult queued = PhaseSummaryReadbackPolicy.SelectSlot(
            [true, false, true],
            out int queuedSlot);
        if (queued == PhaseSummaryReadbackScheduleResult.NoFreeSlot)
        {
            gate.Request();
        }
        Require(queued == PhaseSummaryReadbackScheduleResult.Queued && queuedSlot == 1,
            "Free readback slot was not selected.");
        Require(!gate.Consume(), "Queued summary incorrectly requested fallback.");

        PhaseSummaryReadbackScheduleResult full = PhaseSummaryReadbackPolicy.SelectSlot(
            [true, true, true],
            out int missingSlot);
        Require(full == PhaseSummaryReadbackScheduleResult.NoFreeSlot && missingSlot == -1,
            "Full readback ring was not reported.");
        if (full == PhaseSummaryReadbackScheduleResult.NoFreeSlot)
        {
            gate.Request();
            gate.Request();
        }
        Require(gate.Consume() && !gate.Consume(),
            "Concurrent fallback requests were not coalesced into one wake-up.");

        if (PhaseSummaryReadbackPolicy.SelectSlot([true, true, true], out _) ==
            PhaseSummaryReadbackScheduleResult.NoFreeSlot)
        {
            gate.Request();
        }
        Require(gate.Consume() && !gate.Consume(),
            "Fallback gate could not be reused after its first Consume.");
        gate.Request();
        gate.Reset();
        Require(!gate.Consume(), "Fallback reset retained a pending wake-up.");
    }

    private static GridCell Cell(uint materialIndex, float temperature) => new()
    {
        MaterialIndex = materialIndex,
        Mass = 1,
        IsActive = 1,
        Temperature = temperature
    };

    private static MaterialProperties Properties(
        MaterialSimulationKind kind,
        MaterialFlags flags = MaterialFlags.None) => new()
    {
        SimulationKind = (uint)kind,
        Flags = (uint)flags,
        HeatCapacity = 1,
        TransitionBelowMaterialIndex = uint.MaxValue,
        TransitionAboveMaterialIndex = uint.MaxValue
    };

    private static void RequireOrdered(string source, params string[] values)
    {
        int previous = -1;
        foreach (string value in values)
        {
            int position = source.IndexOf(value, StringComparison.Ordinal);
            Require(position > previous, $"Declaration '{value}' is missing or out of order.");
            previous = position;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
