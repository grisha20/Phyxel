#include "PhysicsShared.hlsli"
StructuredBuffer<GridCell> Grid : register(t0);
StructuredBuffer<MaterialProperties> Materials : register(t1);
globallycoherent RWStructuredBuffer<uint> Parents : register(u0);
RWStructuredBuffer<uint> Links : register(u1);
#define FineAirWidth Width
#define FineAirHeight Height
#define FineAirMaterialAt(p) Grid[(p).y*Width+(p).x].MaterialIndex
#define FineAirMaterials Materials
#define FineAirBlockGranular false
#include "FineAirGeometry.hlsli"
uint AW(){return (Width+AirCellSize-1)/AirCellSize;}
uint AH(){return (Height+AirCellSize-1)/AirCellSize;}
// Root zero is the ambient boundary, node indices are offset by one.
uint Root(uint i)
{
    // Reading an ancestor does not mutate it. A stale ancestor remains in
    // the same component; Join still validates the actual root with CAS.
    [loop]for(uint step=0;step<32;step++)
    {uint next=Parents[i];if(next==i)return i;i=next;}
    return i;
}
void Join(uint a,uint b)
{
    // Parents only move toward smaller indices; sharing a parent proves the
    // edge is already connected. Avoid atomic root walks on settled edges.
    if(Parents[a]==Parents[b])return;
    [loop]for(uint attempt=0;attempt<32;attempt++)
    {
        a=Root(a);b=Root(b);if(a==b)return;
        uint high=max(a,b),low=min(a,b),observed;
        // Only replace a root. A stale root must be retried, not overwritten:
        // otherwise an already joined branch could become disconnected.
        InterlockedCompareExchange(Parents[high],high,low,observed);
        if(observed==high)return;
    }
}
[numthreads(8,8,1)]
void CSInitialize(uint3 id:SV_DispatchThreadID)
{
    if(id.x>=AW()||id.y>=AH())return;uint i=id.y*AW()+id.x;
    if(i==0)Parents[0]=0;
    int2 p=int2(id.xy)*int(AirCellSize)+int(AirCellSize/2);
    uint links=0;
    if(!AirFineBlocked(p))
    {
        if(id.x+1<AW() && AirFineSegmentOpen(p,p+int2(AirCellSize,0)))links|=1;
        if(id.y+1<AH() && AirFineSegmentOpen(p,p+int2(0,AirCellSize)))links|=2;
        // The last partial coarse cell can have its nominal center outside
        // the world. Seed the last visible center via its fine edge path.
        if((id.x==0 && AirFineSegmentOpen(p,int2(0,p.y))) ||
           (id.y==0 && AirFineSegmentOpen(p,int2(p.x,0))) ||
           (p.x+int(AirCellSize)>=int(Width) && AirFineSegmentOpen(p,int2(Width-1,p.y))) ||
           (p.y+int(AirCellSize)>=int(Height) && AirFineSegmentOpen(p,int2(p.x,Height-1))))links|=4;
    }
    Parents[i+1]=i+1;Links[i]=links;
}
[numthreads(8,8,1)]
void CSUnion(uint3 id:SV_DispatchThreadID)
{
    if(id.x>=AW()||id.y>=AH())return;uint i=id.y*AW()+id.x,bits=Links[i];
    if(bits&4)Join(i+1,0);
    if(bits&1)Join(i+1,i+2);
    if(bits&2)Join(i+1,i+AW()+1);
}
[numthreads(256,1,1)]
void CSCompress(uint3 id:SV_DispatchThreadID)
{
    if(id.x>AW()*AH())return;
    uint parent=Parents[id.x];
    if(parent==0 || parent==id.x)return;
    // Atomic min avoids racing with a concurrent compression of an ancestor.
    uint ignored;InterlockedMin(Parents[id.x],Root(id.x),ignored);
}
