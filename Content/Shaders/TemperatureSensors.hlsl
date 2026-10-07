#include "PhysicsShared.hlsli"
cbuffer ProbeConstants : register(b0) { uint Count; uint AirEnabled; uint SensorWidth; uint SensorHeight; };
struct TemperatureProbeResult
{
    uint IsActive; uint MaterialIndex; float Temperature; uint Reserved;
    float FuelFraction; uint RetainedLiquidMaterialIndex;
};
StructuredBuffer<GridCell> Grid : register(t0);
StructuredBuffer<MaterialProperties> Materials : register(t1);
StructuredBuffer<uint2> Coordinates : register(t2);
StructuredBuffer<float2> Thermal : register(t3);
StructuredBuffer<AirCell> Carrier : register(t4);
RWStructuredBuffer<TemperatureProbeResult> Result : register(u0);
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
    TemperatureProbeResult output = (TemperatureProbeResult)0;
    uint2 p = Coordinates[id.x];
    if (p.x < SensorWidth && p.y < SensorHeight)
    {
        GridCell cell = Grid[p.y * SensorWidth + p.x];
        if (cell.IsActive != 0)
        {
            output.IsActive = 1; output.MaterialIndex = cell.MaterialIndex; output.Temperature = cell.Temperature;
        }
        else if (AirEnabled != 0)
        {
            int2 node; bool visible = AirFineNodeFor(int2(p), node);
            uint aw = (SensorWidth + AirCellSize - 1) / AirCellSize;
            uint ah = (SensorHeight + AirCellSize - 1) / AirCellSize;
            if (visible && node.x >= 0 && node.y >= 0 && node.x < int(aw) && node.y < int(ah))
            {
                uint index = node.y * aw + node.x;
                float2 heat = Thermal[index];
                if (Carrier[index].Blocked < .5 && heat.y > 0 && all(isfinite(heat)))
                {
                    output.Temperature = heat.x / heat.y - 273.15;
                    if (isfinite(output.Temperature)) output.IsActive = 2;
                }
            }
        }
    }
    Result[id.x] = output;
}
