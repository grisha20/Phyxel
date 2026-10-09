// Same packet operations and serial transfer order as phases 56/57.
// Only the searches within a disjoint block are distributed among its lanes.
#include "CellularAutomataSolver.hlsl"

groupshared uint SearchTop[64], SearchLeft[64], SearchRight[64];
groupshared uint Left, Right, Stop, DonorLeft, DonorRight, DonorTop;
groupshared uint ReachLeft, ReachRight, CandidateX, CandidateTop, PathClear;
groupshared uint Rejected[4], ChosenX, ChosenTop, ChosenTarget, ChosenTargetTop, ChosenDistance;
groupshared int ConnectionColumn;
groupshared uint ConnectionTop,ConnectionBase,ConnectionOverlap,ConnectionPending,ConnectionResult;

// Exact old fallback: preserve its column order, but search each vertical
// run with all lanes. The largest matching y and nearest mismatches reproduce
// the original descending search and the two contiguous-run while loops.
bool CooperativeConnection(uint source,uint target,uint top,uint base,uint material,uint lane)
{
    int direction=target>source?1:-1;
    if(lane==0){ConnectionColumn=(int)source+direction;ConnectionTop=top;ConnectionBase=base;ConnectionResult=1;}
    GroupMemoryBarrierWithGroupSync();
    [loop]while(true){
        if(lane==0){
            ConnectionPending=0;
            [loop]while(ConnectionColumn!=(int)target+direction){
                uint cachedTop,cachedBase;
                if(UnpackWaterColumn(WaterColumnState[ConnectionColumn],cachedTop,cachedBase) &&
                    CellMaterials[cachedTop*Width+ConnectionColumn]==material &&
                    cachedTop<=ConnectionBase && ConnectionBase<=cachedBase){
                    ConnectionTop=max(top,cachedTop);ConnectionBase=cachedBase;ConnectionColumn+=direction;
                }else{ConnectionPending=1;break;}
            }
        }
        GroupMemoryBarrierWithGroupSync();
        if(ConnectionPending==0)break;
        uint overlap=0;
        [loop]for(int y=(int)ConnectionBase-(int)lane;y>=(int)ConnectionTop;y-=64)
            if(CellMaterials[(uint)y*Width+ConnectionColumn]==material){overlap=(uint)y+1;break;}
        SearchTop[lane]=overlap;
        GroupMemoryBarrierWithGroupSync();
        [unroll]for(uint stride=32;stride>0;stride>>=1){
            if(lane<stride)SearchTop[lane]=max(SearchTop[lane],SearchTop[lane+stride]);
            GroupMemoryBarrierWithGroupSync();
        }
        if(lane==0){ConnectionOverlap=SearchTop[0];if(ConnectionOverlap==0)ConnectionResult=0;}
        GroupMemoryBarrierWithGroupSync();
        if(ConnectionResult==0)break;
        uint y0=ConnectionOverlap-1,above=0,below=Height;
        [loop]for(int y=(int)y0-1-(int)lane;y>=(int)top;y-=64)
            if(CellMaterials[(uint)y*Width+ConnectionColumn]!=material){above=(uint)y+1;break;}
        [loop]for(uint y=y0+1+lane;y<Height;y+=64)
            if(CellMaterials[y*Width+ConnectionColumn]!=material){below=y;break;}
        SearchLeft[lane]=above;SearchRight[lane]=below;
        GroupMemoryBarrierWithGroupSync();
        [unroll]for(uint stride=32;stride>0;stride>>=1){
            if(lane<stride){SearchLeft[lane]=max(SearchLeft[lane],SearchLeft[lane+stride]);SearchRight[lane]=min(SearchRight[lane],SearchRight[lane+stride]);}
            GroupMemoryBarrierWithGroupSync();
        }
        if(lane==0){ConnectionTop=max(top,SearchLeft[0]);ConnectionBase=SearchRight[0]-1;ConnectionColumn+=direction;}
        GroupMemoryBarrierWithGroupSync();
    }
    return ConnectionResult!=0;
}

void ReduceDonors(uint lane)
{
    GroupMemoryBarrierWithGroupSync();
    [unroll]for(uint stride=32;stride>0;stride>>=1){
        if(lane<stride){
            uint other=lane+stride;
            if(SearchTop[other]<SearchTop[lane]){
                SearchTop[lane]=SearchTop[other];SearchLeft[lane]=SearchLeft[other];SearchRight[lane]=SearchRight[other];
            }else if(SearchTop[other]==SearchTop[lane]){
                SearchLeft[lane]=min(SearchLeft[lane],SearchLeft[other]);
                SearchRight[lane]=max(SearchRight[lane],SearchRight[other]);
            }
        }
        GroupMemoryBarrierWithGroupSync();
    }
}

[numthreads(64,1,1)]
void CSMain(uint3 group : SV_GroupID,uint lane : SV_GroupIndex)
{
    uint blockWidth=SimulationPhase==56?OrdinarySurfaceBlockWidth:OrdinaryLocalSurfaceBlockWidth;
    int start=(int)(group.x*blockWidth)-(int)((FrameIndex&1)*blockWidth/2);
    if(start>=(int)Width || start+(int)blockWidth<=0)return;
    if(lane==0){Left=(uint)max(start,0);Right=(uint)min(start+(int)blockWidth-1,(int)Width-1);Stop=0;}
    GroupMemoryBarrierWithGroupSync();
    if(Right<=Left)return;
    uint budget=SimulationPhase==56?8:4;
    [loop]for(uint transfer=0;transfer<budget;transfer++){
        uint high=Height,left=Width,right=0;
        [loop]for(uint x=Left+lane;x<=Right;x+=64){
            uint top,base;
            if(!UnpackWaterColumn(WaterColumnState[x],top,base))continue;
            GridCell cell=Grid[top*Width+x];
            uint above=top>0?CellKindAt(uint2(x,top-1)):SimulationKindSolid;
            bool perched=base+1<Height && (Materials[CellMaterials[(base+1)*Width+x]].Flags&MaterialFlagDensityBody)!=0;
            bool ready=abs(cell.VelocityY)<=8 && Materials[cell.MaterialIndex].LiquidFlowTemperatureSensitivity<=0 &&
                !perched && (above==SimulationKindNone || above==SimulationKindGas) && top<base &&
                CellMaterials[(top+1)*Width+x]==cell.MaterialIndex;
            if(ready){
                if(top<high){high=top;left=x;right=x;}
                else if(top==high){left=min(left,x);right=max(right,x);}
            }
        }
        SearchTop[lane]=high;SearchLeft[lane]=left;SearchRight[lane]=right;
        ReduceDonors(lane);
        if(lane==0){
            DonorLeft=SearchLeft[0];DonorRight=SearchRight[0];DonorTop=SearchTop[0];
            ChosenX=Width;ChosenTarget=Width;ChosenTargetTop=0;ChosenDistance=Width+1;
            if(DonorLeft==Width)Stop=1;
        }
        GroupMemoryBarrierWithGroupSync();
        if(Stop!=0)break;
        [unroll]for(uint donor=0;donor<2;donor++){
            uint source=donor==0?DonorLeft:DonorRight;
            // The first obstacle on each side defines the old serial reachable interval.
            uint barrierLeft=Left,barrierRight=Right;
            [loop]for(uint x=Left+lane;x<=Right;x+=64){
                uint material=CellMaterials[DonorTop*Width+x],kind=CellKindFromMaterial(material);
                bool floating=kind==SimulationKindSolid && (Materials[material].Flags&MaterialFlagDensityBody)!=0;
                bool blocked=kind!=SimulationKindNone && kind!=SimulationKindLiquid && kind!=SimulationKindGas && !floating;
                if(blocked && x<source)barrierLeft=max(barrierLeft,x+1);
                if(blocked && x>source)barrierRight=min(barrierRight,x-1);
            }
            SearchLeft[lane]=barrierLeft;SearchRight[lane]=barrierRight;
            GroupMemoryBarrierWithGroupSync();
            [unroll]for(uint stride=32;stride>0;stride>>=1){
                if(lane<stride){SearchLeft[lane]=max(SearchLeft[lane],SearchLeft[lane+stride]);SearchRight[lane]=min(SearchRight[lane],SearchRight[lane+stride]);}
                GroupMemoryBarrierWithGroupSync();
            }
            if(lane==0){ReachLeft=SearchLeft[0];ReachRight=SearchRight[0];PathClear=0;}
            GroupMemoryBarrierWithGroupSync();
            uint sourceTop,sourceBase;
            UnpackWaterColumn(WaterColumnState[source],sourceTop,sourceBase);
            uint material=CellMaterials[DonorTop*Width+source];
            [loop]for(uint attempt=0;attempt<4;attempt++){
                uint bestX=Width,bestTop=0,bestDistance=Width+1;
                [loop]for(uint x=ReachLeft+lane;x<=ReachRight;x+=64){
                    bool rejected=false;
                    [unroll]for(uint i=0;i<4;i++)if(i<attempt && Rejected[i]==x)rejected=true;
                    uint top,base;
                    if(rejected || !UnpackWaterColumn(WaterColumnState[x],top,base) || top<=DonorTop+1 ||
                        top>min(sourceBase,base) || CellMaterials[top*Width+x]!=material)continue;
                    uint kind=CellKindAt(uint2(x,top-1));
                    if(kind!=SimulationKindNone && kind!=SimulationKindGas)continue;
                    uint distance=max(source,x)-min(source,x);
                    if(bestX==Width || top>bestTop || (top==bestTop && (distance<bestDistance || (distance==bestDistance && x<bestX)))){
                        bestX=x;bestTop=top;bestDistance=distance;
                    }
                }
                SearchLeft[lane]=bestX;SearchTop[lane]=bestTop;SearchRight[lane]=bestDistance;
                GroupMemoryBarrierWithGroupSync();
                [unroll]for(uint stride=32;stride>0;stride>>=1){
                    if(lane<stride){uint other=lane+stride;
                        if(SearchLeft[other]!=Width && (SearchLeft[lane]==Width || SearchTop[other]>SearchTop[lane] ||
                            (SearchTop[other]==SearchTop[lane] && (SearchRight[other]<SearchRight[lane] ||
                            (SearchRight[other]==SearchRight[lane] && SearchLeft[other]<SearchLeft[lane]))))){
                            SearchLeft[lane]=SearchLeft[other];SearchTop[lane]=SearchTop[other];SearchRight[lane]=SearchRight[other];
                        }
                    }
                    GroupMemoryBarrierWithGroupSync();
                }
                if(lane==0){CandidateX=SearchLeft[0];CandidateTop=SearchTop[0];}
                GroupMemoryBarrierWithGroupSync();
                if(CandidateX==Width)break;
                uint clear=1,connected=1;
                [loop]for(uint y=DonorTop+lane;y<CandidateTop;y+=64){
                    uint kind=CellKindAt(uint2(CandidateX,y));
                    if(kind!=SimulationKindNone && kind!=SimulationKindLiquid && kind!=SimulationKindGas)clear=0;
                }
                uint distance=max(source,CandidateX)-min(source,CandidateX);
                int direction=CandidateX>source?1:-1;
                [loop]for(uint path=lane+1;path<=distance;path+=64){
                    uint x=(uint)((int)source+direction*(int)path),previous=(uint)((int)x-direction);
                    uint top,base,previousTop,previousBase;
                    if(!UnpackWaterColumn(WaterColumnState[x],top,base) ||
                        !UnpackWaterColumn(WaterColumnState[previous],previousTop,previousBase) ||
                        CellMaterials[top*Width+x]!=material || top>previousBase || previousBase>base)connected=0;
                }
                SearchLeft[lane]=clear;SearchRight[lane]=connected;
                GroupMemoryBarrierWithGroupSync();
                [unroll]for(uint stride=32;stride>0;stride>>=1){
                    if(lane<stride){SearchLeft[lane]&=SearchLeft[lane+stride];SearchRight[lane]&=SearchRight[lane+stride];}
                    GroupMemoryBarrierWithGroupSync();
                }
                if(lane==0){PathClear=SearchLeft[0]==0?0:SearchRight[0]!=0?1:2;Rejected[attempt]=CandidateX;}
                GroupMemoryBarrierWithGroupSync();
                if(PathClear==2){
                    bool connectedExactly=CooperativeConnection(source,CandidateX,DonorTop,sourceBase,material,lane);
                    if(lane==0)PathClear=connectedExactly?1:0;
                }
                GroupMemoryBarrierWithGroupSync();
                if(CandidateX==Width || PathClear!=0)break;
            }
            if(lane==0 && PathClear!=0){
                uint distance=max(source,CandidateX)-min(source,CandidateX);
                if(ChosenX==Width || CandidateTop>ChosenTargetTop || (CandidateTop==ChosenTargetTop && distance<ChosenDistance)){
                    ChosenX=source;ChosenTop=DonorTop;ChosenTarget=CandidateX;ChosenTargetTop=CandidateTop;ChosenDistance=distance;
                }
            }
            GroupMemoryBarrierWithGroupSync();
        }
        if(lane==0){
            Stop=1;
            if(ChosenX!=Width && ChosenTarget!=Width){
                uint ignored,base,targetBase;
                if(UnpackWaterColumn(WaterColumnState[ChosenX],ignored,base) && UnpackWaterColumn(WaterColumnState[ChosenTarget],ignored,targetBase)){
                    uint a=ChosenTop*Width+ChosenX,b=(ChosenTargetTop-1)*Width+ChosenTarget;
                    uint kind=CellKindAtIndex(b);
                    [branch]if(CellKindAtIndex(a)==SimulationKindLiquid && (kind==SimulationKindNone || kind==SimulationKindGas) &&
                        abs(Grid[ChosenTargetTop*Width+ChosenTarget].VelocityY)<=8){
                        if(MoveOrdinaryWater(a,b,ChosenTarget>ChosenX?1:-1)){
                            WaterColumnState[ChosenX]=ChosenTop==base?0:PackWaterColumn(ChosenTop+1,base);
                            WaterColumnState[ChosenTarget]=PackWaterColumn(ChosenTargetTop-1,targetBase);Stop=0;
                        }
                    }
                }
            }
        }
        AllMemoryBarrierWithGroupSync();
        if(Stop!=0)break;
    }
}
