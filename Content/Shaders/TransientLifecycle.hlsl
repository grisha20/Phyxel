#include "PhysicsShared.hlsli"

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
RWStructuredBuffer<GridCell> Grid : register(u0);
RWStructuredBuffer<uint> CombustionSummary : register(u1);

static const uint CombustionOccurred = 1u << 0;
static const uint TargetCellular = 1u << 2;
static const uint TargetGas = 1u << 4;

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
    // Temperature can shorten a flame only when it is almost cooled to the
    // ambient range. Normal flame lifetime remains the Powder-Toy-style
    // 2–3 second visual timer; hot wood must not extinguish it immediately.
    cell.Lifetime = flame && cell.Temperature < material.InitialTemperature * 0.15
        ? 0
        : max(0, cell.Lifetime - TransientDeltaTime);
    if (cell.Lifetime > 0)
    {
        if (flame)
        {
            cell.VelocityY = min(cell.VelocityY, -8.0);
        }
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
