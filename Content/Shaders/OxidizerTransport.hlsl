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
StructuredBuffer<float2> CarrierPotential : register(t7);
StructuredBuffer<float4> CarrierFaces : register(t8); // velocity faces, immutable neighbour mask
RWStructuredBuffer<float> DestinationOxygen : register(u0);
// Canonical right/down fluxes shared by both adjacent cells.
RWStructuredBuffer<float2> Faces : register(u1);
RWStructuredBuffer<float> DestinationAvailable : register(u2);
RWStructuredBuffer<float4> DestinationCarrierFaces : register(u3);
RWStructuredBuffer<float2> DestinationPotential : register(u4);
#define FineAirWidth OxygenWidth
#define FineAirHeight OxygenHeight
#define FineAirMaterialAt(p) Cells[uint((p).y) * OxygenWidth + uint((p).x)].MaterialIndex
#define FineAirMaterials Materials
#define FineAirBlockGranular true
#include "FineAirGeometry.hlsli"
float Capacity(uint i) { if(!FilterAirAllows(i))return 0;return OxidizerCapacity(Cells[i], Materials[Cells[i].MaterialIndex]); }
float Space(uint i) { if(!FilterAirAllows(i))return 0;return OxidizerSpace(Cells[i], Materials[Cells[i].MaterialIndex]); }
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
bool CarrierBoundary(int2 p)
{
    return OpenEdges!=0&&(p.x==0||p.y==0||p.x+1==int(OxygenWidth));
}
float VolumeFace(int2 a,int2 b)
{
    if(!Inside(b)||Space(Index(a))==0||Space(Index(b))==0)return 0;
    return dot(.5*(Velocity(a)+Velocity(b)),float2(b-a));
}
[numthreads(16,16,1)]
void CSCarrierFaces(uint3 tid:SV_DispatchThreadID)
{
    int2 p=int2(tid.xy);if(!Inside(p))return;
    uint mask=Space(Index(p))>0?16u:0u;
    int2 ds[4]={int2(1,0),int2(-1,0),int2(0,1),int2(0,-1)};
    [unroll]for(int k=0;k<4;k++)if(Inside(p+ds[k])&&Space(Index(p+ds[k]))>0)mask|=1u<<k;
    if(CarrierBoundary(p))mask|=32u;
    // Store a numeric integer, not denormal float bits: GPU arithmetic may
    // flush asfloat(1..63) to zero and silently close every projection face.
    DestinationCarrierFaces[Index(p)]=float4(VolumeFace(p,p+int2(1,0)),VolumeFace(p,p+int2(0,1)),float(mask),0);
}
[numthreads(16,16,1)]
void CSCarrierDivergence(uint3 tid:SV_DispatchThreadID)
{
    int2 p=int2(tid.xy);if(!Inside(p))return;uint i=Index(p);
    float2 f=CarrierFaces[i].xy;
    float div=f.x+f.y-(p.x>0?CarrierFaces[i-1].x:0)-(p.y>0?CarrierFaces[i-OxygenWidth].y:0);
    // Continue the pressure guess between ticks, as the coarse carrier does.
    // Loading/resetting a world starts a new solve; this is solver scratch.
    bool open=Space(i)>0&&!CarrierBoundary(p);
    DestinationPotential[i]=float2(open&&OxygenReserved0!=0?CarrierPotential[i].x:0,open?div:0);
}
[numthreads(16,16,1)]
void CSCarrierJacobi(uint3 tid:SV_DispatchThreadID)
{
    int2 p=int2(tid.xy);if(!Inside(p))return;uint i=Index(p);
    // Geometry is immutable during this dispatch sequence. Cache its mask
    // once instead of reading grid/material/filter tables for every iteration.
    uint mask=uint(CarrierFaces[i].z);
    if((mask&16u)==0||(mask&32u)!=0){DestinationPotential[i]=0;return;}
    float sum=0,count=0;int2 ds[4]={int2(1,0),int2(-1,0),int2(0,1),int2(0,-1)};
    [unroll]for(int k=0;k<4;k++)
    {
        if((mask&(1u<<k))!=0){sum+=CarrierPotential[Index(p+ds[k])].x;count+=1;}
    }
    float2 own=CarrierPotential[i];
    DestinationPotential[i]=float2(count>0?(sum-own.y)/count:0,own.y);
}
// Four exact steps over a 16x16 output tile and a four-cell dependency halo.
groupshared float OxygenPsi0[576];
groupshared float OxygenPsi1[576];
groupshared float OxygenDiv[576];
groupshared uint OxygenMasks[576];
[numthreads(16,16,1)]
void CSCarrierJacobiFour(uint3 group:SV_GroupID,uint lane:SV_GroupIndex)
{
    int2 origin=int2(group.xy)*16-4;
    [unroll]for(uint j=lane;j<576;j+=256)
    {
        int2 p=origin+int2(j%24,j/24);
        float2 value=0;uint mask=0;
        if(Inside(p)){uint i=Index(p);value=CarrierPotential[i];mask=uint(CarrierFaces[i].z);}
        OxygenPsi0[j]=value.x;OxygenDiv[j]=value.y;OxygenMasks[j]=mask;
    }
    GroupMemoryBarrierWithGroupSync();
    [unroll]for(uint step=1;step<=4;step++)
    {
        [unroll]for(uint j=lane;j<576;j+=256)
        {
            uint x=j%24,y=j/24;
            if(x<step||y<step||x>=24-step||y>=24-step)continue;
            uint mask=OxygenMasks[j];float sum=0,count=0,value=0;
            if((mask&16u)!=0&&(mask&32u)==0)
            {
                if(mask&1u){sum+=step%2?OxygenPsi0[j+1]:OxygenPsi1[j+1];count+=1;}
                if(mask&2u){sum+=step%2?OxygenPsi0[j-1]:OxygenPsi1[j-1];count+=1;}
                if(mask&4u){sum+=step%2?OxygenPsi0[j+24]:OxygenPsi1[j+24];count+=1;}
                if(mask&8u){sum+=step%2?OxygenPsi0[j-24]:OxygenPsi1[j-24];count+=1;}
                value=count>0?(sum-OxygenDiv[j])/count:0;
            }
            if(step%2)OxygenPsi1[j]=value;else OxygenPsi0[j]=value;
        }
        GroupMemoryBarrierWithGroupSync();
    }
    uint2 p=group.xy*16+uint2(lane%16,lane/16);if(!Inside(int2(p)))return;
    uint outputIndex=(lane/16+4)*24+lane%16+4,outputMask=OxygenMasks[outputIndex];
    DestinationPotential[Index(int2(p))]=(outputMask&16u)==0||(outputMask&32u)!=0?0:float2(OxygenPsi0[outputIndex],OxygenDiv[outputIndex]);
}

float RawFace(int2 a, int2 b)
{
    if (!Inside(b)) return 0;
    uint ia=Index(a),ib=Index(b);
    if(!FilterAirAllows(ia)||!FilterAirAllows(ib))return 0;
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
    // A coarse node mapped to both sides of a stair-step fuel face is not a
    // divergence-free fine carrier. Project the same canonical faces used by
    // stock transport; uniform fresh air must not develop a vacuum at the wall.
    float2 face=CarrierFaces[ia].xy;
    float velocity=(b.x!=a.x?face.x:face.y)-(CarrierPotential[ib].x-CarrierPotential[ia].x);
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
    if(!FilterAirAllows(p.y*OxygenWidth+p.x))return 0;
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
