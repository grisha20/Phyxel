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
// FIRE.cpp converts expired FIRE to SMKE only below 625 K.  Phyxel stores
// temperatures in Celsius, hence this exact equivalent threshold.
static const float FireToSmokeTemperature = 351.85;

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

    // Hot FIRE is killed when its resource expires.  Only cooled FIRE becomes
    // SMKE; converting every expired flame made smoke originate at the brush
    // rather than at the cooled perimeter of the flow.
    if (flame && !extinguished && cell.Temperature >= FireToSmokeTemperature)
    {
        Grid[index] = CreateEmptyCell();
        InterlockedOr(CombustionSummary[0], CombustionOccurred | TargetCellular);
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
    // A discrete transient becomes one target particle. Carrying FIRE's unit
    // mass into low-density smoke would make the gas solver split it into
    // dozens of smoke cells and overwhelm the visible flame.
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
