#include "PhysicsShared.hlsli"

StructuredBuffer<GridCell> Grid : register(t0);
StructuredBuffer<MaterialProperties> Materials : register(t1);
RWStructuredBuffer<uint> Claims : register(u0);
RWStructuredBuffer<EmissionRequest> Requests : register(u1);

bool IsEmpty(uint index)
{
    return Grid[index].IsActive == 0;
}

bool TryCoordinate(int2 coordinate, out uint index)
{
    if (coordinate.x < 0 || coordinate.y < 0 ||
        coordinate.x >= int(Width) || coordinate.y >= int(Height))
    {
        index = 0xffffffffu;
        return false;
    }
    index = FlattenCoordinate(uint2(coordinate));
    return IsEmpty(index);
}

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= Width || coordinate.y >= Height)
    {
        return;
    }

    uint sourceIndex = FlattenCoordinate(coordinate);
    GridCell cell = Grid[sourceIndex];
    if (cell.IsActive == 0)
    {
        return;
    }
    MaterialProperties material = Materials[cell.MaterialIndex];
    if (material.SimulationKind != SimulationKindGas ||
        (material.Flags & MaterialFlagFlame) != 0 ||
        cell.Mass > max(0.12, material.Density * 2.0))
    {
        return;
    }

    uint seed = HashValue(sourceIndex ^ (FrameIndex * 0x9e3779b9u) ^ cell.MaterialIndex);
    float moveChance = saturate((2.5 + material.FlowRate * 4.5) * DeltaTime);
    if (HashUnitFloat(seed) >= moveChance)
    {
        return;
    }

    int lateral = (seed & 1u) == 0 ? -1 : 1;
    int selector = int((seed >> 8) % 10u);
    int2 candidates[4];
    if (selector < 6)
    {
        candidates[0] = int2(0, -1);
        candidates[1] = int2(lateral, -1);
        candidates[2] = int2(lateral, 0);
        candidates[3] = int2(-lateral, -1);
    }
    else if (selector < 9)
    {
        candidates[0] = int2(lateral, -1);
        candidates[1] = int2(0, -1);
        candidates[2] = int2(lateral, 0);
        candidates[3] = int2(-lateral, 0);
    }
    else
    {
        candidates[0] = int2(lateral, 0);
        candidates[1] = int2(-lateral, 0);
        candidates[2] = int2(lateral, -1);
        candidates[3] = int2(0, -1);
    }

    uint destinationIndex = 0xffffffffu;
    [unroll]
    for (int candidate = 0; candidate < 4; candidate++)
    {
        if (TryCoordinate(int2(coordinate) + candidates[candidate], destinationIndex))
        {
            break;
        }
    }
    if (destinationIndex == 0xffffffffu)
    {
        return;
    }

    EmissionRequest request;
    request.DestinationIndex = destinationIndex;
    request.MaterialIndex = cell.MaterialIndex;
    request.Mass = cell.Mass;
    request.Temperature = cell.Temperature;
    request.SourceIndex = sourceIndex;
    Requests[sourceIndex] = request;
    uint ignored;
    InterlockedMin(Claims[destinationIndex], sourceIndex, ignored);
}
