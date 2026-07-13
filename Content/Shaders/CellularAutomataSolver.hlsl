#include "PhysicsShared.hlsli"

StructuredBuffer<GridCell> SourceGrid : register(t0);
StructuredBuffer<LatticeParticle> SourceParticles : register(t1);
StructuredBuffer<MaterialProperties> Materials : register(t2);
RWStructuredBuffer<GridCell> DestinationGrid : register(u0);

bool TryMoveCell(uint sourceIndex, uint targetIndex, GridCell sourceCell)
{
    if (targetIndex >= ParticleCount || SourceGrid[targetIndex].IsActive != 0 || SourceParticles[targetIndex].IsActive != 0)
    {
        return false;
    }

    uint originalValue;
    InterlockedCompareExchange(DestinationGrid[targetIndex].IsActive, 0, 2, originalValue);
    if (originalValue != 0)
    {
        return false;
    }

    sourceCell.IsActive = 1;
    DestinationGrid[targetIndex] = sourceCell;
    DestinationGrid[sourceIndex].IsActive = 0;
    DestinationGrid[sourceIndex].MaterialId = 0;
    return true;
}

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= Width || dispatchThreadId.y >= Height)
    {
        return;
    }

    uint2 coordinate = dispatchThreadId.xy;
    uint index = FlattenCoordinate(coordinate);
    GridCell cell = SourceGrid[index];
    if (cell.IsActive == 0 || Materials[cell.MaterialId].SimulationKind != 1 || coordinate.y + 1 >= Height)
    {
        return;
    }

    cell.VelocityY = min(cell.VelocityY + Gravity * DeltaTime, MaximumVelocity);
    uint below = index + Width;
    if (TryMoveCell(index, below, cell))
    {
        return;
    }

    int direction = ((HashValue(index ^ FrameIndex) & 1) == 0) ? -1 : 1;
    if (cell.MaterialId == 1)
    {
        int diagonalX = int(coordinate.x) + direction;
        if (diagonalX >= 0 && diagonalX < int(Width) && TryMoveCell(index, below + direction, cell))
        {
            return;
        }

        diagonalX = int(coordinate.x) - direction;
        if (diagonalX >= 0 && diagonalX < int(Width))
        {
            TryMoveCell(index, below - direction, cell);
        }
    }
    else if (cell.MaterialId == 2)
    {
        int sideX = int(coordinate.x) + direction;
        cell.VelocityX = direction * Materials[cell.MaterialId].FlowRate * 60;
        if (sideX >= 0 && sideX < int(Width) && TryMoveCell(index, index + direction, cell))
        {
            return;
        }

        sideX = int(coordinate.x) - direction;
        if (sideX >= 0 && sideX < int(Width))
        {
            TryMoveCell(index, index - direction, cell);
        }
    }
}
