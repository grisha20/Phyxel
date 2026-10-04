#include "PhysicsShared.hlsli"
#include "OxidizerShared.hlsli"
cbuffer OxidizerConstants : register(b0)
{
    uint OxygenWidth; uint OxygenHeight; uint OpenEdges; float OxygenDeltaTime;
    uint OxygenUseAir; uint OxygenReserved0; uint OxygenReserved1; uint OxygenReserved2;
};
StructuredBuffer<GridCell> Cells : register(t0);
StructuredBuffer<MaterialProperties> Materials : register(t1);
StructuredBuffer<float> SourceOxygen : register(t2);
StructuredBuffer<float> Demand : register(t3);
StructuredBuffer<AirCell> CarrierAir : register(t4);
StructuredBuffer<GasMotionState> ParticleMotion : register(t5);
StructuredBuffer<float> AvailableOxygen : register(t6);
RWStructuredBuffer<float> DestinationOxygen : register(u0);
// Canonical right/down fluxes shared by both adjacent cells.
RWStructuredBuffer<float2> Faces : register(u1);
RWStructuredBuffer<float> DestinationAvailable : register(u2);
#define FineAirWidth OxygenWidth
#define FineAirHeight OxygenHeight
#define FineAirMaterialAt(p) Cells[uint((p).y) * OxygenWidth + uint((p).x)].MaterialIndex
#define FineAirMaterials Materials
#define FineAirBlockGranular true
#include "FineAirGeometry.hlsli"
float Capacity(uint i) { return OxidizerCapacity(Cells[i], Materials[Cells[i].MaterialIndex]); }
float Space(uint i) { return OxidizerSpace(Cells[i], Materials[Cells[i].MaterialIndex]); }
float Amount(uint i) { return max(0, SourceOxygen[i]); }
bool Inside(int2 p) { return all(p >= 0) && p.x < int(OxygenWidth) && p.y < int(OxygenHeight); }
uint Index(int2 p) { return uint(p.y) * OxygenWidth + uint(p.x); }
float2 Velocity(int2 p)
{
    if (OxygenUseAir != 0)
    {
        int2 node;
        if (!AirFineNodeFor(p,node)) return 0;
        uint airWidth = (OxygenWidth + AirCellSize - 1) / AirCellSize;
        AirCell c = CarrierAir[uint(node.y)*airWidth+uint(node.x)];
        return float2(c.VelocityX,c.VelocityY);
    }
    uint i = Index(p);
    if (Cells[i].IsActive == 0 || Materials[Cells[i].MaterialIndex].SimulationKind != SimulationKindGas) return 0;
    GasMotionState c = ParticleMotion[i];
    return float2(c.VelocityX,c.VelocityY);
}
float RawFace(int2 a, int2 b)
{
    if (!Inside(b)) return 0;
    uint ia=Index(a),ib=Index(b);
    float ma=Amount(ia),mb=Amount(ib),sa=Space(ia),sb=Space(ib);
    float diffusion=min(.24,12*OxygenDeltaTime);
    // Occupied solids/liquids may evacuate their trapped stock but never
    // receive new stock: a static wall cannot relay air through itself.
    if (sa==0 && sb==0) return 0;
    if (sa==0) return diffusion*ma;
    if (sb==0) return -diffusion*mb;
    float ca=Capacity(ia),cb=Capacity(ib);
    float concentrationA=ca>0 ? saturate(ma/ca) : 0;
    float concentrationB=cb>0 ? saturate(mb/cb) : 0;
    float mixed=min(ca,cb)*(concentrationA-concentrationB);
    // Displaced excess remains real inventory and seeks adjacent free space.
    float displaced=max(0,ma-ca)-max(0,mb-cb);
    float velocity=dot(.5*(Velocity(a)+Velocity(b)),float2(b-a));
    float advected=velocity*(velocity>=0 ? ma : mb)*(60*OxygenDeltaTime);
    return diffusion*(mixed+displaced)+advected;
}
[numthreads(16,16,1)]
void CSFlux(uint3 tid : SV_DispatchThreadID)
{
    int2 p=int2(tid.xy);
    if (!Inside(p)) return;
    Faces[Index(p)]=float2(RawFace(p,p+int2(1,0)),RawFace(p,p+int2(0,1)));
}
float Outgoing(int2 p)
{
    uint i=Index(p); float2 f=Faces[i];
    return max(0,f.x)+max(0,f.y)+(p.x>0 ? max(0,-Faces[i-1].x) : 0)+
        (p.y>0 ? max(0,-Faces[i-OxygenWidth].y) : 0);
}
float Limited(float flux,int2 a,int2 b)
{
    if (flux==0 || !Inside(b)) return 0;
    int2 donor=flux>0 ? a : b;
    // Both ends apply the identical donor limiter. Four competing faces
    // cannot overdraw a cell; there are no atomics or upper inventory clamps.
    // Keep the multidimensional explicit step below its donor CFL limit.
    // Exhausting a donor in one step makes alternating empty/compressed
    // cells under convergent carrier flow, falsely extinguishing hot FIRE.
    // Both endpoints use this same face flux, so the reserve creates no air.
    return flux*min(1,.5*Amount(Index(donor))/max(Outgoing(donor),1e-20));
}
[numthreads(16,16,1)]
void CSTransport(uint3 tid : SV_DispatchThreadID)
{
    int2 p=int2(tid.xy); if (!Inside(p)) return;
    uint i=Index(p); float2 f=Faces[i];
    float amount=Amount(i)-Limited(f.x,p,p+int2(1,0))-Limited(f.y,p,p+int2(0,1));
    if (p.x>0) amount+=Limited(Faces[i-1].x,p-int2(1,0),p);
    if (p.y>0) amount+=Limited(Faces[i-OxygenWidth].y,p-int2(0,1),p);
    // Only exposed side/top boundary cells exchange with the reservoir.
    if (OpenEdges!=0 && Space(i)>0 && (p.x==0 || p.y==0 || p.x+1==int(OxygenWidth))) amount=Capacity(i);
    amount=max(0,amount); // only roundoff, never cap by available volume
    DestinationOxygen[i]=amount;
    DestinationAvailable[i]=min(amount,Capacity(i));
}
float NeighborSum(uint2 p)
{
    uint i=p.y*OxygenWidth+p.x; float sum=0;
    if (p.x>0) sum+=AvailableOxygen[i-1];
    if (p.x+1<OxygenWidth) sum+=AvailableOxygen[i+1];
    if (p.y>0) sum+=AvailableOxygen[i-OxygenWidth];
    if (p.y+1<OxygenHeight) sum+=AvailableOxygen[i+OxygenWidth];
    return sum;
}
float ConsumerShare(uint2 p,float own)
{
    float total=NeighborSum(p);
    return total>0 ? Demand[p.y*OxygenWidth+p.x]*own/total : 0;
}
[numthreads(16,16,1)]
void CSConsume(uint3 tid : SV_DispatchThreadID)
{
    uint2 p=tid.xy; if (p.x>=OxygenWidth || p.y>=OxygenHeight) return;
    uint i=p.y*OxygenWidth+p.x; float own=AvailableOxygen[i],used=0;
    if (own>0)
    {
        if (p.x>0) used+=ConsumerShare(uint2(p.x-1,p.y),own);
        if (p.x+1<OxygenWidth) used+=ConsumerShare(p+uint2(1,0),own);
        if (p.y>0) used+=ConsumerShare(uint2(p.x,p.y-1),own);
        if (p.y+1<OxygenHeight) used+=ConsumerShare(p+uint2(0,1),own);
    }
    GridCell cell=Cells[i];
    float flameUse=cell.IsActive!=0 && cell.BodyId!=SelfOxidizingFlameMarker && cell.BodyId!=ReactedFuelFlameMarker &&
        (Materials[cell.MaterialIndex].Flags&MaterialFlagFlame)!=0 ? min(own/5,.15*OxygenDeltaTime) : 0;
    DestinationOxygen[i]=max(0,Amount(i)-used-flameUse);
}
