#include "PhysicsShared.hlsli"

StructuredBuffer<GridCell> Grid : register(t0);
StructuredBuffer<uint> SourceParents : register(t1);
StructuredBuffer<MaterialProperties> Materials : register(t2);
RWStructuredBuffer<uint> Parents : register(u0);
RWStructuredBuffer<GridCell> WritableGrid : register(u1);
RWStructuredBuffer<uint> WritableOrigins : register(u2);

bool IsComponentCell(GridCell cell)
{
    return cell.IsActive != 0 && IsMovableSolidMaterial(Materials[cell.MaterialIndex]);
}

bool SameBodyClass(GridCell first, GridCell second)
{
    // Contact with a vessel must not weld a free frozen piece into its hull.
    uint firstFlags = Materials[first.MaterialIndex].Flags;
    uint secondFlags = Materials[second.MaterialIndex].Flags;
    return ((firstFlags ^ secondFlags) & MaterialFlagDensityBody) == 0 &&
        ((firstFlags & MaterialFlagDensityBody) == 0 || first.MaterialIndex == second.MaterialIndex);
}

uint FindRoot(uint index)
{
    uint root = index;
    for (uint step = 0; step < 32; step++)
    {
        uint parent = Parents[root];
        if (parent == root)
        {
            break;
        }
        root = parent;
    }
    return root;
}

void Join(uint first, uint second)
{
    uint firstRoot = FindRoot(first);
    uint secondRoot = FindRoot(second);
    if (firstRoot == secondRoot)
    {
        return;
    }
    uint higher = max(firstRoot, secondRoot);
    uint lower = min(firstRoot, secondRoot);
    uint ignored;
    InterlockedMin(Parents[higher], lower, ignored);
}

[numthreads(256, 1, 1)]
void InitializeComponents(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint index = dispatchThreadId.x;
    if (index >= Width * Height)
    {
        return;
    }
    Parents[index] = IsComponentCell(Grid[index]) ? index : 0xffffffff;
}

[numthreads(16, 16, 1)]
void UnionComponents(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= Width || coordinate.y >= Height)
    {
        return;
    }
    uint index = FlattenCoordinate(coordinate);
    if (!IsComponentCell(Grid[index]))
    {
        return;
    }
    if (coordinate.x + 1 < Width && IsComponentCell(Grid[index + 1]) && SameBodyClass(Grid[index], Grid[index + 1]))
    {
        Join(index, index + 1);
    }
    if (coordinate.y + 1 < Height && IsComponentCell(Grid[index + Width]) && SameBodyClass(Grid[index], Grid[index + Width]))
    {
        Join(index, index + Width);
    }
    // A rotated raster may contain diagonal contacts. Preserve contacts of
    // the same existing body; do not weld unrelated corner-touching pieces.
    if(coordinate.y+1<Height && Grid[index].BodyId!=0)
    {
        if(coordinate.x+1<Width && IsComponentCell(Grid[index+Width+1]) &&
            Grid[index+Width+1].BodyId==Grid[index].BodyId && SameBodyClass(Grid[index],Grid[index+Width+1]))
            Join(index,index+Width+1);
        if(coordinate.x>0 && IsComponentCell(Grid[index+Width-1]) &&
            Grid[index+Width-1].BodyId==Grid[index].BodyId && SameBodyClass(Grid[index],Grid[index+Width-1]))
            Join(index,index+Width-1);
    }
}

[numthreads(256, 1, 1)]
void CompressComponents(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint index = dispatchThreadId.x;
    if (index >= Width * Height || Parents[index] == 0xffffffff)
    {
        return;
    }
    Parents[index] = FindRoot(index);
}

[numthreads(256, 1, 1)]
void FinalizeComponents(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint index = dispatchThreadId.x;
    if (index >= Width * Height)
    {
        return;
    }
    GridCell cell = WritableGrid[index];
    if (IsComponentCell(cell))
    {
        if(cell.BodyId==0)WritableOrigins[index]=0;
        cell.BodyId = SourceParents[index] + 1;
        WritableGrid[index] = cell;
    }
}
