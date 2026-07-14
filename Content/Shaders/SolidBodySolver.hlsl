#include "PhysicsShared.hlsli"

StructuredBuffer<GridCell> SourceGrid : register(t0);
StructuredBuffer<uint> SourceBodyFlags : register(t1);
RWStructuredBuffer<uint> BodyFlags : register(u0);
RWStructuredBuffer<GridCell> DestinationGrid : register(u1);

static const uint BodyBlocked = 1;
static const uint BodyContainsMetal = 2;
static const uint BodyActive = 4;

bool IsMovableSolid(GridCell cell)
{
    return cell.IsActive != 0 && IsFallingSolid(cell.MaterialId) && cell.BodyId != 0;
}

bool IsSolidMaterial(uint materialId)
{
    return materialId == 3 || materialId == 4 || materialId == 7;
}

bool BodyMoves(GridCell cell)
{
    if (!IsMovableSolid(cell))
    {
        return false;
    }
    uint flags = SourceBodyFlags[cell.BodyId - 1];
    if ((flags & (BodyBlocked | BodyActive)) != BodyActive)
    {
        return false;
    }
    return SolidPass == 0 || (flags & BodyContainsMetal) == 0;
}

[numthreads(16, 16, 1)]
void AnalyzeSolidBodies(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= Width || coordinate.y >= Height)
    {
        return;
    }
    uint index = FlattenCoordinate(coordinate);
    GridCell cell = SourceGrid[index];
    if (!IsMovableSolid(cell))
    {
        return;
    }

    uint flags = BodyActive | (cell.MaterialId == 3 ? BodyContainsMetal : 0);
    if (coordinate.y + 1 >= Height)
    {
        flags |= BodyBlocked;
    }
    else
    {
        GridCell below = SourceGrid[index + Width];
        bool solidObstacle = below.IsActive != 0 && IsSolidMaterial(below.MaterialId);
        if (solidObstacle && below.BodyId != cell.BodyId)
        {
            flags |= BodyBlocked;
        }
    }
    uint ignored;
    InterlockedOr(BodyFlags[cell.BodyId - 1], flags, ignored);
}

[numthreads(16, 16, 1)]
void MoveSolidBodies(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= Width || coordinate.y >= Height)
    {
        return;
    }
    uint index = FlattenCoordinate(coordinate);
    GridCell current = SourceGrid[index];
    bool aboveMoves = coordinate.y > 0 && BodyMoves(SourceGrid[index - Width]);
    if (aboveMoves)
    {
        GridCell moved = SourceGrid[index - Width];
        moved.RestFrames = 0;
        DestinationGrid[index] = moved;
        return;
    }
    if (!BodyMoves(current))
    {
        if (IsMovableSolid(current))
        {
            current.RestFrames = min(current.RestFrames + 1, 2);
        }
        DestinationGrid[index] = current;
        return;
    }

    GridCell replacement = CreateEmptyCell();
    for (uint y = coordinate.y + 1; y < Height; y++)
    {
        GridCell sample = SourceGrid[y * Width + coordinate.x];
        if (!IsMovableSolid(sample) || sample.BodyId != current.BodyId)
        {
            replacement = sample;
            replacement.RestFrames = 0;
            replacement.VelocityX = 0;
            replacement.VelocityY = 0;
            break;
        }
    }
    DestinationGrid[index] = replacement;
}
