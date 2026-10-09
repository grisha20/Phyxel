// Compile the existing liquid functions separately from the large multi-phase
// solver. Keep its thread geometry, ownership, clocks and packet operations.
#include "CellularAutomataSolver.hlsl"

[numthreads(16,16,1)]
void CSAdjacentSurface(uint3 id : SV_DispatchThreadID)
{
    if(id.x>=DispatchExtentX || id.y>=DispatchExtentY)return;
    uint2 coordinate=id.xy+uint2(DispatchOffsetX,DispatchOffsetY);
    uint offset=FrameIndex&1;
    if(coordinate.x<Width && coordinate.y<Height && coordinate.x>=offset && ((coordinate.x-offset)&1)==0)
        ResolveWaterColumnSpan(coordinate,1);
}

[numthreads(16,16,1)]
void CSBroadSurface(uint3 id : SV_DispatchThreadID)
{
    if(id.x>=DispatchExtentX || id.y>=DispatchExtentY)return;
    uint x=id.x+DispatchOffsetX;
    if(x<Width)ResolveOrdinarySurfaceBlock(x);
}

[numthreads(16,16,1)]
void CSLocalSurface(uint3 id : SV_DispatchThreadID)
{
    if(id.x>=DispatchExtentX || id.y>=DispatchExtentY)return;
    uint x=id.x+DispatchOffsetX;
    if(x<Width)ResolveOrdinaryLocalSurfaceBlock(x);
}

[numthreads(16,16,1)]
void CSViscousSurface(uint3 id : SV_DispatchThreadID)
{
    if(id.x>=DispatchExtentX || id.y>=DispatchExtentY)return;
    uint x=id.x+DispatchOffsetX;
    if(x<Width)ResolveViscousSurfaceBlock(x);
}
