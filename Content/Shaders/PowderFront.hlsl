#include "PhysicsShared.hlsli"
StructuredBuffer<GridCell> SourceGrid : register(t0);
StructuredBuffer<MaterialProperties> Materials : register(t1);
StructuredBuffer<uint> ReactionSummary : register(t2);
RWStructuredBuffer<GridCell> DestinationGrid : register(u0);

static const uint RadialIgnition = 1u << 16;

bool SameDryFuel(int2 p, uint material)
{
    if(p.x<0||p.y<0||p.x>=int(Width)||p.y>=int(Height))return false;
    GridCell c=SourceGrid[p.y*Width+p.x];
    return c.IsActive!=0 && c.MaterialIndex==material && c.MoistureMass==0 && c.FuelMass==0;
}

float RadialArrival(uint2 p, GridCell cell, MaterialProperties m)
{
    // A snapshot of the old front, never a read/write chain within a dispatch.
    // Sixteen rays bound the angular error without a full disk neighbourhood.
    // Diagonal costs use distance; a diagonal does not advance as fast as a face.
    int2 rays[16]={int2(1,0),int2(2,1),int2(1,1),int2(1,2),
        int2(0,1),int2(-1,2),int2(-1,1),int2(-2,1),
        int2(-1,0),int2(-2,-1),int2(-1,-1),int2(-1,-2),
        int2(0,-1),int2(1,-2),int2(1,-1),int2(2,-1)};
    float best=0;
    int radius=min(12,int(ceil(m.FlameSpreadRate*DeltaTime))+1);
    [loop] for(int ray=0;ray<16;ray++)
    {
        float2 direction=normalize(float2(rays[ray]));
        int2 previous=int2(p);
        [loop] for(int step=1;step<=radius;step++)
        {
            int2 q=int2(p)+int2(round(direction*step));
            if(!SameDryFuel(q,cell.MaterialIndex))break;
            if(q.x!=previous.x && q.y!=previous.y &&
                (!SameDryFuel(int2(previous.x,q.y),cell.MaterialIndex)||
                 !SameDryFuel(int2(q.x,previous.y),cell.MaterialIndex)))break;
            GridCell donor=SourceGrid[q.y*Width+q.x];
            if(donor.Lifetime>0 && donor.Temperature>m.ContactIgnitionTemperature)
                best=max(best,donor.Lifetime-length(float2(q-int2(p)))/m.FlameSpreadRate);
            previous=q;
        }
    }
    return best;
}

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
    MaterialProperties source=Materials[c.MaterialIndex];
    if((ReactionSummary[0]&(1u<<9))!=0 && c.IsActive!=0 && (source.Flags&RadialIgnition)!=0 && c.Lifetime==0 &&
        c.MoistureMass==0 && c.FuelMass==0)
        c.Lifetime=RadialArrival(p.xy,c,source);
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
