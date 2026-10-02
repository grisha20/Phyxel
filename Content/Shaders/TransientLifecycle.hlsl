#include "PhysicsShared.hlsli"
#include "OxidizerShared.hlsli"

cbuffer TransientConstants : register(b0)
{
    float TransientDeltaTime;
    uint TransientWidth;
    uint TransientHeight;
    uint TransientMaterialCount;
    uint TransientTickIndex;
    uint TransientReserved0;
    uint TransientReserved1;
    uint TransientReserved2;
};

StructuredBuffer<MaterialProperties> Materials : register(t0);
StructuredBuffer<float> Oxidizer : register(t1);
RWStructuredBuffer<GridCell> Grid : register(u0);
RWStructuredBuffer<uint> CombustionSummary : register(u1);

static const uint CombustionOccurred = 1u << 0;
static const uint TargetCellular = 1u << 2;
static const uint TargetGas = 1u << 4;

float2 OxidizerAt(uint index)
{
    GridCell cell = Grid[index];
    return float2(Oxidizer[index], OxidizerSpace(cell, Materials[cell.MaterialIndex]));
}

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= TransientWidth || coordinate.y >= TransientHeight)
    {
        return;
    }

    uint index = coordinate.y * TransientWidth + coordinate.x;
    GridCell cell = Grid[index];
    if (cell.IsActive == 0 || cell.MaterialIndex >= TransientMaterialCount)
    {
        return;
    }
    MaterialProperties material = Materials[cell.MaterialIndex];
    if (material.MaximumLifetime <= 0 || material.DecayIntoMaterialIndex == 0xffffffffu ||
        TransientDeltaTime <= 0)
    {
        return;
    }

    bool flame = (material.Flags & MaterialFlagFlame) != 0;
    float2 supply = OxidizerAt(index);
    if (coordinate.x > 0) supply += OxidizerAt(index - 1);
    if (coordinate.x + 1 < TransientWidth) supply += OxidizerAt(index + 1);
    if (coordinate.y > 0) supply += OxidizerAt(index - TransientWidth);
    if (coordinate.y + 1 < TransientHeight) supply += OxidizerAt(index + TransientWidth);
    bool extinguished = flame && cell.BodyId != SelfOxidizingFlameMarker &&
        supply.x <= max(supply.y, 1) * OxidizerExtinctionThreshold;
    cell.Lifetime = extinguished ? 0 : max(0, cell.Lifetime - TransientDeltaTime);
    if (cell.Lifetime > 0)
    {
        Grid[index] = cell;
        return;
    }

    uint targetIndex = material.DecayIntoMaterialIndex;
    if (targetIndex == 0 || targetIndex >= TransientMaterialCount ||
        Materials[targetIndex].SimulationKind == SimulationKindNone)
    {
        Grid[index] = CreateEmptyCell();
        InterlockedOr(CombustionSummary[0], CombustionOccurred | TargetCellular);
        return;
    }

    MaterialProperties target = Materials[targetIndex];
    cell.MaterialIndex = targetIndex;
    // Ordinary motion gas carries whole packets without splitting by density.
    // Flame -> smoke must carry its heat rather than deleting the hot packet
    // or discarding 96% of its thermal capacity when changing material.
    bool carryHeat = flame && (target.Flags & MaterialFlagSmoke) != 0 &&
        material.HeatCapacity > 0 && target.HeatCapacity > 0;
    if (carryHeat)
        cell.Temperature = 20.0 + (cell.Temperature - 20.0) * material.HeatCapacity / target.HeatCapacity;
    else
        cell.Mass = target.Density;
    cell.Pressure = 0;
    cell.IsActive = 1;
    cell.BodyId = 0;
    cell.RestFrames = 0;
    cell.Lifetime = InitialMaterialLifetime(target, index ^ TransientTickIndex);
    Grid[index] = cell;
    uint flags = CombustionOccurred | TargetCellular;
    if (target.SimulationKind == SimulationKindGas)
    {
        flags |= TargetGas;
    }
    InterlockedOr(CombustionSummary[0], flags);
}
