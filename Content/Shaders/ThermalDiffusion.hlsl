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

float EffectiveCapacity(GridCell cell)
{
    return Materials[cell.MaterialIndex].HeatCapacity * max(cell.Mass, MinimumThermalMass);
}

float ContactHeatFlow(GridCell cell, float capacity, uint neighborIndex)
{
    GridCell neighbor = SourceGrid[neighborIndex];
    if (neighbor.IsActive == 0)
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
    float neighborCapacity = EffectiveCapacity(neighbor);
    float exchangeFraction = min(
        MaximumExchangeFraction,
        ThermalExchangeRate * ThermalDeltaTime);
    float edgeCoefficient =
        min(capacity, neighborCapacity) * contactConductivity * exchangeFraction / 4;
    return edgeCoefficient * (neighbor.Temperature - cell.Temperature);
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
        heatFlow += ContactHeatFlow(cell, capacity, index - 1);
    }
    if (coordinate.x + 1 < ThermalWidth)
    {
        heatFlow += ContactHeatFlow(cell, capacity, index + 1);
    }
    if (coordinate.y > 0)
    {
        heatFlow += ContactHeatFlow(cell, capacity, index - ThermalWidth);
    }
    if (coordinate.y + 1 < ThermalHeight)
    {
        heatFlow += ContactHeatFlow(cell, capacity, index + ThermalWidth);
    }

    cell.Temperature += heatFlow / capacity;
    MaterialProperties material = Materials[cell.MaterialIndex];
    if (material.AmbientCoolingRate > 0)
    {
        float ambientFactor = 1.0 - exp(-material.AmbientCoolingRate * ThermalDeltaTime);
        cell.Temperature +=
            (material.AmbientTemperature - cell.Temperature) * saturate(ambientFactor);
    }
    DestinationGrid[index] = cell;
}
