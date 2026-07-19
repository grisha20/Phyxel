#include "PhysicsShared.hlsli"

StructuredBuffer<EmissionRequest> Requests : register(t0);
StructuredBuffer<uint> Claims : register(t1);
RWStructuredBuffer<GridCell> Grid : register(u0);
RWStructuredBuffer<uint> CellMaterials : register(u1);

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= Width || coordinate.y >= Height)
    {
        return;
    }
    uint destinationIndex = FlattenCoordinate(coordinate);
    uint sourceIndex = Claims[destinationIndex];
    if (sourceIndex == 0xffffffffu || sourceIndex >= Width * Height)
    {
        return;
    }
    EmissionRequest request = Requests[sourceIndex];
    if (request.SourceIndex != sourceIndex || request.DestinationIndex != destinationIndex)
    {
        return;
    }
    GridCell source = Grid[sourceIndex];
    if (source.IsActive == 0 || source.MaterialIndex != request.MaterialIndex ||
        Grid[destinationIndex].IsActive != 0)
    {
        return;
    }
    source.RestFrames = 0;
    source.BodyId = 0;
    source.Pressure = 0;
    source.VelocityX = float(int(coordinate.x) - int(sourceIndex % Width));
    source.VelocityY = float(int(coordinate.y) - int(sourceIndex / Width));
    Grid[destinationIndex] = source;
    Grid[sourceIndex] = CreateEmptyCell();
    CellMaterials[destinationIndex] = source.MaterialIndex;
    CellMaterials[sourceIndex] = 0;
}
