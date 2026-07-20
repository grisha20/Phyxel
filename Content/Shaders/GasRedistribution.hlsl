#include "PhysicsShared.hlsli"

StructuredBuffer<MaterialProperties> Materials : register(t0);
RWStructuredBuffer<GridCell> Grid : register(u0);
RWStructuredBuffer<uint> CellMaterials : register(u1);

static const float MinimumGasMass = 0.0005;

bool IsContinuumGas(GridCell cell)
{
    if (cell.IsActive == 0)
    {
        return false;
    }
    MaterialProperties material = Materials[cell.MaterialIndex];
    return material.SimulationKind == SimulationKindGas &&
        (material.Flags & MaterialFlagFlame) == 0;
}

void StorePair(uint firstIndex, GridCell first, uint secondIndex, GridCell second)
{
    Grid[firstIndex] = first;
    Grid[secondIndex] = second;
    CellMaterials[firstIndex] = first.IsActive != 0 ? first.MaterialIndex : 0;
    CellMaterials[secondIndex] = second.IsActive != 0 ? second.MaterialIndex : 0;
}

GridCell CreateGasCell(
    GridCell templateCell,
    float mass,
    float temperature,
    float lifetime,
    float velocityX,
    float velocityY)
{
    if (mass < MinimumGasMass)
    {
        return CreateEmptyCell();
    }
    GridCell result = templateCell;
    result.Mass = mass;
    result.Temperature = temperature;
    result.Lifetime = lifetime;
    result.VelocityX = velocityX;
    result.VelocityY = velocityY;
    result.Pressure = 0;
    result.IsActive = 1;
    result.BodyId = 0;
    result.RestFrames = 0;
    return result;
}

void ResolveDifferentGases(
    uint firstIndex,
    GridCell first,
    uint secondIndex,
    GridCell second,
    bool verticalOrDiagonal,
    uint seed)
{
    if (!verticalOrDiagonal)
    {
        return;
    }
    MaterialProperties firstMaterial = Materials[first.MaterialIndex];
    MaterialProperties secondMaterial = Materials[second.MaterialIndex];
    if (firstMaterial.Density <= secondMaterial.Density + 0.000001)
    {
        return;
    }
    float separation = min(firstMaterial.GasDiffusion, secondMaterial.GasDiffusion) * 0.35;
    if (HashUnitFloat(seed) >= separation)
    {
        return;
    }
    first.RestFrames = 0;
    second.RestFrames = 0;
    StorePair(firstIndex, second, secondIndex, first);
}

void RedistributeSameGas(
    uint firstIndex,
    GridCell first,
    uint secondIndex,
    GridCell second,
    bool vertical,
    bool diagonal)
{
    bool firstGas = IsContinuumGas(first);
    GridCell templateCell = first;
    if (!firstGas)
    {
        templateCell = second;
    }
    MaterialProperties material = Materials[templateCell.MaterialIndex];
    float firstMass = firstGas ? first.Mass : 0;
    float secondMass = IsContinuumGas(second) ? second.Mass : 0;
    float totalMass = firstMass + secondMass;
    if (totalMass < MinimumGasMass)
    {
        return;
    }

    float firstFraction = 0.5;
    if (vertical || diagonal)
    {
        firstFraction = saturate(0.5 + material.GasBuoyancy * (diagonal ? 0.6 : 1.0));
    }
    float targetFirstMass = totalMass * firstFraction;
    float passWeight = diagonal ? 0.42 : vertical ? 0.55 : 0.70;
    float relaxation = saturate(material.GasDiffusion * passWeight);
    float newFirstMass = firstMass + (targetFirstMass - firstMass) * relaxation;
    float newSecondMass = totalMass - newFirstMass;

    // Never discard a sub-cell tail: consolidate it into the paired cell.
    if (newFirstMass < MinimumGasMass)
    {
        newFirstMass = 0;
        newSecondMass = totalMass;
    }
    else if (newSecondMass < MinimumGasMass)
    {
        newFirstMass = totalMass;
        newSecondMass = 0;
    }

    float heatCapacity = material.HeatCapacity;
    float firstEnergy = firstMass > 0 ? firstMass * heatCapacity * first.Temperature : 0;
    float secondEnergy = secondMass > 0 ? secondMass * heatCapacity * second.Temperature : 0;
    float firstLifetimeAmount = firstMass > 0 ? firstMass * first.Lifetime : 0;
    float secondLifetimeAmount = secondMass > 0 ? secondMass * second.Lifetime : 0;
    float transfer = newSecondMass - secondMass;
    if (transfer > 0)
    {
        firstEnergy -= transfer * heatCapacity * first.Temperature;
        secondEnergy += transfer * heatCapacity * first.Temperature;
        firstLifetimeAmount -= transfer * first.Lifetime;
        secondLifetimeAmount += transfer * first.Lifetime;
    }
    else if (transfer < 0)
    {
        float movedMass = -transfer;
        firstEnergy += movedMass * heatCapacity * second.Temperature;
        secondEnergy -= movedMass * heatCapacity * second.Temperature;
        firstLifetimeAmount += movedMass * second.Lifetime;
        secondLifetimeAmount -= movedMass * second.Lifetime;
    }

    float directionX = vertical ? 0 : transfer;
    float directionY = vertical || diagonal ? transfer : 0;
    GridCell newFirst = CreateGasCell(
        templateCell,
        newFirstMass,
        newFirstMass > 0 ? firstEnergy / (newFirstMass * heatCapacity) : 0,
        newFirstMass > 0 ? max(0, firstLifetimeAmount / newFirstMass) : 0,
        -directionX * 8,
        -directionY * 8);
    GridCell newSecond = CreateGasCell(
        templateCell,
        newSecondMass,
        newSecondMass > 0 ? secondEnergy / (newSecondMass * heatCapacity) : 0,
        newSecondMass > 0 ? max(0, secondLifetimeAmount / newSecondMass) : 0,
        directionX * 8,
        directionY * 8);
    StorePair(firstIndex, newFirst, secondIndex, newSecond);
}

void ResolveContinuumPair(
    uint firstIndex,
    uint secondIndex,
    bool vertical,
    bool diagonal,
    uint seed)
{
    GridCell first = Grid[firstIndex];
    GridCell second = Grid[secondIndex];
    bool firstGas = IsContinuumGas(first);
    bool secondGas = IsContinuumGas(second);
    bool firstEmpty = first.IsActive == 0;
    bool secondEmpty = second.IsActive == 0;
    if ((!firstGas && !firstEmpty) || (!secondGas && !secondEmpty) ||
        (!firstGas && !secondGas))
    {
        return;
    }
    if (firstGas && secondGas && first.MaterialIndex != second.MaterialIndex)
    {
        ResolveDifferentGases(
            firstIndex, first, secondIndex, second, vertical || diagonal, seed);
        return;
    }
    RedistributeSameGas(firstIndex, first, secondIndex, second, vertical, diagonal);
}

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (SimulationPhase <= 1)
    {
        uint2 upper = uint2(dispatchThreadId.x, dispatchThreadId.y * 2 + SimulationPhase);
        if (upper.x >= Width || upper.y + 1 >= Height)
        {
            return;
        }
        uint firstIndex = FlattenCoordinate(upper);
        ResolveContinuumPair(
            firstIndex,
            firstIndex + Width,
            true,
            false,
            firstIndex ^ (FrameIndex * 0x9e3779b9u));
        return;
    }

    if (SimulationPhase <= 3)
    {
        uint2 left = uint2(dispatchThreadId.x * 2 + SimulationPhase - 2, dispatchThreadId.y);
        if (left.x + 1 >= Width || left.y >= Height)
        {
            return;
        }
        uint firstIndex = FlattenCoordinate(left);
        ResolveContinuumPair(
            firstIndex,
            firstIndex + 1,
            false,
            false,
            firstIndex ^ (FrameIndex * 0x85ebca6bu));
        return;
    }

    uint2 block = dispatchThreadId.xy * 2;
    if (block.x + 1 >= Width || block.y + 1 >= Height)
    {
        return;
    }
    bool otherDiagonal = HashUnitFloat(
        FlattenCoordinate(block) ^ (FrameIndex * 0xc2b2ae35u)) < 0.5;
    uint2 upper = block + uint2(otherDiagonal ? 1u : 0u, 0u);
    uint2 lower = block + uint2(otherDiagonal ? 0u : 1u, 1u);
    uint firstIndex = FlattenCoordinate(upper);
    uint secondIndex = FlattenCoordinate(lower);
    ResolveContinuumPair(
        firstIndex,
        secondIndex,
        false,
        true,
        firstIndex ^ secondIndex ^ (FrameIndex * 0x27d4eb2du));
}
