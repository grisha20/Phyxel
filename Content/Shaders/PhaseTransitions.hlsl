#include "PhysicsShared.hlsli"

cbuffer PhaseConstants : register(b0)
{
    uint PhaseWidth;
    uint PhaseHeight;
    uint MaterialCount;
    uint PhaseTickIndex;
    uint PhaseTickCount;
    uint PhaseReserved0;
    uint PhaseReserved1;
    uint PhaseReserved2;
};

StructuredBuffer<MaterialProperties> Materials : register(t0);
RWStructuredBuffer<GridCell> Grid : register(u0);
RWStructuredBuffer<uint> PhaseSummary : register(u1);

bool IsValidPhaseTarget(uint materialIndex)
{
    if (materialIndex == 0 || materialIndex >= MaterialCount)
    {
        return false;
    }
    uint kind = Materials[materialIndex].SimulationKind;
    return kind != SimulationKindNone && kind != SimulationKindTool;
}

uint SelectPhaseTarget(GridCell cell, MaterialProperties source)
{
    if (source.TransitionBelowMaterialIndex != 0xffffffffu &&
        cell.Temperature < source.TransitionBelowTemperature)
    {
        return source.TransitionBelowMaterialIndex;
    }
    if (source.TransitionAboveMaterialIndex != 0xffffffffu &&
        cell.Temperature > source.TransitionAboveTemperature)
    {
        return source.TransitionAboveMaterialIndex;
    }
    return 0xffffffffu;
}

uint BuildPhaseSummary(MaterialProperties source, MaterialProperties target)
{
    uint flags = PhaseSummaryPhaseOccurred;
    if (IsCellularMaterial(target.SimulationKind))
    {
        flags |= PhaseSummaryTargetCellular;
    }
    if (target.SimulationKind == SimulationKindLiquid)
    {
        flags |= PhaseSummaryTargetLiquid;
    }
    if (target.SimulationKind == SimulationKindGas)
    {
        flags |= PhaseSummaryTargetGas;
    }
    if (source.SimulationKind == SimulationKindLiquid || target.SimulationKind == SimulationKindLiquid)
    {
        flags |= PhaseSummaryTouchesLiquid;
    }
    if (source.SimulationKind == SimulationKindSolid || target.SimulationKind == SimulationKindSolid)
    {
        flags |= PhaseSummaryTouchesSolid;
    }
    if (IsMovableSolidMaterial(target))
    {
        flags |= PhaseSummaryTargetMovableSolid;
    }
    return flags;
}

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= PhaseWidth || coordinate.y >= PhaseHeight)
    {
        return;
    }

    uint index = coordinate.y * PhaseWidth + coordinate.x;
    GridCell cell = Grid[index];
    if (cell.IsActive == 0 || cell.MaterialIndex >= MaterialCount)
    {
        return;
    }

    MaterialProperties source = Materials[cell.MaterialIndex];
    bool latentTransitionAccepted = false;
    if (source.TransitionAboveLatentHeat > 0 &&
        source.TransitionAboveMaterialIndex != 0xffffffffu &&
        cell.Temperature > source.TransitionAboveTemperature)
    {
        float excessTemperature = cell.Temperature - source.TransitionAboveTemperature;
        float vaporizedFraction = saturate(
            excessTemperature * max(0.01, source.HeatCapacity) /
            source.TransitionAboveLatentHeat);
        float transitionChance = excessTemperature >= 10.0
            ? 1.0
            : 1.0 - pow(1.0 - vaporizedFraction, max(1u, PhaseTickCount));
        uint seed = index ^ (PhaseTickIndex * 0x9e3779b9u);
        cell.Temperature = source.TransitionAboveTemperature;
        if (HashUnitFloat(seed) >= transitionChance)
        {
            Grid[index] = cell;
            return;
        }
        latentTransitionAccepted = true;
    }
    uint targetIndex = latentTransitionAccepted
        ? source.TransitionAboveMaterialIndex
        : SelectPhaseTarget(cell, source);
    if (!IsValidPhaseTarget(targetIndex))
    {
        return;
    }

    MaterialProperties target = Materials[targetIndex];
    bool sourceCellular = IsCellularMaterial(source.SimulationKind);
    bool targetCellular = IsCellularMaterial(target.SimulationKind);
    cell.MaterialIndex = targetIndex;
    cell.IsActive = 1;
    cell.BodyId = 0;
    if (!sourceCellular || !targetCellular)
    {
        cell.VelocityX = 0;
        cell.VelocityY = 0;
    }
    if (source.SimulationKind != SimulationKindLiquid || target.SimulationKind != SimulationKindLiquid)
    {
        cell.Pressure = 0;
    }
    cell.RestFrames = target.SimulationKind == SimulationKindSolid && !IsMovableSolidMaterial(target)
        ? 2u
        : 0u;
    cell.Lifetime = InitialMaterialLifetime(target, index);
    Grid[index] = cell;
    InterlockedOr(PhaseSummary[0], BuildPhaseSummary(source, target));
}
