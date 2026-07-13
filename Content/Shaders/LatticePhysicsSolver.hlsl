#include "PhysicsShared.hlsli"

StructuredBuffer<LatticeParticle> SourceParticles : register(t0);
StructuredBuffer<LatticeBond> SourceBonds : register(t1);
StructuredBuffer<GridCell> Grid : register(t2);
StructuredBuffer<MaterialProperties> Materials : register(t3);
RWStructuredBuffer<LatticeParticle> DestinationParticles : register(u0);
RWStructuredBuffer<LatticeBond> DestinationBonds : register(u1);

static const int2 NeighborOffsets[8] =
{
    int2(-1, -1), int2(0, -1), int2(1, -1), int2(-1, 0),
    int2(1, 0), int2(-1, 1), int2(0, 1), int2(1, 1)
};

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= Width || dispatchThreadId.y >= Height)
    {
        return;
    }

    uint index = FlattenCoordinate(dispatchThreadId.xy);
    LatticeParticle particle = SourceParticles[index];
    if (particle.IsActive == 0)
    {
        return;
    }

    MaterialProperties material = Materials[particle.MaterialId];
    float substep = DeltaTime * 0.25;
    if (SolverIteration == 0)
    {
        particle.VelocityY = min(particle.VelocityY + Gravity * DeltaTime, MaximumVelocity);
    }

    float2 position = float2(particle.PositionX, particle.PositionY);
    float2 correction = 0;
    float maximumStrain = 0;
    uint activeNeighbors = 0;
    for (uint neighbor = 0; neighbor < 8; neighbor++)
    {
        int2 neighborCoordinate = int2(dispatchThreadId.xy) + NeighborOffsets[neighbor];
        if (neighborCoordinate.x < 0 || neighborCoordinate.y < 0 || neighborCoordinate.x >= int(Width) || neighborCoordinate.y >= int(Height))
        {
            continue;
        }

        uint neighborIndex = FlattenCoordinate(uint2(neighborCoordinate));
        LatticeParticle neighborParticle = SourceParticles[neighborIndex];
        if (neighborParticle.IsActive == 0 || neighborParticle.MaterialId != particle.MaterialId)
        {
            continue;
        }

        float2 separation = float2(neighborParticle.PositionX, neighborParticle.PositionY) - position;
        float currentLength = max(length(separation), 0.0001);
        float restLength = abs(NeighborOffsets[neighbor].x) + abs(NeighborOffsets[neighbor].y) == 2 ? 1.41421356 : 1;
        float strain = abs(currentLength - restLength) / restLength;
        maximumStrain = max(maximumStrain, strain);
        if (strain <= material.PlasticLimit)
        {
            correction += normalize(separation) * (currentLength - restLength) * 0.12;
            activeNeighbors++;
        }
    }

    if (activeNeighbors > 0)
    {
        position += correction / activeNeighbors;
    }

    float2 velocity = float2(particle.VelocityX, particle.VelocityY);
    if (maximumStrain > material.PlasticLimit)
    {
        velocity += normalize(float2(HashUnitFloat(index) - 0.5, HashUnitFloat(index ^ FrameIndex) - 0.5) + 0.001) * 18;
    }

    position += velocity * substep;
    position.x = clamp(position.x, 0.5, Width - 0.5);
    if (position.y >= Height - 0.5)
    {
        position.y = Height - 0.5;
        velocity.y *= -material.Restitution;
        velocity.x *= 1 - material.Friction;
    }

    uint2 collisionCoordinate = uint2(clamp(position, float2(0, 0), float2(Width - 1, Height - 1)));
    GridCell collisionCell = Grid[FlattenCoordinate(collisionCoordinate)];
    if (collisionCell.IsActive != 0 && Materials[collisionCell.MaterialId].SimulationKind == 1)
    {
        velocity += float2(collisionCell.VelocityX, collisionCell.VelocityY) * 0.02 / max(particle.Mass, 0.01);
        position.y -= 0.15;
    }

    velocity *= 0.998;
    particle.PositionX = position.x;
    particle.PositionY = position.y;
    particle.VelocityX = velocity.x;
    particle.VelocityY = velocity.y;
    particle.Stress = maximumStrain;
    DestinationParticles[index] = particle;

    LatticeBond bond = SourceBonds[index];
    if (bond.IsActive != 0)
    {
        LatticeParticle other = SourceParticles[min(bond.ParticleB, ParticleCount - 1)];
        float currentLength = length(float2(other.PositionX, other.PositionY) - position);
        float strain = abs(currentLength - bond.RestLength) / max(bond.RestLength, 0.0001);
        bond.CurrentLength = currentLength;
        bond.AccumulatedStrain = max(bond.AccumulatedStrain, strain);
        if (strain > bond.PlasticLimit)
        {
            bond.IsActive = 0;
        }
        else if (strain > bond.ElasticLimit)
        {
            bond.RestLength = lerp(bond.RestLength, currentLength, 0.08);
        }

        DestinationBonds[index] = bond;
    }
}
