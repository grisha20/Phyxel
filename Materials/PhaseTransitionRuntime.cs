using System;
using Phyxel.Physics;

namespace Phyxel.Materials;

[Flags]
public enum PhaseTransitionSummaryFlags : uint
{
    None = 0,
    PhaseOccurred = 1u << 0,
    TargetCellular = 1u << 1,
    TargetLiquid = 1u << 2,
    TargetGas = 1u << 3,
    TouchesLiquid = 1u << 4,
    TouchesSolid = 1u << 5,
    TargetMovableSolid = 1u << 6
}

public static class PhaseTransitionRuntime
{
    public const uint MissingMaterialIndex = uint.MaxValue;

    public static bool TryApply(
        ref GridCell cell,
        ReadOnlySpan<MaterialProperties> materials,
        out PhaseTransitionSummaryFlags summary)
    {
        summary = PhaseTransitionSummaryFlags.None;
        if (cell.IsActive == 0 || cell.MaterialIndex >= materials.Length)
        {
            return false;
        }

        MaterialProperties source = materials[(int)cell.MaterialIndex];
        uint targetIndex = SelectTarget(cell.Temperature, source);
        if (!IsValidTarget(targetIndex, materials))
        {
            return false;
        }

        MaterialProperties target = materials[(int)targetIndex];
        summary = GetSummaryFlags(source, target);
        cell = Normalize(cell, source, target, targetIndex);
        return true;
    }

    public static uint SelectTarget(float temperature, MaterialProperties source)
    {
        if (source.TransitionBelowMaterialIndex != MissingMaterialIndex &&
            temperature < source.TransitionBelowTemperature)
        {
            return source.TransitionBelowMaterialIndex;
        }
        if (source.TransitionAboveMaterialIndex != MissingMaterialIndex &&
            temperature > source.TransitionAboveTemperature)
        {
            return source.TransitionAboveMaterialIndex;
        }
        return MissingMaterialIndex;
    }

    public static bool IsValidTarget(
        uint targetIndex,
        ReadOnlySpan<MaterialProperties> materials)
    {
        if (targetIndex == 0 || targetIndex >= materials.Length)
        {
            return false;
        }
        MaterialSimulationKind kind =
            (MaterialSimulationKind)materials[(int)targetIndex].SimulationKind;
        return kind is not MaterialSimulationKind.None and not MaterialSimulationKind.Tool;
    }

    public static GridCell Normalize(
        GridCell cell,
        MaterialProperties source,
        MaterialProperties target,
        uint targetIndex)
    {
        MaterialSimulationKind sourceKind = (MaterialSimulationKind)source.SimulationKind;
        MaterialSimulationKind targetKind = (MaterialSimulationKind)target.SimulationKind;
        bool sourceCellular = IsCellular(sourceKind);
        bool targetCellular = IsCellular(targetKind);
        bool targetMovableSolid = targetKind == MaterialSimulationKind.Solid &&
            ((MaterialFlags)target.Flags & MaterialFlags.MovableSolid) != 0;

        cell.MaterialIndex = targetIndex;
        cell.IsActive = 1;
        cell.BodyId = 0;
        if (!sourceCellular || !targetCellular)
        {
            cell.VelocityX = 0;
            cell.VelocityY = 0;
        }
        if (sourceKind != MaterialSimulationKind.Liquid ||
            targetKind != MaterialSimulationKind.Liquid)
        {
            cell.Pressure = 0;
        }
        cell.RestFrames = targetKind == MaterialSimulationKind.Solid && !targetMovableSolid
            ? 2u
            : 0u;
        cell.Lifetime = target.MinimumLifetime;
        return cell;
    }

    public static PhaseTransitionSummaryFlags GetSummaryFlags(
        MaterialProperties source,
        MaterialProperties target)
    {
        MaterialSimulationKind sourceKind = (MaterialSimulationKind)source.SimulationKind;
        MaterialSimulationKind targetKind = (MaterialSimulationKind)target.SimulationKind;
        PhaseTransitionSummaryFlags flags = PhaseTransitionSummaryFlags.PhaseOccurred;
        if (IsCellular(targetKind))
        {
            flags |= PhaseTransitionSummaryFlags.TargetCellular;
        }
        if (targetKind == MaterialSimulationKind.Liquid)
        {
            flags |= PhaseTransitionSummaryFlags.TargetLiquid;
        }
        if (targetKind == MaterialSimulationKind.Gas)
        {
            flags |= PhaseTransitionSummaryFlags.TargetGas;
        }
        if (sourceKind == MaterialSimulationKind.Liquid || targetKind == MaterialSimulationKind.Liquid)
        {
            flags |= PhaseTransitionSummaryFlags.TouchesLiquid;
        }
        if (sourceKind == MaterialSimulationKind.Solid || targetKind == MaterialSimulationKind.Solid)
        {
            flags |= PhaseTransitionSummaryFlags.TouchesSolid;
        }
        if (targetKind == MaterialSimulationKind.Solid &&
            ((MaterialFlags)target.Flags & MaterialFlags.MovableSolid) != 0)
        {
            flags |= PhaseTransitionSummaryFlags.TargetMovableSolid;
        }
        return flags;
    }

    public static bool IsCellular(MaterialSimulationKind kind) => kind is
        MaterialSimulationKind.Granular or MaterialSimulationKind.Liquid or MaterialSimulationKind.Gas;
}

public static class PhaseTransitionDispatchPolicy
{
    public static int GetDispatchCount(
        bool registryHasPhaseTransitions,
        bool simulationAllocated,
        bool thermalActive,
        bool paused,
        int thermalTicks) =>
        registryHasPhaseTransitions && simulationAllocated && thermalActive && !paused && thermalTicks > 0
            ? 1
            : 0;
}

public enum PhaseSummaryReadbackScheduleResult
{
    Queued,
    NoFreeSlot
}

public static class PhaseSummaryReadbackPolicy
{
    public static PhaseSummaryReadbackScheduleResult SelectSlot(
        ReadOnlySpan<bool> pendingSlots,
        out int slotIndex)
    {
        for (int index = 0; index < pendingSlots.Length; index++)
        {
            if (!pendingSlots[index])
            {
                slotIndex = index;
                return PhaseSummaryReadbackScheduleResult.Queued;
            }
        }
        slotIndex = -1;
        return PhaseSummaryReadbackScheduleResult.NoFreeSlot;
    }
}

public sealed class PhaseTransitionWakeUpGate
{
    private bool pending;

    public void Request() => pending = true;

    public bool Consume()
    {
        bool result = pending;
        pending = false;
        return result;
    }

    public void Reset() => pending = false;
}
