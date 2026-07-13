#include "PhysicsShared.hlsli"

StructuredBuffer<MaterialProperties> Materials : register(t0);
RWStructuredBuffer<GridCell> SourceGrid : register(u0);

#define DestinationGrid SourceGrid

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
    if (fluidKind == 4 && lower.IsActive != 0)
    {
        float pressureLift = saturate((lower.Pressure - upper.Pressure - 1) * 0.08);
        lowerMass -= min(lowerMass, min(totalMass * 0.45, pressureLift * 0.45));
    }

    float upperMass = totalMass - lowerMass;
    GridCell resolvedUpper = CellWithMass(templateCell, upperMass);
    GridCell resolvedLower = CellWithMass(templateCell, lowerMass);
    resolvedUpper.Pressure = upper.IsActive != 0 ? upper.Pressure : templateCell.Pressure;
    resolvedLower.Pressure = lower.IsActive != 0 ? lower.Pressure : templateCell.Pressure;
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
        resolvedLeft.Pressure = left.IsActive != 0 ? left.Pressure : templateCell.Pressure;
        resolvedRight.Pressure = right.IsActive != 0 ? right.Pressure : templateCell.Pressure;
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

void ResolveHorizontalPressureSpan(uint2 leftCoordinate, uint stride)
{
    uint rightX = leftCoordinate.x + stride;
    if (rightX >= Width)
    {
        return;
    }

    uint leftIndex = FlattenCoordinate(leftCoordinate);
    uint rightIndex = FlattenCoordinate(uint2(rightX, leftCoordinate.y));
    GridCell left = SourceGrid[leftIndex];
    GridCell right = SourceGrid[rightIndex];
    uint leftKind = CellKind(left);
    uint rightKind = CellKind(right);
    uint materialId = left.IsActive != 0 ? left.MaterialId : right.MaterialId;
    uint materialKind = left.IsActive != 0 ? leftKind : rightKind;
    if (materialKind != 4 ||
        (left.IsActive != 0 && left.MaterialId != materialId) ||
        (right.IsActive != 0 && right.MaterialId != materialId))
    {
        return;
    }

    for (uint offset = 1; offset < stride; offset++)
    {
        GridCell intermediate = SourceGrid[leftIndex + offset];
        if (intermediate.IsActive != 0 && intermediate.MaterialId != materialId)
        {
            return;
        }
    }

    GridCell templateCell = right;
    if (left.IsActive != 0)
    {
        templateCell = left;
    }

    float totalMass = left.Mass + right.Mass;
    float pressureDifference = left.Pressure - right.Pressure;
    float desiredLeftMass = clamp(totalMass * 0.5 - pressureDifference * 0.045, 0, totalMass);
    float leftMass = lerp(left.Mass, desiredLeftMass, 0.88);
    float rightMass = totalMass - leftMass;
    GridCell resolvedLeft = CellWithMass(templateCell, leftMass);
    GridCell resolvedRight = CellWithMass(templateCell, rightMass);
    resolvedLeft.Pressure = left.IsActive != 0 ? left.Pressure : templateCell.Pressure;
    resolvedRight.Pressure = right.IsActive != 0 ? right.Pressure : templateCell.Pressure;
    DestinationGrid[leftIndex] = resolvedLeft;
    DestinationGrid[rightIndex] = resolvedRight;
}

void ResolveVerticalPressureSpan(uint2 upperCoordinate, uint stride)
{
    uint lowerY = upperCoordinate.y + stride;
    if (lowerY >= Height)
    {
        return;
    }

    uint upperIndex = FlattenCoordinate(upperCoordinate);
    uint lowerIndex = FlattenCoordinate(uint2(upperCoordinate.x, lowerY));
    GridCell upper = SourceGrid[upperIndex];
    GridCell lower = SourceGrid[lowerIndex];
    if (lower.IsActive == 0 || CellKind(lower) != 4 ||
        (upper.IsActive != 0 && upper.MaterialId != lower.MaterialId))
    {
        return;
    }

    for (uint offset = 1; offset < stride; offset++)
    {
        GridCell intermediate = SourceGrid[upperIndex + offset * Width];
        if (intermediate.IsActive != 0 && intermediate.MaterialId != lower.MaterialId)
        {
            return;
        }
    }

    float pressureDifference = lower.Pressure - upper.Pressure - stride * 0.75;
    float availableCapacity = max(0, 1 - upper.Mass);
    float transfer = min(lower.Mass, min(availableCapacity, max(0, pressureDifference) * 0.08));
    if (transfer <= 0)
    {
        return;
    }

    GridCell templateCell = lower;
    GridCell resolvedUpper = CellWithMass(templateCell, upper.Mass + transfer);
    GridCell resolvedLower = CellWithMass(templateCell, lower.Mass - transfer);
    resolvedUpper.Pressure = upper.IsActive != 0 ? upper.Pressure : lower.Pressure;
    resolvedLower.Pressure = lower.Pressure;
    DestinationGrid[upperIndex] = resolvedUpper;
    DestinationGrid[lowerIndex] = resolvedLower;
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
    int direction = kind == 5 ? 1 : -1;
    for (uint distance = 1; distance <= 32; distance++)
    {
        int sampleY = int(coordinate.y) + direction * int(distance);
        if (sampleY < 0 || sampleY >= int(Height))
        {
            break;
        }

        GridCell sampleCell = SourceGrid[FlattenCoordinate(uint2(coordinate.x, sampleY))];
        if (sampleCell.IsActive == 0 || sampleCell.MaterialId != cell.MaterialId)
        {
            break;
        }

        inheritedPressure += distance == 32
            ? sampleCell.Pressure
            : sampleCell.Mass * CellDensity(sampleCell);
    }

    float hydrostaticPressure = inheritedPressure + cell.Mass * CellDensity(cell);
    cell.Pressure = lerp(cell.Pressure, hydrostaticPressure, 0.9);
    cell.VelocityX *= 0.985;
    cell.VelocityY *= 0.985;
    DestinationGrid[index] = cell;
}

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy + uint2(DispatchOffsetX, DispatchOffsetY);
    if (coordinate.x >= Width || coordinate.y >= Height)
    {
        return;
    }

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
    else if (SimulationPhase == 4)
    {
        ResolvePressure(coordinate);
    }
    else if (SimulationPhase <= 9)
    {
        uint stride = 1u << (SimulationPhase - 4);
        uint blockPosition = coordinate.x % (stride * 2);
        if (blockPosition < stride)
        {
            ResolveHorizontalPressureSpan(coordinate, stride);
        }
    }
    else
    {
        uint stride = 1u << (SimulationPhase - 9);
        uint blockPosition = coordinate.y % (stride * 2);
        if (blockPosition < stride)
        {
            ResolveVerticalPressureSpan(coordinate, stride);
        }
    }
}
