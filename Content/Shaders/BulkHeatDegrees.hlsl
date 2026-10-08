#include "PhysicsShared.hlsli"
cbuffer ThermalConstants : register(b0)
{
    float ThermalDeltaTime; float ThermalExchangeRate;
    uint ThermalWidth; uint ThermalHeight;
    uint ObserveThermalEnergy; uint Reserved0; uint Reserved1; uint Reserved2;
};
StructuredBuffer<GridCell> SourceGrid : register(t0);
StructuredBuffer<MaterialProperties> Materials : register(t1);
RWStructuredBuffer<uint> Degrees : register(u0);
#include "BulkThermalGeometry.hlsli"

[numthreads(16,16,1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ThermalWidth || id.y >= ThermalHeight) return;
    uint index = id.y * ThermalWidth + id.x;
    GridCell cell = SourceGrid[index];
    uint degree = 0;
    if (cell.IsActive != 0 && Materials[cell.MaterialIndex].SimulationKind == SimulationKindSolid &&
        Materials[cell.MaterialIndex].ThermalConductivity > .5 && IsBulkParticipant(int2(id.xy), cell.MaterialIndex))
    {
        [unroll] for (int direction = 0; direction < 4; direction++)
        {
            int2 step = direction == 0 ? int2(-1, 0) : direction == 1 ? int2(1, 0) :
                direction == 2 ? int2(0, -1) : int2(0, 1);
            [loop] for (int distance = 2; distance <= 16; distance += 2)
            {
                int2 p = int2(id.xy) + step * distance;
                if (p.x < 1 || p.y < 1 || p.x+1 >= (int)ThermalWidth || p.y+1 >= (int)ThermalHeight) break;
                if (IsBulkParticipant(p, cell.MaterialIndex) && HasBulkPath(int2(id.xy), p, cell.MaterialIndex)) degree++;
            }
        }
    }
    if(degree!=0 && !IsBulkInterior(int2(id.xy),cell.MaterialIndex))degree|=BulkSurfaceDegreeFlag;
    if(cell.IsActive!=0 && Materials[cell.MaterialIndex].SimulationKind==SimulationKindSolid &&
        Materials[cell.MaterialIndex].ThermalConductivity>.5 && HasWetWallPath(int2(id.xy),cell.MaterialIndex))
        degree|=BulkWetPathFlag;
    Degrees[index] = degree;
}
