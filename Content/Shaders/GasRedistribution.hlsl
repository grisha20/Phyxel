#include "PhysicsShared.hlsli"

StructuredBuffer<MaterialProperties> Materials : register(t0);
RWStructuredBuffer<GridCell> Grid : register(u0);
RWStructuredBuffer<uint> CellMaterials : register(u1);

static const float GasMinimumRepresentableMass = 0.01;
static const float GasTransferEpsilon = 0.0005;

bool IsOrdinaryGas(GridCell cell)
{
    if (cell.IsActive == 0)
    {
        return false;
    }
    MaterialProperties material = Materials[cell.MaterialIndex];
    return material.SimulationKind == SimulationKindGas &&
        (material.Flags & MaterialFlagFlame) == 0;
}

bool IsEmpty(GridCell cell)
{
    return cell.IsActive == 0;
}

GridCell BuildGasPortion(GridCell source, float mass)
{
    if (mass <= 0)
    {
        return CreateEmptyCell();
    }
    source.Mass = mass;
    source.IsActive = 1;
    source.BodyId = 0;
    source.Pressure = 0;
    source.RestFrames = 0;
    source.VelocityX = 0;
    source.VelocityY = 0;
    return source;
}

void StorePair(uint firstIndex, GridCell first, uint secondIndex, GridCell second)
{
    Grid[firstIndex] = first;
    Grid[secondIndex] = second;
    CellMaterials[firstIndex] = first.IsActive != 0 ? first.MaterialIndex : 0;
    CellMaterials[secondIndex] = second.IsActive != 0 ? second.MaterialIndex : 0;
}

void RedistributeSameGasOrEmpty(
    uint firstIndex,
    GridCell first,
    uint secondIndex,
    GridCell second,
    bool vertical,
    float firstShare)
{
    bool firstGas = IsOrdinaryGas(first);
    bool secondGas = IsOrdinaryGas(second);
    float firstMass = firstGas ? first.Mass : 0;
    float secondMass = secondGas ? second.Mass : 0;
    float totalMass = firstMass + secondMass;
    if (totalMass <= 0)
    {
        return;
    }

    // A sub-threshold remnant is still real material. Keep it in its current
    // cell until it can merge instead of silently deleting it.
    if (totalMass < GasMinimumRepresentableMass)
    {
        return;
    }

    float targetFirst = min(1, totalMass * firstShare);
    float targetSecond = totalMass - targetFirst;
    if (targetFirst > 0 && targetSecond > 0 &&
        targetFirst < GasMinimumRepresentableMass &&
        targetSecond < GasMinimumRepresentableMass)
    {
        // A packet this small cannot be split without creating fractional
        // liquid fog after condensation. Move the intact packet according to
        // the same target share instead; the fixed-tick hash avoids any
        // permanent down/right bias while preserving all mass and energy.
        bool keepFirst = HashUnitFloat(
            firstIndex ^ secondIndex ^ (FrameIndex * 0x27d4eb2du)) <
            targetFirst / totalMass;
        targetFirst = keepFirst ? totalMass : 0;
        targetSecond = keepFirst ? 0 : totalMass;
    }
    else if (targetFirst < GasMinimumRepresentableMass)
    {
        targetSecond += targetFirst;
        targetFirst = 0;
    }
    else if (targetSecond < GasMinimumRepresentableMass)
    {
        targetFirst += targetSecond;
        targetSecond = 0;
    }
    if (abs(targetFirst - firstMass) <= GasTransferEpsilon)
    {
        return;
    }

    GridCell source = first;
    if (!firstGas)
    {
        source = second;
    }
    float heatCapacity = max(Materials[source.MaterialIndex].HeatCapacity, 0.01);
    float totalCapacity = heatCapacity * totalMass;
    float thermalEnergy =
        (firstGas ? heatCapacity * firstMass * first.Temperature : 0) +
        (secondGas ? heatCapacity * secondMass * second.Temperature : 0);
    float mixedTemperature = totalCapacity > 0
        ? thermalEnergy / totalCapacity
        : source.Temperature;
    float mixedLifetime = totalMass > 0
        ? ((firstGas ? firstMass * first.Lifetime : 0) +
            (secondGas ? secondMass * second.Lifetime : 0)) / totalMass
        : source.Lifetime;

    GridCell resolvedFirst = BuildGasPortion(source, targetFirst);
    GridCell resolvedSecond = BuildGasPortion(source, targetSecond);
    if (resolvedFirst.IsActive != 0)
    {
        resolvedFirst.Temperature = mixedTemperature;
        resolvedFirst.Lifetime = mixedLifetime;
        resolvedFirst.VelocityX = vertical ? 0 : -12;
        resolvedFirst.VelocityY = vertical ? -12 : 0;
    }
    if (resolvedSecond.IsActive != 0)
    {
        resolvedSecond.Temperature = mixedTemperature;
        resolvedSecond.Lifetime = mixedLifetime;
        resolvedSecond.VelocityX = vertical ? 0 : 12;
        resolvedSecond.VelocityY = vertical ? 12 : 0;
    }
    StorePair(firstIndex, resolvedFirst, secondIndex, resolvedSecond);
}

bool HorizontalPathAllowsGas(uint firstIndex, uint secondIndex)
{
    [loop]
    for (uint index = firstIndex + 1; index < secondIndex; index++)
    {
        GridCell cell = Grid[index];
        if (!IsEmpty(cell) && !IsOrdinaryGas(cell))
        {
            return false;
        }
    }
    return true;
}

void ResolveGasPair(uint firstIndex, uint secondIndex, bool vertical, uint seed)
{
    GridCell first = Grid[firstIndex];
    GridCell second = Grid[secondIndex];
    bool firstGas = IsOrdinaryGas(first);
    bool secondGas = IsOrdinaryGas(second);
    if ((!firstGas && !IsEmpty(first)) || (!secondGas && !IsEmpty(second)) ||
        (!firstGas && !secondGas))
    {
        return;
    }

    if (firstGas && secondGas && first.MaterialIndex != second.MaterialIndex)
    {
        if (vertical)
        {
            float firstDensity = Materials[first.MaterialIndex].Density;
            float secondDensity = Materials[second.MaterialIndex].Density;
            if (firstDensity > secondDensity + 0.000001)
            {
                first.RestFrames = 0;
                second.RestFrames = 0;
                StorePair(firstIndex, second, secondIndex, first);
            }
        }
        else if (HashUnitFloat(seed) < 0.20)
        {
            first.RestFrames = 0;
            second.RestFrames = 0;
            StorePair(firstIndex, second, secondIndex, first);
        }
        return;
    }

    RedistributeSameGasOrEmpty(
        firstIndex,
        first,
        secondIndex,
        second,
        vertical,
        vertical ? 0.56 : 0.5);
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
        ResolveGasPair(
            firstIndex,
            firstIndex + Width,
            true,
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
        ResolveGasPair(
            firstIndex,
            firstIndex + 1,
            false,
            firstIndex ^ (FrameIndex * 0x85ebca6bu));
        return;
    }

    uint stride = 1u << ((FrameIndex >> 1) & 3u); // 1, 2, 4, 8
    stride *= stride; // 1, 4, 16, 64
    // Alternate the long-range block origin every stride cycle. Without the
    // shifted layout, a thin concentration can remain trapped behind the
    // permanent 2*stride block boundaries even though the space is connected.
    uint origin = ((FrameIndex >> 3) & 1u) * stride;
    uint pairsPerRow = (Width + 1) / 2;
    if (dispatchThreadId.x >= pairsPerRow || dispatchThreadId.y >= Height)
    {
        return;
    }
    uint block = dispatchThreadId.x / stride;
    uint withinBlock = dispatchThreadId.x - block * stride;
    uint firstX = origin + block * stride * 2 + withinBlock;
    uint secondX = firstX + stride;
    if (secondX >= Width)
    {
        return;
    }
    uint firstIndex = FlattenCoordinate(uint2(firstX, dispatchThreadId.y));
    uint secondIndex = FlattenCoordinate(uint2(secondX, dispatchThreadId.y));
    GridCell first = Grid[firstIndex];
    GridCell second = Grid[secondIndex];
    bool firstGas = IsOrdinaryGas(first);
    bool secondGas = IsOrdinaryGas(second);
    if ((!firstGas && !secondGas) ||
        (!firstGas && !IsEmpty(first)) ||
        (!secondGas && !IsEmpty(second)))
    {
        return;
    }
    if (!HorizontalPathAllowsGas(firstIndex, secondIndex))
    {
        return;
    }
    ResolveGasPair(
        firstIndex,
        secondIndex,
        false,
        firstIndex ^ secondIndex ^ (FrameIndex * 0xc2b2ae35u));
}
