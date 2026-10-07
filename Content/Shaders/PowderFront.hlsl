#include "PhysicsShared.hlsli"
StructuredBuffer<GridCell> SourceGrid : register(t0);
StructuredBuffer<MaterialProperties> Materials : register(t1);
RWStructuredBuffer<GridCell> DestinationGrid : register(u0);

bool DryReactiveGrain(GridCell c)
{
    MaterialProperties m = Materials[c.MaterialIndex];
    return c.IsActive != 0 && c.MoistureMass == 0 && c.FuelMass == 0 && m.SimulationKind == SimulationKindGranular &&
        (m.Flags & MaterialFlagSelfOxidizing) != 0 && m.ReactionPressurePerMass > 0 && m.BurnRate > 0;
}

[numthreads(16,16,1)]
void CSMain(uint3 p : SV_DispatchThreadID)
{
    if(p.x >= Width || p.y >= Height) return;
    uint index = p.y * Width + p.x;
    GridCell c = SourceGrid[index];
    if(DryReactiveGrain(c))
    {
        MaterialProperties m = Materials[c.MaterialIndex];
        float capacity = max(1e-6,c.Mass*m.HeatCapacity);
        float energy = 0;
        // Symmetric edge weights: each neighbour debits exactly what this cell receives.
        [unroll] for(int dy=-1;dy<=1;dy++)
        [unroll] for(int dx=-1;dx<=1;dx++)
        {
            int2 q=int2(p.xy)+int2(dx,dy);
            if((dx==0 && dy==0)||q.x<0||q.y<0||q.x>=int(Width)||q.y>=int(Height))continue;
            GridCell n=SourceGrid[q.y*Width+q.x];
            if(n.MaterialIndex!=c.MaterialIndex||!DryReactiveGrain(n))continue;
            if(dx!=0&&dy!=0 &&
                (SourceGrid[p.y*Width+q.x].MaterialIndex!=c.MaterialIndex ||
                 SourceGrid[q.y*Width+p.x].MaterialIndex!=c.MaterialIndex))continue;
            if(max(c.Temperature,n.Temperature)<=m.IgnitionTemperature)continue;
            float weight=saturate(m.FlameSpreadRate*DeltaTime)/8;
            float neighborCapacity=max(1e-6,n.Mass*m.HeatCapacity);
            float exchange=min(capacity,neighborCapacity)*weight*(n.Temperature-c.Temperature);
            float hotCapacity=n.Temperature>c.Temperature?neighborCapacity:capacity;
            float budget=max(0,max(c.Temperature,n.Temperature)-m.IgnitionTemperature-1)*hotCapacity/8;
            energy+=sign(exchange)*min(abs(exchange),budget);
        }
        c.Temperature+=energy/capacity;
    }
    DestinationGrid[index]=c;
}
