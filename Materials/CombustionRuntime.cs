using System;
using Phyxel.Physics;

namespace Phyxel.Materials;

[Flags]
public enum CombustionSummaryFlags : uint
{
    None = 0,
    CombustionOccurred = 1u << 0,
    BurnoutOccurred = 1u << 1,
    TargetCellular = 1u << 2,
    TargetLiquid = 1u << 3,
    TargetGas = 1u << 4,
    TouchesLiquid = 1u << 5,
    TouchesSolid = 1u << 6,
    TargetMovableSolid = 1u << 7
}

public static class CombustionRuntime
{
    public const uint MissingMaterialIndex = uint.MaxValue;
    public const float MassEpsilon = 0.0001f;
    public const float MinimumTemperature = -273.15f;
    public const float MaximumTemperature = 5000f;

    public static bool IsBurning(
        GridCell cell,
        ReadOnlySpan<MaterialProperties> materials)
    {
        if (cell.IsActive == 0 || cell.MaterialIndex >= materials.Length)
        {
            return false;
        }

        MaterialProperties source = materials[(int)cell.MaterialIndex];
        if (source.SimulationKind != (uint)MaterialSimulationKind.Solid ||
            source.BurnedIntoMaterialIndex == MissingMaterialIndex ||
            source.BurnedIntoMaterialIndex >= materials.Length)
        {
            return false;
        }

        MaterialProperties target = materials[(int)source.BurnedIntoMaterialIndex];
        float residueMass = target.SimulationKind == (uint)MaterialSimulationKind.None
            ? 0
            : target.Density;
        return cell.Mass > residueMass + MassEpsilon &&
            cell.Temperature > source.IgnitionTemperature;
    }

    public static bool TryApply(
        ref GridCell cell,
        ReadOnlySpan<MaterialProperties> materials,
        float elapsedSeconds,
        out CombustionSummaryFlags summary,
        out float burnedMass)
    {
        summary = CombustionSummaryFlags.None;
        burnedMass = 0;
        if (!IsBurning(cell, materials) || !float.IsFinite(elapsedSeconds) || elapsedSeconds <= 0)
        {
            return false;
        }

        MaterialProperties source = materials[(int)cell.MaterialIndex];
        MaterialProperties target = materials[(int)source.BurnedIntoMaterialIndex];
        float residueMass = target.SimulationKind == (uint)MaterialSimulationKind.None
            ? 0
            : Math.Max(0, target.Density);
        float availableFuel = Math.Max(0, cell.Mass - residueMass);
        burnedMass = Math.Min(availableFuel, source.BurnRate * elapsedSeconds);
        if (burnedMass <= 0)
        {
            return false;
        }

        float capacity = Math.Max(MaterialRegistry.MinimumHeatCapacity,
            source.HeatCapacity * Math.Max(cell.Mass, MassEpsilon));
        cell.Temperature = Math.Clamp(
            cell.Temperature + burnedMass * source.HeatPerMass / capacity,
            MinimumTemperature,
            MaximumTemperature);
        cell.Mass = Math.Max(residueMass, cell.Mass - burnedMass);
        summary = CombustionSummaryFlags.CombustionOccurred | GetTargetFlags(source, target);

        if (availableFuel - burnedMass <= MassEpsilon)
        {
            cell.Mass = residueMass;
            summary |= CombustionSummaryFlags.BurnoutOccurred;
            NormalizeBurnout(ref cell, source, target, source.BurnedIntoMaterialIndex);
        }

        return true;
    }

    public static void NormalizeBurnout(
        ref GridCell cell,
        MaterialProperties source,
        MaterialProperties target,
        uint targetIndex)
    {
        if (target.SimulationKind == (uint)MaterialSimulationKind.None || targetIndex == 0)
        {
            cell = default;
            return;
        }

        MaterialSimulationKind sourceKind = (MaterialSimulationKind)source.SimulationKind;
        MaterialSimulationKind targetKind = (MaterialSimulationKind)target.SimulationKind;
        bool sourceCellular = PhaseTransitionRuntime.IsCellular(sourceKind);
        bool targetCellular = PhaseTransitionRuntime.IsCellular(targetKind);
        bool targetMovable = targetKind == MaterialSimulationKind.Solid &&
            ((MaterialFlags)target.Flags & MaterialFlags.MovableSolid) != 0;
        cell.MaterialIndex = targetIndex;
        cell.IsActive = 1;
        cell.Mass = target.Density;
        cell.BodyId = 0;
        if (!sourceCellular || !targetCellular)
        {
            cell.VelocityX = 0;
            cell.VelocityY = 0;
        }
        if (sourceKind != MaterialSimulationKind.Liquid || targetKind != MaterialSimulationKind.Liquid)
        {
            cell.Pressure = 0;
        }
        cell.RestFrames = targetKind == MaterialSimulationKind.Solid && !targetMovable ? 2u : 0u;
        cell.Lifetime = target.MinimumLifetime;
    }

    public static CombustionSummaryFlags GetTargetFlags(
        MaterialProperties source,
        MaterialProperties target)
    {
        MaterialSimulationKind sourceKind = (MaterialSimulationKind)source.SimulationKind;
        MaterialSimulationKind targetKind = (MaterialSimulationKind)target.SimulationKind;
        CombustionSummaryFlags flags = CombustionSummaryFlags.None;
        if (PhaseTransitionRuntime.IsCellular(targetKind)) flags |= CombustionSummaryFlags.TargetCellular;
        if (targetKind == MaterialSimulationKind.Liquid) flags |= CombustionSummaryFlags.TargetLiquid;
        if (targetKind == MaterialSimulationKind.Gas) flags |= CombustionSummaryFlags.TargetGas;
        if (sourceKind == MaterialSimulationKind.Liquid || targetKind == MaterialSimulationKind.Liquid)
            flags |= CombustionSummaryFlags.TouchesLiquid;
        if (sourceKind == MaterialSimulationKind.Solid || targetKind == MaterialSimulationKind.Solid)
            flags |= CombustionSummaryFlags.TouchesSolid;
        if (targetKind == MaterialSimulationKind.Solid &&
            ((MaterialFlags)target.Flags & MaterialFlags.MovableSolid) != 0)
            flags |= CombustionSummaryFlags.TargetMovableSolid;
        return flags;
    }
}

public static class TransientMaterialRuntime
{
    public static bool IsFlame(GridCell cell, ReadOnlySpan<MaterialProperties> materials) =>
        cell.IsActive != 0 && cell.MaterialIndex < materials.Length && cell.Lifetime > 0 &&
        (((MaterialFlags)materials[(int)cell.MaterialIndex].Flags & MaterialFlags.Flame) != 0);

    public static bool ShouldIgnite(
        MaterialProperties combustible,
        float elapsedSeconds,
        float randomUnit) =>
        combustible.FlameSpreadRate > 0 && elapsedSeconds > 0 &&
        randomUnit >= 0 && randomUnit < Math.Clamp(combustible.FlameSpreadRate * elapsedSeconds, 0, 1);

    public static bool TryAdvance(
        ref GridCell cell,
        ReadOnlySpan<MaterialProperties> materials,
        float elapsedSeconds)
    {
        if (cell.IsActive == 0 || cell.MaterialIndex >= materials.Length ||
            !float.IsFinite(elapsedSeconds) || elapsedSeconds <= 0)
        {
            return false;
        }
        MaterialProperties source = materials[(int)cell.MaterialIndex];
        if (source.MaximumLifetime <= 0 ||
            source.DecayIntoMaterialIndex == CombustionRuntime.MissingMaterialIndex)
        {
            return false;
        }
        bool flame = ((MaterialFlags)source.Flags & MaterialFlags.Flame) != 0;
        cell.Lifetime = flame && cell.Temperature < source.InitialTemperature * 0.15f
            ? 0
            : Math.Max(0, cell.Lifetime - elapsedSeconds);
        if (cell.Lifetime > 0)
        {
            if (flame)
            {
                cell.VelocityY = Math.Min(cell.VelocityY, -8);
            }
            return true;
        }
        uint targetIndex = source.DecayIntoMaterialIndex;
        if (targetIndex == 0 || targetIndex >= materials.Length ||
            materials[(int)targetIndex].SimulationKind == (uint)MaterialSimulationKind.None)
        {
            cell = default;
            return true;
        }
        MaterialProperties target = materials[(int)targetIndex];
        cell.MaterialIndex = targetIndex;
        cell.Mass = target.Density;
        cell.Pressure = 0;
        cell.IsActive = 1;
        cell.BodyId = 0;
        cell.RestFrames = 0;
        cell.Lifetime = target.MinimumLifetime;
        return true;
    }
}

public static class CombustionDispatchPolicy
{
    public static int GetDispatchCount(
        bool registryHasCombustion,
        bool simulationAllocated,
        bool thermalActive,
        bool paused,
        int thermalTicks) =>
        registryHasCombustion && simulationAllocated && thermalActive && !paused && thermalTicks > 0 ? 1 : 0;
}
