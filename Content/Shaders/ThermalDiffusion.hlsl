#include "PhysicsShared.hlsli"

cbuffer ThermalConstants : register(b0)
{
    float ThermalDeltaTime;
    float ThermalExchangeRate;
    uint ThermalWidth;
    uint ThermalHeight;
};

StructuredBuffer<GridCell> SourceGrid : register(t0);
StructuredBuffer<MaterialProperties> Materials : register(t1);
RWStructuredBuffer<GridCell> DestinationGrid : register(u0);

static const float MinimumThermalMass = 0.0001;
static const float MaximumExchangeFraction = 0.80;
static const float SameGasConductivityFloor = 0.16;
static const float DiagonalGasContactWeight = 0.5;
static const float InteriorAmbientExposure = 0.04;

float EffectiveCapacity(GridCell cell)
{
    return Materials[cell.MaterialIndex].HeatCapacity * max(cell.Mass, MinimumThermalMass);
}

bool IsSameGas(GridCell cell, GridCell neighbor)
{
    MaterialProperties material = Materials[cell.MaterialIndex];
    return cell.MaterialIndex == neighbor.MaterialIndex &&
        material.SimulationKind == SimulationKindGas &&
        (material.Flags & MaterialFlagFlame) == 0;
}

float ContactHeatFlow(
    GridCell cell,
    float capacity,
    uint neighborIndex,
    float contactWeight,
    bool sameGasOnly)
{
    GridCell neighbor = SourceGrid[neighborIndex];
    if (neighbor.IsActive == 0)
    {
        return 0;
    }

    bool sameGas = IsSameGas(cell, neighbor);
    if (sameGasOnly && !sameGas)
    {
        return 0;
    }

    float conductivityA = Materials[cell.MaterialIndex].ThermalConductivity;
    float conductivityB = Materials[neighbor.MaterialIndex].ThermalConductivity;
    float conductivitySum = conductivityA + conductivityB;
    if (conductivityA <= 0 || conductivityB <= 0 || conductivitySum <= 0)
    {
        return 0;
    }

    float contactConductivity =
        2 * conductivityA * conductivityB / conductivitySum;
    if (sameGas)
    {
        // Gas packets retain their identity and mass, but their temperature
        // must not retain injection-batch boundaries. This floor models local
        // molecular mixing without introducing mass redistribution here.
        contactConductivity = max(contactConductivity, SameGasConductivityFloor);
    }
    float neighborCapacity = EffectiveCapacity(neighbor);
    float exchangeFraction = min(
        MaximumExchangeFraction,
        ThermalExchangeRate * ThermalDeltaTime);
    float edgeCoefficient =
        min(capacity, neighborCapacity) * contactConductivity * exchangeFraction *
        contactWeight / 4;
    return edgeCoefficient * (neighbor.Temperature - cell.Temperature);
}

bool IsEmptyAt(int2 coordinate)
{
    if (coordinate.x < 0 || coordinate.y < 0 ||
        coordinate.x >= (int)ThermalWidth || coordinate.y >= (int)ThermalHeight)
    {
        return true;
    }
    return SourceGrid[coordinate.y * ThermalWidth + coordinate.x].IsActive == 0;
}

float AmbientSurfaceExposure(uint2 coordinate)
{
    uint immediateEmptyNeighbors = 0;
    uint localEmptyNeighbors = 0;
    [unroll]
    for (int y = -2; y <= 2; y++)
    {
        [unroll]
        for (int x = -2; x <= 2; x++)
        {
            if ((x != 0 || y != 0) && IsEmptyAt(int2(coordinate) + int2(x, y)))
            {
                localEmptyNeighbors++;
                if (abs(x) <= 1 && abs(y) <= 1)
                {
                    immediateEmptyNeighbors++;
                }
            }
        }
    }
    float surfaceOpenFraction = immediateEmptyNeighbors / 8.0;
    float localOpenFraction = localEmptyNeighbors / 24.0;
    // A partially sheltered packet must cool substantially more slowly than a
    // fully exposed one. The immediate ring measures the actual open surface;
    // the outer ring distinguishes a sparse grid cloud's core from its edge.
    float exposure = pow(surfaceOpenFraction, 4.0) * pow(localOpenFraction, 12.0);
    return lerp(InteriorAmbientExposure, 1.0, exposure);
}

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= ThermalWidth || coordinate.y >= ThermalHeight)
    {
        return;
    }

    uint index = coordinate.y * ThermalWidth + coordinate.x;
    GridCell cell = SourceGrid[index];
    if (cell.IsActive == 0)
    {
        DestinationGrid[index] = (GridCell)0;
        return;
    }

    float capacity = EffectiveCapacity(cell);
    float heatFlow = 0;
    if (coordinate.x > 0)
    {
        heatFlow += ContactHeatFlow(cell, capacity, index - 1, 1.0, false);
    }
    if (coordinate.x + 1 < ThermalWidth)
    {
        heatFlow += ContactHeatFlow(cell, capacity, index + 1, 1.0, false);
    }
    if (coordinate.y > 0)
    {
        heatFlow += ContactHeatFlow(cell, capacity, index - ThermalWidth, 1.0, false);
    }
    if (coordinate.y + 1 < ThermalHeight)
    {
        heatFlow += ContactHeatFlow(cell, capacity, index + ThermalWidth, 1.0, false);
    }


    // Diagonal contact is limited to identical gases. It removes visible
    // thermal stripes between sequential packets while leaving solid/liquid
    // thermal behavior unchanged.
    if (coordinate.x > 0 && coordinate.y > 0)
        heatFlow += ContactHeatFlow(cell, capacity, index - ThermalWidth - 1,
            DiagonalGasContactWeight, true);
    if (coordinate.x + 1 < ThermalWidth && coordinate.y > 0)
        heatFlow += ContactHeatFlow(cell, capacity, index - ThermalWidth + 1,
            DiagonalGasContactWeight, true);
    if (coordinate.x > 0 && coordinate.y + 1 < ThermalHeight)
        heatFlow += ContactHeatFlow(cell, capacity, index + ThermalWidth - 1,
            DiagonalGasContactWeight, true);
    if (coordinate.x + 1 < ThermalWidth && coordinate.y + 1 < ThermalHeight)
        heatFlow += ContactHeatFlow(cell, capacity, index + ThermalWidth + 1,
            DiagonalGasContactWeight, true);

    cell.Temperature += heatFlow / capacity;
    MaterialProperties material = Materials[cell.MaterialIndex];
    if (material.AmbientCoolingRate > 0)
    {
        float ambientRate = material.AmbientCoolingRate * AmbientSurfaceExposure(coordinate);
        float ambientFactor = 1.0 - exp(-ambientRate * ThermalDeltaTime);
        cell.Temperature +=
            (material.AmbientTemperature - cell.Temperature) * saturate(ambientFactor);
    }
    DestinationGrid[index] = cell;
}
