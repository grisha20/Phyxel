#include "PhysicsShared.hlsli"

cbuffer ProbeConstants : register(b0)
{
    uint ProbeX;
    uint ProbeY;
    uint ProbeWidth;
    uint ProbeHeight;
};

struct TemperatureProbeResult
{
    uint IsActive;
    uint MaterialIndex;
    float Temperature;
    uint Reserved;
    float FuelFraction;
};

StructuredBuffer<GridCell> Grid : register(t0);
StructuredBuffer<MaterialProperties> Materials : register(t1);
RWStructuredBuffer<TemperatureProbeResult> Result : register(u0);

[numthreads(1, 1, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    TemperatureProbeResult output = (TemperatureProbeResult)0;
    if (ProbeX < ProbeWidth && ProbeY < ProbeHeight)
    {
        GridCell cell = Grid[ProbeY * ProbeWidth + ProbeX];
        if (cell.IsActive != 0)
        {
            output.IsActive = 1;
            output.MaterialIndex = cell.MaterialIndex;
            output.Temperature = cell.Temperature;
            output.FuelFraction=cell.FuelMass/max(cell.Mass+cell.MoistureMass+cell.FuelMass,.0001);
            if (Materials[cell.MaterialIndex].MoistureCapacity > 0)
                output.Reserved = asuint(cell.MoistureMass / max(cell.Mass + cell.MoistureMass, .0001));
            if ((Materials[cell.MaterialIndex].Flags & (MaterialFlagThermalHeater | MaterialFlagThermalCooler)) != 0)
                output.Reserved = ((uint)((int)round(cell.Pressure * 10) + 2732) & 0xffff) |
                    ((uint)round(cell.Lifetime * 10) << 16);
        }
    }
    Result[0] = output;
}
