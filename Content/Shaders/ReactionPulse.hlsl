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
#define FineAirBlockGranular (Sandbox==0)
#include "FineAirGeometry.hlsli"

bool Inside(int2 p) { return all(p>=0) && p.x<int(AirWidth) && p.y<int(AirHeight); }
uint Index(int2 p) { return uint(p.y)*AirWidth+uint(p.x); }
bool Open(int2 p,int2 d)
{
    return Inside(p+d) && (Links[Index(p)]&(1u<<uint((d.y+1)*3+d.x+1)))!=0 &&
        (Links[Index(p+d)]&(1u<<uint((-d.y+1)*3-d.x+1)))!=0;
}
bool Edge(int2 p) { return p.x==0 || p.y==0 || p.x==int(AirWidth)-1 || p.y==int(AirHeight)-1; }

// Gather one unique mapped receiver. Solid/granular-blocked sources stay
// pending, including energy, until the grain burns out or that location opens.
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
        int2 node;if(AirFineNodeFor(q,node) && all(node==p)) source+=s;
    }
    float4 packet=Previous[i]+float4(source.x,0,0,source.x*6.0);
    // Admit the finite newly produced gas volume over ~0.16 s instead of
    // deleting its divergence in the ordinary draft projection. The W stock
    // is issued once and consumed, not a permanent HotAir source.
    float admitted=packet.w*.1;
    packet.w-=admitted;
    ProjectionSource[i].y+=admitted;
    AirCell carrier=Air[i];carrier.Pressure=clamp(carrier.Pressure+admitted*.3,-256,256);Air[i]=carrier;
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
    int2 node;if(AirFineNodeFor(q,node)) Pending[i]=0;
}

// A separate damped, compressible pressure pulse. The normal low-Mach
// projection remains unchanged; it must not erase the expansion of a rapid
// reaction. Face velocities reflect off sealed fine links.
static const float WaveSpeed=.5; // coarse cells per 60-Hz tick; CFL < 1/sqrt(2).
[numthreads(8,8,1)]
void CSFaces(uint3 id:SV_DispatchThreadID)
{
    int2 p=int2(id.xy);if(!Inside(p)) return;uint i=Index(p);
    float4 a=Next[i];float2 v=0;
    if(Open(p,int2(1,0))) v.x=.97*a.y-WaveSpeed*(Next[i+1].x-a.x);
    if(Open(p,int2(0,1))) v.y=.97*a.z-WaveSpeed*(Next[i+AirWidth].x-a.x);
    Scratch[i]=float4(a.x,clamp(v,-64,64),a.w);
}
[numthreads(8,8,1)]
void CSCommit(uint3 id:SV_DispatchThreadID)
{
    int2 p=int2(id.xy);if(!Inside(p)) return;uint i=Index(p);
    float4 s=Scratch[i];
    float l=Open(p,int2(-1,0))?Scratch[i-1].y:0;
    float u=Open(p,int2(0,-1))?Scratch[i-AirWidth].z:0;
    s.x=clamp(.995*s.x-WaveSpeed*(s.y-l+s.z-u),-256,256);
    if(Edge(p) || Air[i].Blocked>.5) s=0;
    Next[i]=s;
    AirCell a=Air[i];
    if(a.Blocked<.5 && !Edge(p))
    {
        a.Pressure=clamp(a.Pressure+s.x,-256,256);
        a.VelocityX=clamp(a.VelocityX+(l+s.y)*.5,-64,64);
        a.VelocityY=clamp(a.VelocityY+(u+s.z)*.5,-64,64);
        Air[i]=a;
    }
}
