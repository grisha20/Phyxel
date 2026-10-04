#include "PhysicsShared.hlsli"
StructuredBuffer<GridCell> Cells : register(t0);
StructuredBuffer<MaterialProperties> Materials : register(t1);
RWStructuredBuffer<uint> Tiles : register(u0);

// Rebuilt immediately before every gas tick, after brush/phase changes.
// A full 64-pixel halo covers 8 motion substeps, their pairs and obstacle
// probes, even when a gas crosses a tile corner. It is an acceleration mask,
// never a frozen list of particles: all checkerboard phases remain in order.
[numthreads(16,16,1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x>=Width || id.y>=Height) return;
    GridCell c=Cells[id.y*Width+id.x];
    if (c.IsActive==0 || Materials[c.MaterialIndex].SimulationKind!=SimulationKindGas) return;
    int2 tile=int2(id.xy)/64;
    uint tw=(Width+63)/64, th=(Height+63)/64;
    for(int y=-1;y<=1;y++) for(int x=-1;x<=1;x++)
    {
        int2 q=tile+int2(x,y);
        if (all(q>=0) && q.x<int(tw) && q.y<int(th)) InterlockedOr(Tiles[uint(q.y)*tw+uint(q.x)],1);
    }
}
