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

bool IsAnchored(uint2 coordinate, LatticeParticle particle)
{
    if (coordinate.y + 2 >= Height)
    {
        return true;
    }

    for (uint neighbor = 0; neighbor < 8; neighbor++)
    {
        int2 adjacentCoordinate = int2(coordinate) + NeighborOffsets[neighbor];
        if (adjacentCoordinate.x < 0 || adjacentCoordinate.y < 0 ||
            adjacentCoordinate.x >= int(Width) || adjacentCoordinate.y >= int(Height))
        {
            continue;
        }

        LatticeParticle adjacent = SourceParticles[FlattenCoordinate(uint2(adjacentCoordinate))];
        if (adjacent.IsActive != 0 && adjacent.BodyId != particle.BodyId && adjacent.IsDynamic == 0)
        {
            return true;
        }
    }

    return false;
}

bool HasDynamicNeighbor(uint2 coordinate, LatticeParticle particle)
{
    for (uint neighbor = 0; neighbor < 8; neighbor++)
    {
        int2 adjacentCoordinate = int2(coordinate) + NeighborOffsets[neighbor];
        if (adjacentCoordinate.x < 0 || adjacentCoordinate.y < 0 ||
            adjacentCoordinate.x >= int(Width) || adjacentCoordinate.y >= int(Height))
        {
            continue;
        }

        LatticeParticle adjacent = SourceParticles[FlattenCoordinate(uint2(adjacentCoordinate))];
        if (adjacent.IsActive != 0 && adjacent.BodyId == particle.BodyId && adjacent.IsDynamic != 0)
        {
            return true;
        }
    }

    return false;
}

float CalculateExternalLoad(
    uint2 coordinate,
    LatticeParticle particle,
    out float granularLoad,
    out float liquidImpactLoad)
{
    uint2 loadCoordinate = uint2(clamp(
        float2(particle.PositionX, particle.PositionY),
        float2(0, 0),
        float2(Width - 1, Height - 1)));
    uint index = FlattenCoordinate(loadCoordinate);
    GridCell occupiedCell = Grid[index];
    float load = max(0, occupiedCell.Pressure - particle.Stress);
    granularLoad = 0;
    liquidImpactLoad = 0;
    for (uint distance = 1; distance <= 128 && loadCoordinate.y >= distance; distance++)
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

        float layerLoad = cellAbove.Mass * Materials[cellAbove.MaterialId].Density;
        load += layerLoad;
        granularLoad += kind == 1 ? layerLoad : 0;
        float impactSpeed = abs(cellAbove.VelocityY) + abs(cellAbove.VelocityX) * 0.35;
        liquidImpactLoad += kind == 4 ? layerLoad * saturate(impactSpeed * 0.02) : 0;
    }

    return load;
}

float CalculateGranularColumnLoad(int sampleX, uint centerY)
{
    if (sampleX < 0 || sampleX >= int(Width))
    {
        return 0;
    }

    float columnLoad = 0;
    for (int distance = -128; distance <= 128; distance++)
    {
        int sampleY = int(centerY) + distance;
        if (sampleY < 0 || sampleY >= int(Height))
        {
            continue;
        }

        GridCell cell = Grid[FlattenCoordinate(uint2(sampleX, sampleY))];
        uint kind = cell.IsActive == 0 ? 0 : Materials[cell.MaterialId].SimulationKind;
        columnLoad += kind == 1 ? cell.Mass * Materials[cell.MaterialId].Density : 0;
    }

    return columnLoad;
}

float CalculateNearbyGranularLoad(uint2 coordinate)
{
    float nearbyLoad = 0;
    float totalWeight = 0;
    for (int offset = -64; offset <= 64; offset += 8)
    {
        int sampleX = int(coordinate.x) + offset;
        if (sampleX < 0 || sampleX >= int(Width))
        {
            continue;
        }

        float columnLoad = CalculateGranularColumnLoad(sampleX, coordinate.y);
        float weight = 1 - abs(offset) / 72.0;
        nearbyLoad += columnLoad * weight;
        totalWeight += weight;
    }

    return nearbyLoad / max(totalWeight, 0.001);
}

float ConstrainDuctileSag(uint2 coordinate, LatticeParticle particle, float proposedSag)
{
    float minimumSag = -10000;
    float maximumSag = 10000;
    uint samples = 0;
    uint cardinalNeighbors[4] = { 1, 3, 4, 6 };
    for (uint sample = 0; sample < 4; sample++)
    {
        uint neighbor = cardinalNeighbors[sample];
        int2 neighborCoordinate = int2(coordinate) + NeighborOffsets[neighbor];
        if (neighborCoordinate.x < 0 || neighborCoordinate.y < 0 ||
            neighborCoordinate.x >= int(Width) || neighborCoordinate.y >= int(Height))
        {
            continue;
        }

        LatticeParticle neighborParticle = SourceParticles[FlattenCoordinate(uint2(neighborCoordinate))];
        if (neighborParticle.IsActive == 0 || neighborParticle.BodyId != particle.BodyId)
        {
            continue;
        }

        float neighborSag = neighborParticle.PositionY - (neighborCoordinate.y + 0.5);
        float tolerance = NeighborOffsets[neighbor].y == 0 ? 0.75 : 0.5;
        minimumSag = max(minimumSag, neighborSag - tolerance);
        maximumSag = min(maximumSag, neighborSag + tolerance);
        samples++;
    }

    return samples > 0 ? clamp(proposedSag, minimumSag, maximumSag) : proposedSag;
}

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= Width || dispatchThreadId.y >= Height)
    {
        return;
    }

    uint2 coordinate = dispatchThreadId.xy + uint2(DispatchOffsetX, DispatchOffsetY);
    if (coordinate.x >= Width || coordinate.y >= Height)
    {
        return;
    }

    uint index = FlattenCoordinate(coordinate);
    LatticeParticle particle = SourceParticles[index];
    if (particle.IsActive == 0)
    {
        return;
    }

    MaterialProperties material = Materials[particle.MaterialId];
    LatticeBond bond = SourceBonds[index];
    float granularLoad;
    float liquidImpactLoad;
    float externalLoad = CalculateExternalLoad(coordinate, particle, granularLoad, liquidImpactLoad);
    float failureLoad = granularLoad + liquidImpactLoad;
    float loadStrain = failureLoad * 0.004 / max(material.Density, 0.1);
    bool anchored = IsAnchored(coordinate, particle);
    if (anchored)
    {
        particle.IsDynamic = 0;
    }
    else if (particle.IsDynamic == 0 &&
        (material.FailureMode == 1 || granularLoad >= material.ActivationLoad ||
        HasDynamicNeighbor(coordinate, particle)))
    {
        particle.IsDynamic = 1;
    }

    if (material.FailureMode == 1 && loadStrain > material.PlasticLimit)
    {
        particle.IsDynamic |= 2;
    }

    if (particle.IsDynamic == 0)
    {
        particle.VelocityX = 0;
        particle.VelocityY = 0;
        particle.Stress = loadStrain;
        bond.MaximumStrain = loadStrain;
        bond.AccumulatedLoad = lerp(bond.AccumulatedLoad, externalLoad, 0.18);
        DestinationParticles[index] = particle;
        DestinationBonds[index] = bond;
        return;
    }

    if ((particle.IsDynamic & 4) != 0)
    {
        float forcedSubstep = DeltaTime / 4;
        float2 forcedVelocity = float2(particle.VelocityX, particle.VelocityY);
        if (SolverIteration == 0)
        {
            forcedVelocity.y += Gravity * DeltaTime;
        }

        float2 forcedPosition = float2(particle.PositionX, particle.PositionY) + forcedVelocity * forcedSubstep;
        forcedPosition.x = clamp(forcedPosition.x, 0.5, Width - 0.5);
        if (forcedPosition.y >= Height - 0.5)
        {
            forcedPosition.y = Height - 0.5;
            forcedVelocity.y = min(0, -forcedVelocity.y * material.Restitution);
            forcedVelocity.x *= 1 - material.Friction;
        }

        particle.PositionX = forcedPosition.x;
        particle.PositionY = forcedPosition.y;
        particle.VelocityX = clamp(forcedVelocity.x, -MaximumVelocity, MaximumVelocity);
        particle.VelocityY = clamp(forcedVelocity.y, -MaximumVelocity, MaximumVelocity);
        particle.Stress = 0;
        DestinationParticles[index] = particle;
        DestinationBonds[index] = bond;
        return;
    }

    if (material.FailureMode == 1)
    {
        float nearbyGranularLoad = max(granularLoad, CalculateNearbyGranularLoad(coordinate));
        float criticalLoad = material.Density * material.PlasticLimit * 1.95 / 0.004;
        uint fractureSampleY = coordinate.y / 16 * 16;
        float columnLoad = CalculateGranularColumnLoad(coordinate.x, fractureSampleY);
        bool criticallyLoaded = columnLoad >= criticalLoad;
        bool nearCriticalBoundary = columnLoad >= criticalLoad * 0.8;
        if ((particle.IsDynamic & 8) == 0 && nearCriticalBoundary)
        {
            bool leftCriticallyLoaded = coordinate.x > 0 &&
                CalculateGranularColumnLoad(int(coordinate.x) - 1, fractureSampleY) >= criticalLoad;
            bool rightCriticallyLoaded = coordinate.x + 1 < Width &&
                CalculateGranularColumnLoad(int(coordinate.x) + 1, fractureSampleY) >= criticalLoad;
            if (criticallyLoaded != rightCriticallyLoaded)
            {
                bond.ActiveNeighborMask &= ~((1u << 4) | (1u << 7));
            }
            if (criticallyLoaded != leftCriticallyLoaded)
            {
                bond.ActiveNeighborMask &= ~(1u << 5);
            }
            particle.IsDynamic |= criticallyLoaded ? 12 : 8;
        }

        float nearbyStrain = nearbyGranularLoad * 0.004 / max(material.Density, 0.1);
        float yieldStrain = max(loadStrain, nearbyStrain);
        if (yieldStrain > material.PlasticLimit)
        {
            particle.IsDynamic |= 2;
            float plasticTarget = min(18, (yieldStrain - material.PlasticLimit) * 1400);
            particle.PlasticOffsetY = lerp(particle.PlasticOffsetY, plasticTarget, 0.16);
        }

        float loadSag = min(36, nearbyGranularLoad * 0.65);
        float2 targetPosition = float2(coordinate) + 0.5 +
            float2(particle.PlasticOffsetX, particle.PlasticOffsetY + loadSag);
        float2 resolvedPosition = lerp(float2(particle.PositionX, particle.PositionY), targetPosition, 0.16);
        float proposedSag = resolvedPosition.y - (coordinate.y + 0.5);
        resolvedPosition.y = coordinate.y + 0.5 + ConstrainDuctileSag(coordinate, particle, proposedSag);
        particle.PositionX = resolvedPosition.x;
        particle.PositionY = resolvedPosition.y;
        particle.VelocityX = 0;
        particle.VelocityY = 0;
        particle.Stress = nearbyStrain;
        bond.MaximumStrain = nearbyStrain;
        bond.AccumulatedLoad = lerp(bond.AccumulatedLoad, nearbyGranularLoad, 0.18);
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
        float structuralGravity = countbits(bond.ActiveNeighborMask) > 0 && (particle.IsDynamic & 4) == 0 ? 0.01 : 1;
        velocity.y += Gravity * (1 - buoyancyRatio) * DeltaTime * structuralGravity;
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
        int2 neighborCoordinate = int2(coordinate) + NeighborOffsets[neighbor];
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
        if (strain > constraintBond.PlasticLimit && material.FailureMode == 2 &&
            (particle.IsDynamic & 4) == 0)
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

    float shapeMemory = 0;
    if (material.FailureMode == 2 && loadStrain < material.PlasticLimit &&
        (particle.IsDynamic & 4) == 0)
    {
        shapeMemory = 0.08;
    }
    if (activeConstraints > 0 && shapeMemory > 0)
    {
        float2 restPosition = float2(coordinate) + 0.5 +
            float2(particle.PlasticOffsetX, particle.PlasticOffsetY);
        correction += (restPosition - predictedPosition) * shapeMemory;
    }

    maximumStrain = max(maximumStrain, loadStrain);
    float loadForceScale = material.FailureMode == 2 ? 0.35 : 3;
    velocity.y += externalLoad / max(material.Density, 0.1) * DeltaTime * loadForceScale;
    if (loadStrain > material.PlasticLimit && material.FailureMode == 2 &&
        (particle.IsDynamic & 4) == 0)
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
        if (otherParticle.BodyId != particle.BodyId)
        {
            float2 otherVelocity = float2(otherParticle.VelocityX, otherParticle.VelocityY);
            float relativeSpeed = length(velocity - otherVelocity);
            float combinedMass = max(particle.Mass + otherParticle.Mass, 0.01);
            velocity = (velocity * particle.Mass + otherVelocity * otherParticle.Mass) / combinedMass;
            resolvedPosition = sourcePosition;
            externalLoad += relativeSpeed * otherParticle.Mass;
            maximumStrain = max(maximumStrain, relativeSpeed * 0.0015);
        }
    }

    velocity += (resolvedPosition - predictedPosition) / max(substep, 0.0001) * 0.28;
    velocity *= activeConstraints > 0 ? 0.97 : 0.996;
    if (length(velocity) < 0.02 && length(correction) < 0.001)
    {
        velocity = 0;
    }
    particle.PositionX = resolvedPosition.x;
    particle.PositionY = resolvedPosition.y;
    particle.VelocityX = clamp(velocity.x, -MaximumVelocity, MaximumVelocity);
    particle.VelocityY = clamp(velocity.y, -MaximumVelocity, MaximumVelocity);
    particle.Stress = maximumStrain;
    if (material.FailureMode == 1 && loadStrain > material.PlasticLimit)
    {
        float2 originalPosition = float2(coordinate) + 0.5;
        float2 targetOffset = resolvedPosition - originalPosition;
        targetOffset.y = max(targetOffset.y, min(30, (loadStrain - material.PlasticLimit) * 1000));
        float2 plasticOffset = lerp(
            float2(particle.PlasticOffsetX, particle.PlasticOffsetY),
            targetOffset,
            0.08);
        particle.PlasticOffsetX = plasticOffset.x;
        particle.PlasticOffsetY = plasticOffset.y;
    }
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

    if (material.FailureMode == 1 && loadStrain > material.PlasticLimit)
    {
        float plasticGrowth = min((loadStrain - material.PlasticLimit) * 0.002, 0.001);
        bond.CardinalRestLength *= 1 + plasticGrowth;
        bond.DiagonalRestLength *= 1 + plasticGrowth;
    }

    DestinationBonds[index] = bond;
}
