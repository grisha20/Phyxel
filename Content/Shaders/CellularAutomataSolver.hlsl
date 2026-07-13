#include "PhysicsShared.hlsli"

StructuredBuffer<GridCell> SourceGrid : register(t0);
StructuredBuffer<LatticeParticle> SourceParticles : register(t1);
StructuredBuffer<MaterialProperties> Materials : register(t2);
RWStructuredBuffer<GridCell> DestinationGrid : register(u0);

float StableLowerMass(float totalMass)
{
    const float maximumMass = 1;
    const float maximumCompression = 0.04;
    if (totalMass <= maximumMass)
    {
        return totalMass;
    }

    if (totalMass < maximumMass * 2 + maximumCompression)
    {
        return (maximumMass * maximumMass + totalMass * maximumCompression) / (maximumMass + maximumCompression);
    }

    return (totalMass + maximumCompression) * 0.5;
}

GridCell CellWithMass(GridCell templateCell, float mass)
{
    if (mass < 0.001)
    {
        return CreateEmptyCell();
    }

    templateCell.Mass = mass;
    templateCell.IsActive = 1;
    return templateCell;
}

uint CellKind(GridCell cell)
{
    return cell.IsActive == 0 ? 0 : Materials[cell.MaterialId].SimulationKind;
}

float CellDensity(GridCell cell)
{
    return cell.IsActive == 0 ? 0 : Materials[cell.MaterialId].Density;
}

void ResolveVerticalPair(uint2 upperCoordinate)
{
    uint upperIndex = FlattenCoordinate(upperCoordinate);
    uint lowerIndex = upperIndex + Width;
    GridCell upper = SourceGrid[upperIndex];
    GridCell lower = SourceGrid[lowerIndex];
    uint upperKind = CellKind(upper);
    uint lowerKind = CellKind(lower);

    if (lowerKind == 2)
    {
        if (IsCellularMaterial(upperKind))
        {
            lower.Pressure += upper.Mass * CellDensity(upper) * Gravity * DeltaTime;
            upper.VelocityY = 0;
        }

        DestinationGrid[upperIndex] = upper;
        DestinationGrid[lowerIndex] = lower;
        return;
    }

    if (upperKind == 2)
    {
        DestinationGrid[upperIndex] = upper;
        DestinationGrid[lowerIndex] = lower;
        return;
    }

    if (upper.IsActive != 0 && lower.IsActive != 0 && upper.MaterialId != lower.MaterialId)
    {
        if (CellDensity(upper) > CellDensity(lower))
        {
            float impactVelocity = upper.VelocityY;
            upper.VelocityY = lower.VelocityY;
            lower.VelocityY = max(impactVelocity, lower.VelocityY);
            DestinationGrid[upperIndex] = lower;
            DestinationGrid[lowerIndex] = upper;
        }

        return;
    }

    if (upperKind == 1)
    {
        if (lower.IsActive == 0)
        {
            upper.VelocityY = min(upper.VelocityY + Gravity * DeltaTime, MaximumVelocity);
            DestinationGrid[upperIndex] = CreateEmptyCell();
            DestinationGrid[lowerIndex] = upper;
        }

        return;
    }

    if (lowerKind == 5 && upper.IsActive == 0)
    {
        DestinationGrid[upperIndex] = lower;
        DestinationGrid[lowerIndex] = CreateEmptyCell();
        return;
    }

    uint fluidMaterialId = upper.IsActive != 0 ? upper.MaterialId : lower.MaterialId;
    uint fluidKind = upper.IsActive != 0 ? upperKind : lowerKind;
    if (!IsFluidMaterial(fluidKind) ||
        (upper.IsActive != 0 && upper.MaterialId != fluidMaterialId) ||
        (lower.IsActive != 0 && lower.MaterialId != fluidMaterialId))
    {
        return;
    }

    GridCell templateCell = lower;
    if (upper.IsActive != 0)
    {
        templateCell = upper;
    }
    float totalMass = upper.Mass + lower.Mass;
    float stableMass = StableLowerMass(totalMass);
    float lowerMass = fluidKind == 5 ? totalMass - stableMass : stableMass;
    float upperMass = totalMass - lowerMass;
    GridCell resolvedUpper = CellWithMass(templateCell, upperMass);
    GridCell resolvedLower = CellWithMass(templateCell, lowerMass);
    float transferredMass = lowerMass - lower.Mass;
    resolvedUpper.VelocityY = max(-MaximumVelocity, min(MaximumVelocity, upper.VelocityY - transferredMass * 60));
    resolvedLower.VelocityY = max(-MaximumVelocity, min(MaximumVelocity, lower.VelocityY + transferredMass * 60));
    DestinationGrid[upperIndex] = resolvedUpper;
    DestinationGrid[lowerIndex] = resolvedLower;
}

bool CanGranularMoveDiagonally(uint2 sourceCoordinate, uint2 destinationCoordinate)
{
    if (sourceCoordinate.y + 1 >= Height || destinationCoordinate.y + 1 >= Height)
    {
        return false;
    }

    GridCell support = SourceGrid[FlattenCoordinate(uint2(sourceCoordinate.x, sourceCoordinate.y + 1))];
    GridCell destinationBelow = SourceGrid[FlattenCoordinate(uint2(destinationCoordinate.x, destinationCoordinate.y + 1))];
    return support.IsActive != 0 && destinationBelow.IsActive == 0;
}

void ResolveHorizontalPair(uint2 leftCoordinate)
{
    uint leftIndex = FlattenCoordinate(leftCoordinate);
    uint rightIndex = leftIndex + 1;
    GridCell left = SourceGrid[leftIndex];
    GridCell right = SourceGrid[rightIndex];
    uint leftKind = CellKind(left);
    uint rightKind = CellKind(right);

    if (leftKind == 1 && right.IsActive == 0 && CanGranularMoveDiagonally(leftCoordinate, leftCoordinate + uint2(1, 0)))
    {
        left.VelocityX = 24;
        DestinationGrid[leftIndex] = CreateEmptyCell();
        DestinationGrid[rightIndex] = left;
        return;
    }

    if (rightKind == 1 && left.IsActive == 0 && CanGranularMoveDiagonally(leftCoordinate + uint2(1, 0), leftCoordinate))
    {
        right.VelocityX = -24;
        DestinationGrid[leftIndex] = right;
        DestinationGrid[rightIndex] = CreateEmptyCell();
        return;
    }

    bool sameFluid = left.IsActive != 0 && IsFluidMaterial(leftKind) &&
        (right.IsActive == 0 || right.MaterialId == left.MaterialId);
    sameFluid = sameFluid || right.IsActive != 0 && IsFluidMaterial(rightKind) &&
        (left.IsActive == 0 || left.MaterialId == right.MaterialId);
    if (sameFluid)
    {
        GridCell templateCell = right;
        if (left.IsActive != 0)
        {
            templateCell = left;
        }
        float totalMass = left.Mass + right.Mass;
        float pressureDifference = left.Pressure - right.Pressure;
        float desiredLeftMass = clamp(totalMass * 0.5 - pressureDifference * 0.035, 0, totalMass);
        float flowRate = Materials[templateCell.MaterialId].FlowRate;
        float leftMass = lerp(left.Mass, desiredLeftMass, saturate(flowRate));
        float rightMass = totalMass - leftMass;
        float transferredMass = rightMass - right.Mass;
        GridCell resolvedLeft = CellWithMass(templateCell, leftMass);
        GridCell resolvedRight = CellWithMass(templateCell, rightMass);
        resolvedLeft.VelocityX = lerp(left.VelocityX, -transferredMass * 60, 0.55);
        resolvedRight.VelocityX = lerp(right.VelocityX, transferredMass * 60, 0.55);
        DestinationGrid[leftIndex] = resolvedLeft;
        DestinationGrid[rightIndex] = resolvedRight;
        return;
    }

    bool leftLiquidPushesSand = leftKind == 4 && rightKind == 1 &&
        abs(left.VelocityX) + left.Pressure > Materials[right.MaterialId].Friction * 8;
    bool rightLiquidPushesSand = rightKind == 4 && leftKind == 1 &&
        abs(right.VelocityX) + right.Pressure > Materials[left.MaterialId].Friction * 8;
    if (leftLiquidPushesSand || rightLiquidPushesSand)
    {
        float sharedImpulse = (left.VelocityX * left.Mass + right.VelocityX * right.Mass) /
            max(left.Mass + right.Mass, 0.001);
        left.VelocityX = sharedImpulse * 0.7;
        right.VelocityX = sharedImpulse * 0.7;
        DestinationGrid[leftIndex] = right;
        DestinationGrid[rightIndex] = left;
    }
}

void ResolvePressure(uint2 coordinate)
{
    uint index = FlattenCoordinate(coordinate);
    GridCell cell = SourceGrid[index];
    uint kind = CellKind(cell);
    if (!IsCellularMaterial(kind))
    {
        return;
    }

    float inheritedPressure = 0;
    if (kind == 5 && coordinate.y + 1 < Height)
    {
        GridCell below = SourceGrid[index + Width];
        inheritedPressure = below.MaterialId == cell.MaterialId ? below.Pressure : 0;
    }
    else if (coordinate.y > 0)
    {
        GridCell above = SourceGrid[index - Width];
        inheritedPressure = above.MaterialId == cell.MaterialId ? above.Pressure : 0;
    }

    float hydrostaticPressure = inheritedPressure + cell.Mass * CellDensity(cell);
    cell.Pressure = lerp(cell.Pressure, hydrostaticPressure, 0.72);
    cell.VelocityX *= 0.985;
    cell.VelocityY *= 0.985;
    DestinationGrid[index] = cell;
}

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= Width || dispatchThreadId.y >= Height)
    {
        return;
    }

    uint2 coordinate = dispatchThreadId.xy;
    if (SimulationPhase <= 1)
    {
        if ((coordinate.y & 1) == SimulationPhase && coordinate.y + 1 < Height)
        {
            ResolveVerticalPair(coordinate);
        }
    }
    else if (SimulationPhase <= 3)
    {
        if ((coordinate.x & 1) == SimulationPhase - 2 && coordinate.x + 1 < Width)
        {
            ResolveHorizontalPair(coordinate);
        }
    }
    else
    {
        ResolvePressure(coordinate);
    }
}
