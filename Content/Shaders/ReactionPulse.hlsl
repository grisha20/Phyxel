#include "PhysicsShared.hlsli"
cbuffer AirSimulationConstants : register(b0)
{
    uint AirWidth; uint AirHeight; uint AirGridWidth; uint AirGridHeight;
    float Ambient; float HotScale; uint Tick; uint Sandbox;
};
StructuredBuffer<MaterialProperties> Materials : register(t0);
StructuredBuffer<GridCell> Grid : register(t1);
StructuredBuffer<float4> Previous : register(t2);
StructuredBuffer<uint> Links : register(t3);
RWStructuredBuffer<float4> Pending : register(u0);
RWStructuredBuffer<float4> Next : register(u1);
RWStructuredBuffer<float4> Scratch : register(u2);
RWStructuredBuffer<AirCell> Air : register(u3);
RWStructuredBuffer<float2> Heat : register(u4);
RWStructuredBuffer<float2> ProjectionSource : register(u5);
#define FineAirWidth AirGridWidth
#define FineAirHeight AirGridHeight
#define FineAirMaterialAt(p) Grid[uint((p).y)*AirGridWidth+uint((p).x)].MaterialIndex
#define FineAirMaterials Materials
#define FineAirBlockGranular ((Sandbox&1u)==0)
#include "FineAirGeometry.hlsli"
// The pressure mapping uses the same fine walls/filter geometry, with pores.
#define FineAirWidth AirGridWidth
#define FineAirHeight AirGridHeight
#define FineAirMaterialAt(p) Grid[uint((p).y)*AirGridWidth+uint((p).x)].MaterialIndex
#define FineAirMaterials Materials
#define FineAirBlockGranular false
#define AirFineBlocked PressureFineBlocked
#define AirFineSegmentOpen PressureFineSegmentOpen
#define AirFineNodeFor PressureFineNodeFor
#include "FineAirGeometry.hlsli"
#undef AirFineBlocked
#undef AirFineSegmentOpen
#undef AirFineNodeFor

bool Inside(int2 p) { return all(p>=0) && p.x<int(AirWidth) && p.y<int(AirHeight); }
uint Index(int2 p) { return uint(p.y)*AirWidth+uint(p.x); }
bool Strong(int2 p,bool scratch)
{if(!Inside(p))return false;float4 a=scratch?Scratch[Index(p)]:Next[Index(p)];return any(abs(a)>.001);}
bool StrongNeighborhood(int2 p,bool scratch)
{return Strong(p,scratch)||Strong(p+int2(-1,0),scratch)||Strong(p+int2(1,0),scratch)||Strong(p+int2(0,-1),scratch)||Strong(p+int2(0,1),scratch);}
// Mechanical pressure crosses the pores of a powder even when the ordinary
// finite oxygen carrier cannot occupy its grains. Heat/source mapping above
// keeps its existing fine geometry. Walls, liquids and filters still seal it.
bool WaveBlocked(int2 p) {return PressureFineBlocked(p);}
bool Open(int2 p,int2 d,bool scratch)
{
    if(!Inside(p+d))return false;
    if(!Strong(p,scratch)&&!Strong(p+d,scratch))return
        (Links[Index(p)]&(1u<<uint((d.y+1)*3+d.x+1)))!=0 &&
        (Links[Index(p+d)]&(1u<<uint((-d.y+1)*3-d.x+1)))!=0;
    int2 a=p*4+2;
    [unroll]for(int k=0;k<=4;k++)if(WaveBlocked(a+d*k))return false;
    return true;
}
bool Edge(int2 p) { return p.x==0 || p.y==0 || p.x==int(AirWidth)-1 || p.y==int(AirHeight)-1; }

// Surface oxidation releases heat into an adjacent gas volume, even while
// the grain itself still occupies its cell. Select one receiver identically
// in gather and clear; no heat leaks through a wall or a closed filter.
bool ReactionHeatNodeFor(int2 q,out int2 node)
{
    if(AirFineNodeFor(q,node))return true;
    uint i=uint(q.y)*AirGridWidth+uint(q.x);
    if((Materials[Grid[i].MaterialIndex].Flags & MaterialFlagPersistentCoalIgnition)==0 || !FilterAirAllows(i))return false;
    int2 offsets[4]={int2(0,-1),int2(-1,0),int2(1,0),int2(0,1)};
    [unroll]for(int k=0;k<4;k++)
    {
        int2 neighbor=q+offsets[k];
        if(any(neighbor<0)||neighbor.x>=int(AirGridWidth)||neighbor.y>=int(AirGridHeight))continue;
        int2 candidate;
        if(AirFineNodeFor(neighbor,candidate) && all(abs(candidate*4+2-q)<=4))
        {node=candidate;return true;}
    }
    return false;
}

// Gather one unique receiver for each finite stock. Pressure can cross pores;
// heat stays pending until the original gas mapping opens. Solids block both.
[numthreads(8,8,1)]
void CSGather(uint3 id:SV_DispatchThreadID)
{
    int2 p=int2(id.xy);if(!Inside(p)) return; uint i=Index(p);
    float3 source=0;
    [loop] for(int dy=-2;dy<=6;dy++) [loop] for(int dx=-2;dx<=6;dx++)
    {
        int2 q=p*4+int2(dx,dy);
        if(any(q<0)||q.x>=int(AirGridWidth)||q.y>=int(AirGridHeight)) continue;
        float3 s=Pending[uint(q.y)*AirGridWidth+uint(q.x)].xyz;
        if(all(s==0)) continue;
        int2 node;
        if(PressureFineNodeFor(q,node) && all(node==p))source.x+=s.x;
        if(ReactionHeatNodeFor(q,node) && all(node==p))source.yz+=s.yz;
    }
    float phasePressure=ProjectionSource[i].x;
    ProjectionSource[i].x=0;
    float4 packet=Previous[i]+float4(source.x+phasePressure,0,0,source.x*6.0);
    // Admit the finite newly produced expansion over ~0.16 s in this wave.
    // The W stock is issued once and consumed, not a permanent HotAir source.
    float admitted=packet.w*.1;
    packet.w-=admitted;
    // Newly produced gas belongs to the compressible branch. Sending it into
    // the incompressible draft as well accumulates a second carrier pressure
    // reservoir which cannot follow the faster wave through an open outlet.
    if(admitted!=0)packet.x+=admitted*.3;
    else
    {
        // Preserve the ordinary carrier's established normalization exactly
        // when no reaction expansion is being issued (including old saves).
        ProjectionSource[i].y+=admitted;
        AirCell carrier=Air[i];carrier.Pressure=clamp(carrier.Pressure+admitted*.3,-256,256);Air[i]=carrier;
    }
    Next[i]=packet;
    // Absolute-K energy and capacity are added together: neither clamping the
    // temperature nor capping the pressure silently deletes reaction heat.
    Heat[i]+=source.yz;
}

[numthreads(16,16,1)]
void CSClearMapped(uint3 id:SV_DispatchThreadID)
{
    int2 q=int2(id.xy);if(q.x>=int(AirGridWidth)||q.y>=int(AirGridHeight)) return;
    uint i=uint(q.y)*AirGridWidth+uint(q.x);
    if(all(Pending[i]==0)) return;
    int2 node;float4 source=Pending[i];
    if(PressureFineNodeFor(q,node))source.x=0;
    if(ReactionHeatNodeFor(q,node))source.yz=0;
    Pending[i]=source;
}

// A separate damped, compressible pressure pulse. The normal low-Mach
// projection remains unchanged; it must not erase the expansion of a rapid
// reaction. Face velocities reflect off sealed fine links.
static const float WaveSpeed=.5; // coarse cells per wave substep; CFL < 1/sqrt(2).
void Faces(uint3 id,bool fast)
{
    int2 p=int2(id.xy);if(!Inside(p)) return;uint i=Index(p);
    float4 a=Next[i];float2 v=0;
    bool strong=StrongNeighborhood(p,false);
    if(fast&&!strong){Scratch[i]=a;return;}
    // Fourth root of the original damping: four substeps retain one tick loss.
    float damping=strong?.99241412:.97;
    if(Open(p,int2(1,0),false)) v.x=damping*a.y-WaveSpeed*(Next[i+1].x-a.x);
    if(Open(p,int2(0,1),false)) v.y=damping*a.z-WaveSpeed*(Next[i+AirWidth].x-a.x);
    Scratch[i]=float4(a.x,clamp(v,-64,64),a.w);
}
void Commit(uint3 id,bool fast)
{
    int2 p=int2(id.xy);if(!Inside(p)) return;uint i=Index(p);
    float4 s=Scratch[i];
    float l=Open(p,int2(-1,0),true)?Scratch[i-1].y:0;
    float u=Open(p,int2(0,-1),true)?Scratch[i-AirWidth].z:0;
    bool strong=StrongNeighborhood(p,true)||any(abs(float4(s.y,s.z,l,u))>.001);
    if(fast&&!strong)return;
    s.x=clamp((strong?.99874765:.995)*s.x-WaveSpeed*(s.y-l+s.z-u),-256,256);
    if(Edge(p) || (strong?WaveBlocked(p*4+2):Air[i].Blocked>.5)) s=0;
    Next[i]=s;
    if(fast)return; // Only the final substep injects momentum into the carrier.
    AirCell a=Air[i];
    if(a.Blocked<.5 && !Edge(p))
    {
        a.Pressure=clamp(a.Pressure+s.x,-256,256);
        a.VelocityX=clamp(a.VelocityX+(l+s.y)*.5,-64,64);
        a.VelocityY=clamp(a.VelocityY+(u+s.z)*.5,-64,64);
        Air[i]=a;
    }
}
[numthreads(8,8,1)]
void CSFaces(uint3 id:SV_DispatchThreadID){Faces(id,false);}
[numthreads(8,8,1)]
void CSCommit(uint3 id:SV_DispatchThreadID){Commit(id,false);}
[numthreads(8,8,1)]
void CSFastFaces(uint3 id:SV_DispatchThreadID){Faces(id,true);}
[numthreads(8,8,1)]
void CSFastCommit(uint3 id:SV_DispatchThreadID){Commit(id,true);}
