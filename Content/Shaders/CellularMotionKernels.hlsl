// Compile pair movement without the unrelated gas/pressure/surface branches.
// Ownership, parity and packet operations are the existing solver's.
#include "CellularAutomataSolver.hlsl"

bool PairCannotMove(uint a,uint b,bool side)
{
    uint material=CellMaterials[a];
    if(material!=CellMaterials[b])return false;
    uint kind=CellKindFromMaterial(material);
    if(kind==SimulationKindNone || kind==SimulationKindSolid)return true;
    if(kind!=SimulationKindLiquid || Materials[material].MoistureCapacity>0)return false;
    if(Grid[b].Mass>=.999999)return true;
    return side && Grid[a].Mass>=.999999;
}

[numthreads(16,16,1)]
void CSVertical(uint3 id : SV_DispatchThreadID)
{
    if(id.x>=DispatchExtentX || id.y>=DispatchExtentY)return;
    uint2 p=uint2(DispatchOffsetX+id.x,DispatchOffsetY+id.y*2);
    if(p.x<Width && p.y+1<Height){
        uint a=p.y*Width+p.x;
        [branch]if(PairCannotMove(a,a+Width,false))return;
        ResolveVerticalPair(p);
    }
}
[numthreads(16,16,1)]
void CSHorizontal(uint3 id : SV_DispatchThreadID)
{
    if(id.x>=DispatchExtentX || id.y>=DispatchExtentY)return;
    uint2 p=uint2(DispatchOffsetX+id.x*2,DispatchOffsetY+id.y);
    if(p.x+1<Width && p.y<Height){
        uint a=p.y*Width+p.x;
        [branch]if(PairCannotMove(a,a+1,true))return;
        ResolveHorizontalPair(p);
    }
}
[numthreads(16,16,1)]
void CSDiagonal(uint3 id : SV_DispatchThreadID)
{
    if(id.x>=DispatchExtentX || id.y>=DispatchExtentY)return;
    uint2 p=uint2(DispatchOffsetX,DispatchOffsetY)+id.xy*2;
    if(p.x+1>=Width || p.y+1>=Height)return;
    uint orientation=(SimulationPhase-5)&1;
    uint2 upper=orientation==0?p:p+uint2(1,0),lower=orientation==0?p+uint2(1,1):p+uint2(0,1);
    [branch]if(PairCannotMove(upper.y*Width+upper.x,lower.y*Width+lower.x,false))return;
    ResolveDiagonalPair(upper,lower);
}
