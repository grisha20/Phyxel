#include "PhysicsShared.hlsli"

cbuffer ContactConstants : register(b0)
{
    float ContactDeltaTime;
    uint ContactWidth;
    uint ContactHeight;
    uint ContactTickIndex;
};

StructuredBuffer<MaterialProperties> Materials : register(t0);
RWStructuredBuffer<GridCell> Grid : register(u0);
RWStructuredBuffer<uint> CellMaterials : register(u1);

bool IsLiquidContact(uint2 coordinate)
{
    uint index = coordinate.y * ContactWidth + coordinate.x;
    GridCell neighbour = Grid[index];
    return neighbour.IsActive != 0 &&
        Materials[neighbour.MaterialIndex].SimulationKind == SimulationKindLiquid;
}

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= ContactWidth || dispatchThreadId.y >= ContactHeight)
    {
        return;
    }

    uint2 coordinate = dispatchThreadId.xy;
    uint index = coordinate.y * ContactWidth + coordinate.x;
    GridCell cell = Grid[index];
    if (cell.IsActive == 0)
    {
        return;
    }

    MaterialProperties source = Materials[cell.MaterialIndex];
    if (source.SimulationKind != SimulationKindGranular ||
        source.ContactLiquidIntoMaterialIndex == 0xffffffffu ||
        source.ContactLiquidRatePerSecond <= 0)
    {
        return;
    }

    bool touchingLiquid =
        (coordinate.x > 0 && IsLiquidContact(coordinate - uint2(1, 0))) ||
        (coordinate.x + 1 < ContactWidth && IsLiquidContact(coordinate + uint2(1, 0))) ||
        (coordinate.y > 0 && IsLiquidContact(coordinate - uint2(0, 1))) ||
        (coordinate.y + 1 < ContactHeight && IsLiquidContact(coordinate + uint2(0, 1)));
    if (!touchingLiquid)
    {
        return;
    }

    float probability = 1.0 - exp(-source.ContactLiquidRatePerSecond * ContactDeltaTime);
    uint seed = index ^ (ContactTickIndex * 0x9e3779b9u) ^ 0x68bc21ebu;
    if (HashUnitFloat(seed) >= saturate(probability))
    {
        return;
    }

    cell.MaterialIndex = source.ContactLiquidIntoMaterialIndex;
    cell.BodyId = 0;
    cell.Pressure = 0;
    cell.RestFrames = 0;
    Grid[index] = cell;
    CellMaterials[index] = cell.MaterialIndex;
}
