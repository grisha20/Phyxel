#include "PhysicsShared.hlsli"
cbuffer AirThermalConstants : register(b0)
{
    uint HeatWidth; uint HeatHeight; uint HeatGridWidth; uint HeatGridHeight;
    float HeatAmbient; float HeatCapacity; float HeatExchange; uint HeatOpenBoundaries;
};
StructuredBuffer<AirCell> Carrier : register(t0);
StructuredBuffer<uint> Links : register(t1);
StructuredBuffer<MaterialProperties> Materials : register(t2);
#include "PhaseEnthalpy.hlsli"
RWStructuredBuffer<float2> Thermal : register(u0); // energy, transported heat capacity
RWStructuredBuffer<GridCell> Grid : register(u1);
RWStructuredBuffer<float4> Flux : register(u2); // right capacity/energy, down capacity/energy
#define FineAirWidth HeatGridWidth
#define FineAirHeight HeatGridHeight
#define FineAirMaterialAt(p) Grid[uint((p).y) * HeatGridWidth + uint((p).x)].MaterialIndex
#define FineAirMaterials Materials
#define FineAirBlockGranular true
#include "FineAirGeometry.hlsli"
bool Inside(int2 p) { return all(p >= 0) && p.x < int(HeatWidth) && p.y < int(HeatHeight); }
uint Index(int2 p) { return uint(p.y) * HeatWidth + uint(p.x); }
bool Connected(int2 p, int2 d)
{
    int2 q = p + d;
    if (!Inside(q)) return false;
    return (Links[Index(p)] & (1u << uint((d.y+1)*3+d.x+1))) != 0 &&
        (Links[Index(q)] & (1u << uint((1-d.y)*3+1-d.x))) != 0;
}
float T(int2 p) { float2 s=Thermal[Index(p)]; return s.y>1e-8?s.x/s.y:HeatAmbient+273.15; }
// Surface ownership adds a one-pixel halo to the gas stencil. Every fine
// cell has one owner per step, including faces on either side of thin walls.
groupshared uint ExchangeOwner[39*39];
groupshared float ExchangeCapacity[39*39];
groupshared float ExchangeResponse[39*39];
groupshared uint ExchangeHasCells;
bool ExchangeNode(int2 q, MaterialProperties m, out int2 mapped)
{
    mapped=0;
    if(m.SimulationKind==SimulationKindGas) return AirFineNodeFor(q,mapped) && Inside(mapped);
    if(m.ThermalConductivity<=0) return false;
    int2 offsets[4]={int2(1,0),int2(0,1),int2(-1,0),int2(0,-1)};
    uint phase=(HeatOpenBoundaries>>1)&3;
    [loop] for(uint face=0;face<4;face++)
    {
        int2 surface=q+offsets[(face+phase)&3];
        if(AirFineNodeFor(surface,mapped) && Inside(mapped) && Carrier[Index(mapped)].Blocked<=.5) return true;
    }
    return false;
}
[numthreads(8,8,1)] void CSExchange(uint3 id : SV_DispatchThreadID,
    uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID, uint lane : SV_GroupIndex)
{
    if(lane==0) ExchangeHasCells=0;
    GroupMemoryBarrierWithGroupSync();
    int2 origin=int2(group.xy)*32-3;
    [loop] for(uint k=lane;k<39*39;k+=64)
    {
        int2 q=origin+int2(k%39,k/39), mapped;
        uint owner=0xffffffffu; float capacity=0, response=0;
        if(all(q>=0) && q.x<int(HeatGridWidth) && q.y<int(HeatGridHeight))
        {
            GridCell c=Grid[uint(q.y)*HeatGridWidth+uint(q.x)];
            MaterialProperties m=Materials[c.MaterialIndex];
            if(c.IsActive!=0 && c.Mass>0 && ExchangeNode(q,m,mapped))
            {
                owner=Index(mapped); capacity=CellEffectiveCapacity(c);
                response=m.SimulationKind==SimulationKindGas ? 1 : saturate(m.ThermalConductivity);
                InterlockedOr(ExchangeHasCells,1);
            }
        }
        ExchangeOwner[k]=owner; ExchangeCapacity[k]=capacity; ExchangeResponse[k]=response;
    }
    GroupMemoryBarrierWithGroupSync();
    if(ExchangeHasCells==0) return;
    int2 p=int2(id.xy); if(!Inside(p) || Carrier[Index(p)].Blocked>.5) return;
    uint owner=Index(p); float totalCapacity=0;
    [loop] for(uint y=0;y<11;y++) [loop] for(uint x=0;x<11;x++)
    {
        uint k=(local.y*4+y)*39+local.x*4+x;
        if(ExchangeOwner[k]==owner) totalCapacity+=ExchangeCapacity[k];
    }
    if(totalCapacity==0) return;
    float2 state=Thermal[owner]; if(state.y<1e-8) return;
    float airT=T(p), heat=0;
    [loop] for(int y=-3;y<=7;y++) [loop] for(int x=-3;x<=7;x++)
    {
        uint k=(local.y*4+uint(y+3))*39+local.x*4+uint(x+3);
        if(ExchangeOwner[k]!=owner) continue;
        int2 q=p*4+int2(x,y);
        GridCell c=Grid[uint(q.y)*HeatGridWidth+uint(q.x)];
        float conductance=HeatExchange*ExchangeResponse[k]*ExchangeCapacity[k]*state.y/
            max(state.y+totalCapacity,.0001);
        float transfer=conductance*(airT-(c.Temperature+273.15));
        c=SetCellSpecificEnthalpy(c,CellSpecificEnthalpy(c)+transfer/c.Mass);
        Grid[uint(q.y)*HeatGridWidth+uint(q.x)]=c;
        heat-=transfer;
    }
    state.x+=heat; Thermal[owner]=state;
}
float2 Candidate(int2 p,int2 d)
{
    if (!Connected(p,d)) return 0;
    AirCell a=Carrier[Index(p)], b=Carrier[Index(p+d)];
    float v=d.x!=0 ? (a.VelocityX+b.VelocityX)*.5*d.x : (a.VelocityY+b.VelocityY)*.5*d.y;
    float advection=clamp(v/4,-1,1);
    float2 sa=Thermal[Index(p)], sb=Thermal[Index(p+d)];
    float capacityFlux=advection*(advection>=0?sa.y:sb.y);
    float energyFlux=capacityFlux*(advection>=0?T(p):T(p+d))+.025*min(sa.y,sb.y)*(T(p)-T(p+d));
    return float2(capacityFlux,energyFlux);
}
// Share raw faces and donor limits within the tile. Two halo layers cover
// both endpoints and all four competitors of each face. No global scratch,
// no change to the conservative limiter or the number of physical steps.
groupshared float4 RawFaces[12*12];
groupshared float2 DonorLimits[10*10];
float2 LimitFace(float2 f,float2 a,float2 b)
{
    return f*min(f.x>=0?a.x:b.x,f.y>=0?a.y:b.y);
}
[numthreads(8,8,1)] void CSFlux(uint3 id : SV_DispatchThreadID,
    uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID, uint lane : SV_GroupIndex)
{
    int2 origin=int2(group.xy)*8-2;
    [loop] for(uint k=lane;k<144;k+=64)
    {
        int2 q=origin+int2(k%12,k/12);
        RawFaces[k]=Inside(q)?float4(Candidate(q,int2(1,0)),Candidate(q,int2(0,1))):0;
    }
    GroupMemoryBarrierWithGroupSync();
    [loop] for(uint k=lane;k<100;k+=64)
    {
        int2 q=origin+1+int2(k%10,k/10);
        uint raw=(k/10+1)*12+k%10+1;
        float2 outgoing=max(0,RawFaces[raw].xy)+max(0,-RawFaces[raw-1].xy)+
            max(0,RawFaces[raw].zw)+max(0,-RawFaces[raw-12].zw);
        float2 state=Inside(q)?Thermal[Index(q)]:0;
        // Leave a one-part-per-million reserve when a donor is exhausted.
        // Four rounded face sums can otherwise subtract a few ulps more than
        // the stock (also present in the uncached solver). Limit the shared
        // flux, rather than clamp the transported state and create energy.
        DonorLimits[k]=min(1,.999999*state.yx/max(outgoing,1e-20));
    }
    GroupMemoryBarrierWithGroupSync();
    int2 p=int2(id.xy); if (!Inside(p)) return;
    uint raw=(local.y+2)*12+local.x+2, lim=(local.y+1)*10+local.x+1;
    Flux[Index(p)]=float4(LimitFace(RawFaces[raw].xy,DonorLimits[lim],DonorLimits[lim+1]),
        LimitFace(RawFaces[raw].zw,DonorLimits[lim],DonorLimits[lim+10]));
}
[numthreads(8,8,1)] void CSTransport(uint3 id : SV_DispatchThreadID)
{
    int2 p=int2(id.xy); if (!Inside(p)) return;
    uint i=Index(p); float4 own=Flux[i];
    float2 incoming=(p.x>0?Flux[i-1].xy:0)+(p.y>0?Flux[i-HeatWidth].zw:0);
    float2 transfer=incoming-own.xy-own.zw;
    float2 next=Thermal[i]+transfer.yx;
    if ((HeatOpenBoundaries&1)!=0 && (p.x<1 || p.y<1 || p.x+1>=int(HeatWidth)))
        next=float2((HeatAmbient+273.15)*HeatCapacity,HeatCapacity);
    Thermal[i]=next;
}
