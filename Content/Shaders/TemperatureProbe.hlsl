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
};

StructuredBuffer<GridCell> Grid : register(t0);
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
        }
    }
    Result[0] = output;
}
