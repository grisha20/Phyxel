#include "PhysicsShared.hlsli"
StructuredBuffer<MaterialProperties> Materials : register(t0);
RWStructuredBuffer<GridCell> Grid : register(u0);
RWStructuredBuffer<uint> Columns : register(u1);
RWStructuredBuffer<uint> ColumnSupport : register(u2);
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

[numthreads(64,1,1)]
void CSPrepareSupport(uint3 id : SV_DispatchThreadID)
{
    uint x=id.x;
    if(x>=Width)return;
    uint top,bottom,material;
    bool valid=ReadColumn(x,top,bottom,material);
    uint flags=valid ? 1u : 0u;
    if(valid){
        if(SupportedColumn(x,top,bottom,material))flags|=2u;
        if(abs(Grid[top*Width+x].VelocityY)<=8)flags|=4u;
    }
    ColumnSupport[x*3]=material;
    ColumnSupport[x*3+1]=flags;
    ColumnSupport[x*3+2]=Columns[x];
}

bool ReadCachedColumn(uint x,out uint top,out uint bottom,out uint material)
{
    uint packed=ColumnSupport[x*3+2];top=(packed&65535u)-1;bottom=(packed>>16)-1;
    material=ColumnSupport[x*3];
    return (ColumnSupport[x*3+1]&1u)!=0;
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

groupshared uint BasinRight,BasinMaterial,BasinStop;
groupshared uint High[64],Low[64],Sources[64],Targets[64];

// A group owns one initial basin; lanes search its extrema together instead
// of issuing thousands of dependent UAV reads from a single GPU lane.
[numthreads(64,1,1)]
void CSBalanceCached(uint3 group : SV_GroupID,uint lane : SV_GroupIndex)
{
    uint left=group.x;
    if(lane==0){
        BasinStop=1;BasinRight=left;BasinMaterial=0;
        uint top,bottom,material;
        if(ReadCachedColumn(left,top,bottom,material)){
            uint previousTop,previousBottom,previousMaterial;
            bool leader=left==0 || !ReadCachedColumn(left-1,previousTop,previousBottom,previousMaterial) ||
                previousMaterial!=material || max(previousTop,top)>min(previousBottom,bottom);
            if(leader){
                uint right=left,runTop=top,runBottom=bottom;
                bool supported=(ColumnSupport[left*3+1]&2u)!=0;
                [loop] while(right+1<Width){
                    uint nextTop,nextBottom,nextMaterial;
                    if(!ReadCachedColumn(right+1,nextTop,nextBottom,nextMaterial) || nextMaterial!=material ||
                        max(runTop,nextTop)>min(runBottom,nextBottom))break;
                    supported=abs((int)nextBottom-(int)runBottom)<=3 &&
                        (ColumnSupport[(right+1)*3+1]&2u)!=0 && supported;
                    right++;runTop=nextTop;runBottom=nextBottom;
                }
                if(left>0 && !ClosedEdge(left-1,top,bottom))supported=false;
                if(right+1<Width && !ClosedEdge(right+1,runTop,runBottom))supported=false;
                BasinRight=right;BasinMaterial=material;BasinStop=supported && right>left ? 0u : 1u;
            }
        }
    }
    GroupMemoryBarrierWithGroupSync();
    if(BasinStop!=0)return;
    [loop] for(uint transfer=0;transfer<8;transfer++){
        uint high=Height,low=0,sourceX=Width,targetX=Width;
        [loop] for(uint column=left+lane;column<=BasinRight;column+=64){
            uint packed=Columns[column],surface=(packed&65535u)-1,base=(packed>>16)-1;
            if((ColumnSupport[column*3+1]&4u)==0)continue;
            if(surface<base && (surface<high || (surface==high && (FrameIndex&1)!=0))){high=surface;sourceX=column;}
            if(surface>low || (surface==low && (FrameIndex&1)==0)){low=surface;targetX=column;}
        }
        High[lane]=high;Low[lane]=low;Sources[lane]=sourceX;Targets[lane]=targetX;
        GroupMemoryBarrierWithGroupSync();
        [unroll] for(uint stride=32;stride>0;stride>>=1){
            if(lane<stride){
                uint other=lane+stride;
                if(Sources[other]!=Width && (Sources[lane]==Width || High[other]<High[lane] ||
                    (High[other]==High[lane] && ((FrameIndex&1)!=0 ? Sources[other]>Sources[lane] : Sources[other]<Sources[lane])))){
                    High[lane]=High[other];Sources[lane]=Sources[other];}
                if(Targets[other]!=Width && (Targets[lane]==Width || Low[other]>Low[lane] ||
                    (Low[other]==Low[lane] && ((FrameIndex&1)==0 ? Targets[other]>Targets[lane] : Targets[other]<Targets[lane])))){
                    Low[lane]=Low[other];Targets[lane]=Targets[other];}
            }
            GroupMemoryBarrierWithGroupSync();
        }
        if(lane==0){
            sourceX=Sources[0];targetX=Targets[0];high=High[0];low=Low[0];
            BasinStop=1;
            if(sourceX!=Width && targetX!=Width && low>high+1){
                uint source=high*Width+sourceX,target=(low-1)*Width+targetX;
                GridCell first=Grid[source],second=Grid[target];
                uint secondKind=second.IsActive!=0?Materials[second.MaterialIndex].SimulationKind:SimulationKindNone;
                uint sourceBottom=(Columns[sourceX]>>16)-1,targetBottom=(Columns[targetX]>>16)-1;
                if(first.MaterialIndex==BasinMaterial && (secondKind==SimulationKindNone || secondKind==SimulationKindGas) &&
                    high<sourceBottom && FilterAllows(source,BasinMaterial,SimulationKindLiquid) &&
                    FilterAllows(target,BasinMaterial,SimulationKindLiquid) &&
                    (secondKind!=SimulationKindGas || FilterPathAllows(target,source,second.MaterialIndex,secondKind,Width)) &&
                    ClockAllows(source,target,sourceBottom-high+1)){
                    first.RestFrames=0;first.VelocityX=targetX>sourceX?54:-54;first.VelocityY=0;second.RestFrames=0;
                    if(HydraulicPressure==0)first.Pressure=0;
                    Grid[source]=second;Grid[target]=first;
                    GasMotionState gas=GasMotion[source];GasMotion[source]=GasMotion[target];GasMotion[target]=gas;
                    CellMaterials[source]=second.IsActive!=0?second.MaterialIndex:0;CellMaterials[target]=BasinMaterial;
                    Columns[sourceX]=((high+2)&65535u)|((sourceBottom+1)<<16);
                    Columns[targetX]=(low&65535u)|((targetBottom+1)<<16);
                    uint flags=ColumnSupport[sourceX*3+1]&~4u;
                    if(abs(Grid[(high+1)*Width+sourceX].VelocityY)<=8)flags|=4u;
                    ColumnSupport[sourceX*3+1]=flags;ColumnSupport[targetX*3+1]|=4u;
                    BasinStop=0;
                }
            }
        }
        // Packet writes and updated readiness must be visible to every lane.
        AllMemoryBarrierWithGroupSync();
        if(BasinStop!=0)return;
    }
}
