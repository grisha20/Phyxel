#include "PhysicsShared.hlsli"
cbuffer ProbeConstants : register(b0) { uint Count; uint AirEnabled; uint SensorWidth; uint SensorHeight; };
struct TemperatureProbeResult
{
    uint IsActive; uint MaterialIndex; float Temperature; uint Reserved;
    float FuelFraction; uint RetainedLiquidMaterialIndex;
};
struct TemperatureSensorReading
{
    TemperatureProbeResult Thermal;
    float Pressure; float ReactionPressure;
};
StructuredBuffer<GridCell> Grid : register(t0);
StructuredBuffer<MaterialProperties> Materials : register(t1);
StructuredBuffer<uint2> Coordinates : register(t2);
StructuredBuffer<float2> Thermal : register(t3);
StructuredBuffer<AirCell> Carrier : register(t4);
StructuredBuffer<float4> Wave : register(t5);
RWStructuredBuffer<TemperatureSensorReading> Result : register(u0);
#define FineAirWidth SensorWidth
#define FineAirHeight SensorHeight
#define FineAirMaterialAt(p) Grid[(p).y * SensorWidth + (p).x].MaterialIndex
#define FineAirMaterials Materials
#define FineAirBlockGranular true
#define FineAirIgnoreFilters
#include "FineAirGeometry.hlsli"

[numthreads(32, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Count) return;
    TemperatureSensorReading output = (TemperatureSensorReading)0;
    output.Pressure = asfloat(0x7fc00000u); output.ReactionPressure = output.Pressure;
    uint2 p = Coordinates[id.x];
    if (p.x < SensorWidth && p.y < SensorHeight)
    {
        GridCell cell = Grid[p.y * SensorWidth + p.x];
        if (cell.IsActive != 0)
        {
            output.Thermal.IsActive = 1; output.Thermal.MaterialIndex = cell.MaterialIndex; output.Thermal.Temperature = cell.Temperature;
        }
        if (AirEnabled != 0 && (cell.IsActive == 0 || Materials[cell.MaterialIndex].SimulationKind == SimulationKindGas))
        {
            int2 node; bool visible = AirFineNodeFor(int2(p), node);
            uint aw = (SensorWidth + AirCellSize - 1) / AirCellSize;
            uint ah = (SensorHeight + AirCellSize - 1) / AirCellSize;
            if (visible && node.x >= 0 && node.y >= 0 && node.x < int(aw) && node.y < int(ah))
            {
                uint index = node.y * aw + node.x;
                float2 heat = Thermal[index];
                if (Carrier[index].Blocked < .5 && isfinite(Carrier[index].Pressure) && isfinite(Wave[index].x))
                {
                    output.Pressure = Carrier[index].Pressure;
                    output.ReactionPressure = Wave[index].x;
                }
                if (cell.IsActive == 0 && Carrier[index].Blocked < .5 && heat.y > 0 && all(isfinite(heat)))
                {
                    output.Thermal.Temperature = heat.x / heat.y - 273.15;
                    if (isfinite(output.Thermal.Temperature)) output.Thermal.IsActive = 2;
                }
            }
        }
    }
    Result[id.x] = output;
}
