#include "PhysicsShared.hlsli"
StructuredBuffer<MaterialProperties> Materials : register(t0);
RWStructuredBuffer<GridCell> Grid : register(u0);
RWStructuredBuffer<uint> Columns : register(u1);
RWStructuredBuffer<uint> CellMaterials : register(u3);
RWStructuredBuffer<GasMotionState> GasMotion : register(u6);

bool ReadColumn(uint x, out uint top, out uint bottom, out uint material)
{
    uint packed=Columns[x];
    top=(packed&65535u)-1; bottom=(packed>>16)-1; material=0;
    if(packed==0 || top>bottom || bottom>=Height)return false;
    uint index=top*Width+x; GridCell cell=Grid[index];material=cell.MaterialIndex;
    if(cell.IsActive==0 || Materials[material].SimulationKind!=SimulationKindLiquid)return false;
    if(top>0){uint above=CellMaterials[index-Width];uint kind=Materials[above].SimulationKind;
        if(kind!=SimulationKindNone && kind!=SimulationKindGas)return false;}
    return true;
}

bool SupportedColumn(uint x, uint top, uint bottom, uint material)
{
    float density=Materials[material].Density;
    uint gap=0;bool bed=false;
    // A small internal bubble does not turn a closed boiler into a drain.
    // Dense lower liquids support an upper layer too; prove the bed below.
    [loop] for(uint y=bottom+1;y<Height;y++)
    {
        uint below=CellMaterials[y*Width+x],kind=Materials[below].SimulationKind;
        if(kind==SimulationKindLiquid)
        {
            if(Materials[below].Density<density)return false;
            density=Materials[below].Density;gap=0;continue;
        }
        if(kind==SimulationKindNone || kind==SimulationKindGas){if(++gap>3)return false;continue;}
        if(kind!=SimulationKindSolid || gap!=0 || (Materials[below].Flags&MaterialFlagDensityBody)!=0)return false;
        bed=true;break;
    }
    // A closed world edge is a real supporting boundary of the local solver.
    // An open edge must not turn an outlet into a hydrostatic shortcut.
    if(gap!=0 || (!bed && OpenBoundaries!=0))return false;
    // A selective overlay can split a full-looking pool. Reject the blocked
    // column conservatively; do not infer an unseen route around its filter.
    if(FilterCells[0]!=0){[loop] for(uint y=top;y<=bottom;y++)
        if(!FilterAllows(y*Width+x,material,SimulationKindLiquid))return false;}
    return true;
}

bool ClosedEdge(uint x, uint top, uint bottom)
{
    // An absent/one-cell neighbouring run may be the mouth of an open slot.
    // Only a real barrier closes a supported segment at that edge.
    [loop] for(uint y=top;y<=bottom;y++)
    {
        uint kind=Materials[CellMaterials[y*Width+x]].SimulationKind;
        if(kind!=SimulationKindSolid && kind!=SimulationKindGranular)return false;
    }
    return true;
}

bool ClockAllows(uint source,uint target,uint depth)
{
    GridCell cell=Grid[source];MaterialProperties m=Materials[cell.MaterialIndex];
    if(m.LiquidFlowTemperatureSensitivity<=0)return true;
    float rate=18*m.FlowRate*LiquidMobility(m,cell.Temperature)*min(16u,depth);
    uint seed=source^HashValue(target+7919u*95u)^HashValue(FrameIndex);
    return HashUnitFloat(seed)<1-exp(-rate*max(0,DeltaTime));
}

[numthreads(1,1,1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    // One writer owns the column cache. Each connected supported basin gets
    // its own budget, so a higher, already level basin cannot starve it.
    uint x=0;
    [loop] while(x<Width)
    {
        uint top,bottom,material;
        if(!ReadColumn(x,top,bottom,material)){x++;continue;}
        uint left=x,right=x,runTop=top,runBottom=bottom;
        bool supported=SupportedColumn(x,top,bottom,material);
        [loop] while(right+1<Width)
        {
            uint nextTop,nextBottom,nextMaterial;
            if(!ReadColumn(right+1,nextTop,nextBottom,nextMaterial) || nextMaterial!=material ||
                max(runTop,nextTop)>min(runBottom,nextBottom))break;
            // Overlapping runs prove a connected filled route; an abrupt
            // shaft is an outlet, not a supported basin head shortcut.
            supported=abs((int)nextBottom-(int)runBottom)<=3 &&
                SupportedColumn(right+1,nextTop,nextBottom,nextMaterial)&&supported;
            right++;runTop=nextTop;runBottom=nextBottom;
        }
        if(left>0 && !ClosedEdge(left-1,top,bottom))supported=false;
        if(right+1<Width && !ClosedEdge(right+1,runTop,runBottom))supported=false;
        if(!supported){x=right+1;continue;}
        [loop] for(uint transfer=0;transfer<8 && right>left;transfer++)
        {
            uint high=Height,low=0,sourceX=Width,targetX=Width;
            [loop] for(uint column=left;column<=right;column++)
            {
                uint surface=(Columns[column]&65535u)-1;
                uint base=(Columns[column]>>16)-1;
                // Leave the incoming falling jet to the local movement pass.
                // Its supported bulk must not starve the rest of the basin.
                if(abs(Grid[surface*Width+column].VelocityY)>8)continue;
                if(surface<base && (surface<high || (surface==high && (FrameIndex&1)!=0))){high=surface;sourceX=column;}
                if(surface>low || (surface==low && (FrameIndex&1)==0)){low=surface;targetX=column;}
            }
            if(sourceX==Width || targetX==Width || low<=high+1)break;
            uint source=high*Width+sourceX,target=(low-1)*Width+targetX;
            GridCell first=Grid[source],second=Grid[target];
            uint secondKind=second.IsActive!=0?Materials[second.MaterialIndex].SimulationKind:SimulationKindNone;
            if(first.MaterialIndex!=material || (secondKind!=SimulationKindNone && secondKind!=SimulationKindGas))break;
            uint sourceBottom=(Columns[sourceX]>>16)-1,targetBottom=(Columns[targetX]>>16)-1;
            // A residual one-cell film is handled by ordinary local flow.
            // Do not empty a column and invalidate the proved basin route.
            if(high>=sourceBottom)break;
            // Check the newly exposed surface too: a filter above the pool
            // must remain impermeable as the volume reaches its next row.
            if(!FilterAllows(source,material,SimulationKindLiquid) || !FilterAllows(target,material,SimulationKindLiquid))break;
            if(secondKind==SimulationKindGas &&
                !FilterPathAllows(target,source,second.MaterialIndex,secondKind,Width))break;
            if(!ClockAllows(source,target,sourceBottom-high+1))break;
            first.RestFrames=0;first.VelocityX=targetX>sourceX?54:-54;first.VelocityY=0;
            second.RestFrames=0;
            if(HydraulicPressure==0)first.Pressure=0;
            Grid[source]=second;Grid[target]=first;
            GasMotionState gas=GasMotion[source];GasMotion[source]=GasMotion[target];GasMotion[target]=gas;
            CellMaterials[source]=second.IsActive!=0?second.MaterialIndex:0;CellMaterials[target]=material;
            Columns[sourceX]=((high+2)&65535u)|((sourceBottom+1)<<16);
            Columns[targetX]=(low&65535u)|((targetBottom+1)<<16);
        }
        x=right+1;
    }
}
