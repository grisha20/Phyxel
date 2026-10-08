#include "PhysicsShared.hlsli"
StructuredBuffer<GridCell> Cells : register(t0);
StructuredBuffer<MaterialProperties> Materials : register(t1);
RWStructuredBuffer<uint> Tiles : register(u0);
groupshared uint GroupHasGas;

// Rebuilt immediately before every gas tick, after brush/phase changes.
// A full 64-pixel halo covers 8 motion substeps, their pairs and obstacle
// probes, even when a gas crosses a tile corner. It is an acceleration mask,
// never a frozen list of particles: all checkerboard phases remain in order.
[numthreads(16,16,1)]
void CSMain(uint3 id : SV_DispatchThreadID, uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    // All lanes, including partial edge groups, must reach both barriers.
    if(lane==0)GroupHasGas=0;
    GroupMemoryBarrierWithGroupSync();
    if(id.x<Width && id.y<Height)
    {
        GridCell c=Cells[id.y*Width+id.x];
        if(c.IsActive!=0 && Materials[c.MaterialIndex].SimulationKind==SimulationKindGas)
            InterlockedOr(GroupHasGas,1);
    }
    GroupMemoryBarrierWithGroupSync();
    if(lane!=0 || GroupHasGas==0)return;
    // 16 divides 64: every lane of this group has the same original tile.
    int2 tile=int2(group.xy)/4;
    uint tw=(Width+63)/64, th=(Height+63)/64;
    for(int y=-1;y<=1;y++) for(int x=-1;x<=1;x++)
    {
        int2 q=tile+int2(x,y);
        if (all(q>=0) && q.x<int(tw) && q.y<int(th)) InterlockedOr(Tiles[uint(q.y)*tw+uint(q.x)],1);
    }
}
