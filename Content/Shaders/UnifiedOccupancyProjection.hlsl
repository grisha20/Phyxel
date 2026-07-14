#include "PhysicsShared.hlsli"

StructuredBuffer<GridCell> SourceGrid : register(t0);
StructuredBuffer<LatticeParticle> Particles : register(t1);
StructuredBuffer<MaterialProperties> Materials : register(t2);
RWStructuredBuffer<GridCell> DestinationGrid : register(u0);

bool TryDisplaceCell(uint2 origin, GridCell displacedCell, LatticeParticle particle)
{
    int horizontalDirection = particle.VelocityX < 0 ? -1 : 1;
    int2 offsets[8] =
    {
        int2(horizontalDirection, 0),
        int2(-horizontalDirection, 0),
        int2(0, -1),
        int2(horizontalDirection, -1),
        int2(-horizontalDirection, -1),
        int2(horizontalDirection, 1),
        int2(-horizontalDirection, 1),
        int2(0, 1)
    };
    for (uint candidate = 0; candidate < 8; candidate++)
    {
        int2 coordinate = int2(origin) + offsets[candidate];
        if (coordinate.x < 0 || coordinate.y < 0 || coordinate.x >= int(Width) || coordinate.y >= int(Height))
        {
            continue;
        }

        uint destinationIndex = FlattenCoordinate(uint2(coordinate));
        uint originalState;
        InterlockedCompareExchange(DestinationGrid[destinationIndex].IsActive, 0, 2, originalState);
        if (originalState != 0)
        {
            continue;
        }

        displacedCell.VelocityX += particle.VelocityX * 0.65;
        displacedCell.VelocityY += particle.VelocityY * 0.65;
        displacedCell.Pressure += length(float2(particle.VelocityX, particle.VelocityY)) * particle.Mass * 0.01;
        displacedCell.IsActive = 1;
        DestinationGrid[destinationIndex] = displacedCell;
        return true;
    }

    return false;
}

[numthreads(16, 16, 1)]
void ClearLatticeOccupancy(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy + uint2(DispatchOffsetX, DispatchOffsetY);
    if (coordinate.x >= Width || coordinate.y >= Height)
    {
        return;
    }

    uint index = FlattenCoordinate(coordinate);
    GridCell cell = SourceGrid[index];
    if (cell.IsActive != 0 && Materials[cell.MaterialId].SimulationKind == 2)
    {
        DestinationGrid[index] = CreateEmptyCell();
    }
    else
    {
        DestinationGrid[index] = cell;
    }
}

[numthreads(256, 1, 1)]
void ProjectLatticeOccupancy(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint particleIndex = dispatchThreadId.x;
    if (particleIndex >= ParticleCount)
    {
        return;
    }

    LatticeParticle particle = Particles[particleIndex];
    if (particle.IsActive == 0)
    {
        return;
    }

    uint2 coordinate = uint2(clamp(
        float2(particle.PositionX, particle.PositionY),
        float2(0, 0),
        float2(Width - 1, Height - 1)));
    uint targetIndex = FlattenCoordinate(coordinate);
    GridCell existingCell = DestinationGrid[targetIndex];
    uint expectedState = existingCell.IsActive == 0 ? 0 : 1;
    uint originalState;
    InterlockedCompareExchange(DestinationGrid[targetIndex].IsActive, expectedState, 2, originalState);
    if (originalState != expectedState)
    {
        return;
    }

    if (expectedState != 0 && IsCellularMaterial(Materials[existingCell.MaterialId].SimulationKind))
    {
        TryDisplaceCell(coordinate, existingCell, particle);
    }

    GridCell latticeCell = (GridCell)0;
    latticeCell.MaterialId = particle.MaterialId;
    latticeCell.Mass = particle.Mass;
    latticeCell.VelocityX = particle.VelocityX;
    latticeCell.VelocityY = particle.VelocityY;
    latticeCell.Pressure = particle.Stress;
    latticeCell.IsActive = 1;
    latticeCell.LatticeParticleIndex = particleIndex;
    DestinationGrid[targetIndex] = latticeCell;
}
