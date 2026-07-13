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

float2 SampleEnvironmentalVelocity(float2 position, out float displacedDensity, out float granularResistance)
{
    int2 center = int2(clamp(position, float2(0, 0), float2(Width - 1, Height - 1)));
    int2 sampleOffsets[5] = { int2(0, 0), int2(-1, 0), int2(1, 0), int2(0, -1), int2(0, 1) };
    float2 velocity = 0;
    displacedDensity = 0;
    granularResistance = 0;
    float fluidSamples = 0;
    for (uint sample = 0; sample < 5; sample++)
    {
        int2 coordinate = center + sampleOffsets[sample];
        if (coordinate.x < 0 || coordinate.y < 0 || coordinate.x >= int(Width) || coordinate.y >= int(Height))
        {
            continue;
        }

        GridCell cell = Grid[FlattenCoordinate(uint2(coordinate))];
        if (cell.IsActive == 0)
        {
            continue;
        }

        uint kind = Materials[cell.MaterialId].SimulationKind;
        if (kind == 4)
        {
            float fill = saturate(cell.Mass);
            displacedDensity += Materials[cell.MaterialId].Density * fill;
            velocity += float2(cell.VelocityX, cell.VelocityY) * fill;
            fluidSamples += fill;
        }
        else if (kind == 1)
        {
            granularResistance += Materials[cell.MaterialId].Density * cell.Mass;
        }
    }

    displacedDensity /= 5;
    granularResistance /= 5;
    return fluidSamples > 0 ? velocity / fluidSamples : 0;
}

float CalculateExternalLoad(uint2 coordinate, LatticeParticle particle)
{
    uint index = FlattenCoordinate(coordinate);
    GridCell occupiedCell = Grid[index];
    float load = max(0, occupiedCell.Pressure - particle.Stress);
    for (uint distance = 1; distance <= 6 && coordinate.y >= distance; distance++)
    {
        GridCell cellAbove = Grid[index - Width * distance];
        if (cellAbove.IsActive == 0)
        {
            break;
        }

        uint kind = Materials[cellAbove.MaterialId].SimulationKind;
        if (!IsCellularMaterial(kind))
        {
            break;
        }

        load += cellAbove.Mass * Materials[cellAbove.MaterialId].Density * Gravity * DeltaTime;
    }

    return load;
}

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
    LatticeBond bond = SourceBonds[index];
    if (particle.IsDynamic == 0)
    {
        particle.VelocityX = 0;
        particle.VelocityY = 0;
        particle.Stress = 0;
        bond.MaximumStrain = 0;
        bond.AccumulatedLoad = 0;
        DestinationParticles[index] = particle;
        DestinationBonds[index] = bond;
        return;
    }

    float substep = DeltaTime / 4;
    float2 sourcePosition = float2(particle.PositionX, particle.PositionY);
    float2 velocity = float2(particle.VelocityX, particle.VelocityY);
    float displacedDensity;
    float granularResistance;
    float2 environmentalVelocity = SampleEnvironmentalVelocity(sourcePosition, displacedDensity, granularResistance);
    if (SolverIteration == 0)
    {
        float buoyancyRatio = displacedDensity / max(material.Density, 0.01);
        velocity.y += Gravity * (1 - buoyancyRatio) * DeltaTime;
        float fluidDrag = saturate(displacedDensity * 0.045);
        velocity = lerp(velocity, environmentalVelocity, fluidDrag);
    }

    float granularDrag = saturate(granularResistance * 0.012);
    velocity.x *= 1 - granularDrag;
    velocity.y *= 1 - granularDrag * 0.35;
    float2 predictedPosition = sourcePosition + velocity * substep;
    float2 correction = 0;
    float maximumStrain = 0;
    float cardinalPlasticLength = 0;
    float diagonalPlasticLength = 0;
    uint cardinalPlasticSamples = 0;
    uint diagonalPlasticSamples = 0;
    uint activeConstraints = 0;
    uint activeMask = bond.ActiveNeighborMask;
    for (uint neighbor = 0; neighbor < 8; neighbor++)
    {
        uint neighborBit = 1u << neighbor;
        int2 neighborCoordinate = int2(dispatchThreadId.xy) + NeighborOffsets[neighbor];
        if (neighborCoordinate.x < 0 || neighborCoordinate.y < 0 || neighborCoordinate.x >= int(Width) || neighborCoordinate.y >= int(Height))
        {
            if (neighbor >= 4)
            {
                activeMask &= ~neighborBit;
            }

            continue;
        }

        uint neighborIndex = FlattenCoordinate(uint2(neighborCoordinate));
        LatticeParticle neighborParticle = SourceParticles[neighborIndex];
        uint reciprocalNeighbor = 7 - neighbor;
        bool ownsConstraint = neighbor >= 4;
        LatticeBond constraintBond = bond;
        if (!ownsConstraint)
        {
            constraintBond = SourceBonds[neighborIndex];
        }

        uint constraintBit = ownsConstraint ? neighborBit : 1u << reciprocalNeighbor;
        if ((constraintBond.ActiveNeighborMask & constraintBit) == 0)
        {
            continue;
        }

        if (neighborParticle.IsActive == 0 || neighborParticle.MaterialId != particle.MaterialId ||
            neighborParticle.BodyId != particle.BodyId)
        {
            if (ownsConstraint)
            {
                activeMask &= ~neighborBit;
            }

            continue;
        }

        float2 separation = float2(neighborParticle.PositionX, neighborParticle.PositionY) - predictedPosition;
        float currentLength = max(length(separation), 0.0001);
        bool diagonal = abs(NeighborOffsets[neighbor].x) + abs(NeighborOffsets[neighbor].y) == 2;
        float restLength = diagonal ? constraintBond.DiagonalRestLength : constraintBond.CardinalRestLength;
        float strain = abs(currentLength - restLength) / max(restLength, 0.0001);
        maximumStrain = max(maximumStrain, strain);
        if (strain > constraintBond.PlasticLimit)
        {
            if (ownsConstraint)
            {
                activeMask &= ~neighborBit;
            }

            velocity += normalize(-separation + 0.001) * min(strain * 22, 80);
            continue;
        }

        correction += normalize(separation) * (currentLength - restLength) * 0.22;
        activeConstraints++;
        if (ownsConstraint && strain > bond.ElasticLimit)
        {
            if (diagonal)
            {
                diagonalPlasticLength += currentLength;
                diagonalPlasticSamples++;
            }
            else
            {
                cardinalPlasticLength += currentLength;
                cardinalPlasticSamples++;
            }
        }
    }

    if (activeConstraints > 0)
    {
        correction /= activeConstraints;
    }

    float externalLoad = CalculateExternalLoad(dispatchThreadId.xy, particle);
    float loadStrain = externalLoad * 0.0025 / max(material.Density, 0.1);
    maximumStrain = max(maximumStrain, loadStrain);
    if (loadStrain > material.PlasticLimit)
    {
        uint weakestNeighbor = 4 + (HashValue(index ^ FrameIndex) & 3);
        activeMask &= ~(1u << weakestNeighbor);
    }

    float2 resolvedPosition = predictedPosition + correction;
    resolvedPosition.x = clamp(resolvedPosition.x, 0.5, Width - 0.5);
    if (resolvedPosition.y >= Height - 0.5)
    {
        resolvedPosition.y = Height - 0.5;
        velocity.y = min(0, -velocity.y * material.Restitution);
        velocity.x *= 1 - material.Friction;
        externalLoad += particle.Mass * abs(velocity.y);
    }

    uint2 collisionCoordinate = uint2(clamp(resolvedPosition, float2(0, 0), float2(Width - 1, Height - 1)));
    GridCell collisionCell = Grid[FlattenCoordinate(collisionCoordinate)];
    if (collisionCell.IsActive != 0 && Materials[collisionCell.MaterialId].SimulationKind == 2 &&
        collisionCell.LatticeParticleIndex != index)
    {
        uint otherIndex = min(collisionCell.LatticeParticleIndex, ParticleCount - 1);
        LatticeParticle otherParticle = SourceParticles[otherIndex];
        float2 otherVelocity = float2(otherParticle.VelocityX, otherParticle.VelocityY);
        float relativeSpeed = length(velocity - otherVelocity);
        float combinedMass = max(particle.Mass + otherParticle.Mass, 0.01);
        velocity = (velocity * particle.Mass + otherVelocity * otherParticle.Mass) / combinedMass;
        resolvedPosition = sourcePosition;
        externalLoad += relativeSpeed * otherParticle.Mass;
        maximumStrain = max(maximumStrain, relativeSpeed * 0.0015);
    }

    velocity += (resolvedPosition - predictedPosition) / max(substep, 0.0001) * 0.28;
    velocity *= 0.996;
    particle.PositionX = resolvedPosition.x;
    particle.PositionY = resolvedPosition.y;
    particle.VelocityX = clamp(velocity.x, -MaximumVelocity, MaximumVelocity);
    particle.VelocityY = clamp(velocity.y, -MaximumVelocity, MaximumVelocity);
    particle.Stress = maximumStrain;
    DestinationParticles[index] = particle;

    bond.ActiveNeighborMask = activeMask;
    bond.MaximumStrain = maximumStrain;
    bond.AccumulatedLoad = lerp(bond.AccumulatedLoad, externalLoad, 0.18);
    if (cardinalPlasticSamples > 0)
    {
        bond.CardinalRestLength = lerp(bond.CardinalRestLength, cardinalPlasticLength / cardinalPlasticSamples, 0.035);
    }

    if (diagonalPlasticSamples > 0)
    {
        bond.DiagonalRestLength = lerp(bond.DiagonalRestLength, diagonalPlasticLength / diagonalPlasticSamples, 0.035);
    }

    DestinationBonds[index] = bond;
}
