#include "PhysicsShared.hlsli"

RWStructuredBuffer<GridCell> WaterGrid : register(u0);
StructuredBuffer<MaterialProperties> Materials : register(t0);
StructuredBuffer<uint> WetWallMask : register(t1);
RWStructuredBuffer<uint> BoilingSummary : register(u1);
RWStructuredBuffer<uint> BoilingCellMaterials : register(u2);
RWStructuredBuffer<GasMotionState> BoilingGasMotion : register(u3);
RWStructuredBuffer<uint> MovementColumns : register(u4);
RWStructuredBuffer<uint> QuenchTileOutput : register(u5);
StructuredBuffer<uint> QuenchTiles : register(t2);
#include "PhaseEnthalpy.hlsli"

groupshared uint QuenchTileActive;
[numthreads(16,16,1)]
void CSPrepareQuench(uint3 thread : SV_DispatchThreadID,uint3 group : SV_GroupID,uint lane : SV_GroupIndex)
{
    if(lane==0)QuenchTileActive=0;
    GroupMemoryBarrierWithGroupSync();
    if(thread.x<Width && thread.y<Height)
    {
        uint index=thread.y*Width+thread.x;
        bool active=(WetWallMask[index] & 0x40000000u)!=0;
        GridCell cell=WaterGrid[index];
        if(cell.IsActive!=0)
        {
            MaterialProperties m=Materials[cell.MaterialIndex];
            if(m.SimulationKind==SimulationKindGas)
            {
                uint count,stride;Materials.GetDimensions(count,stride);
                if(m.TransitionBelowMaterialIndex<count)
                    active=active || (Materials[m.TransitionBelowMaterialIndex].Flags & MaterialFlagSurfaceBoiling)!=0;
            }
        }
        if(active){uint ignored;InterlockedOr(QuenchTileActive,1,ignored);}
    }
    GroupMemoryBarrierWithGroupSync();
    if(lane==0)QuenchTileOutput[group.y*((Width+15)/16)+group.x]=QuenchTileActive;
}

[numthreads(16,16,1)]
void CSPrepareMovement(uint3 thread : SV_DispatchThreadID)
{
    if(thread.x>=Width || thread.y>=Height)return;
    GridCell cell=WaterGrid[thread.y*Width+thread.x];
    if(cell.IsActive==0)return;
    MaterialProperties m=Materials[cell.MaterialIndex];
    if((m.Flags & MaterialFlagSurfaceBoiling)==0)return;
    if(cell.Mass>1 || (cell.BodyId & VapourCushionMarker)!=0 ||
        (cell.Temperature>=m.TransitionAboveTemperature-.001 && cell.Lifetime>0)){
        uint ignored;InterlockedOr(MovementColumns[thread.x],1,ignored);
    }
}

bool IsThinFilmColumn(uint index,GridCell cell)
{
    uint x=index%Width,span=1;
    [unroll]for(int direction=-1;direction<=1;direction+=2)
    [loop]for(int distance=1;distance<=24;distance++)
    {
        int q=(int)x+direction*distance;
        if(q<0 || q>=(int)Width)break;
        GridCell side=WaterGrid[(index/Width)*Width+q];
        if(side.IsActive==0 || side.MaterialIndex!=cell.MaterialIndex)break;
        if(++span>24)return false;
    }
    uint y=index/Width;
    [loop]for(uint depth=1;depth<=SurfaceFilmMaximumDepth;depth++)
    {
        if(depth>y)return false;
        GridCell above=WaterGrid[index-depth*Width];
        if(above.IsActive==0 || Materials[above.MaterialIndex].SimulationKind==SimulationKindGas)return true;
        if(above.MaterialIndex!=cell.MaterialIndex)return false;
    }
    return false;
}

void QuenchPair(uint a,uint b,bool optimized)
{
    GridCell first=WaterGrid[a],second=WaterGrid[b];
    if(first.IsActive==0 || second.IsActive==0)return;
    // Identical liquids cannot be a wet solid pair, a liquid/solid contact,
    // or a liquid/vapour interface. Keep the original path for comparison.
    [branch]if(optimized && first.MaterialIndex==second.MaterialIndex &&
        Materials[first.MaterialIndex].SimulationKind==SimulationKindLiquid)return;
    MaterialProperties ma=Materials[first.MaterialIndex],mb=Materials[second.MaterialIndex];
    bool wetA=(WetWallMask[a] & 0x40000000u)!=0,wetB=(WetWallMask[b] & 0x40000000u)!=0;
    bool waterA=(ma.Flags & MaterialFlagSurfaceBoiling)!=0,waterB=(mb.Flags & MaterialFlagSurfaceBoiling)!=0;
    bool metalPair=wetA && wetB && first.MaterialIndex==second.MaterialIndex;
    bool contact=(wetA && waterB) || (wetB && waterA);
    bool condensing=(waterA && mb.SimulationKind==SimulationKindGas && mb.TransitionBelowMaterialIndex==first.MaterialIndex) ||
        (waterB && ma.SimulationKind==SimulationKindGas && ma.TransitionBelowMaterialIndex==second.MaterialIndex);
    if(!metalPair && !contact && !condensing)return;
    if(contact)
    {
        GridCell water=first,wall=second;uint index=a;
        if(!waterA){water=second;wall=first;index=b;}
        if(water.Temperature>=Materials[water.MaterialIndex].TransitionAboveTemperature-.001 &&
            wall.Temperature>Materials[water.MaterialIndex].TransitionAboveTemperature+SurfaceFilmTemperatureOffset &&
            IsThinFilmColumn(index,water))return;
    }
    // Condensation exchanges energy at a moving phase boundary, rather than
    // conducting it through a stationary slab of insulating steam.
    float coefficient=.8*(condensing?1:saturate(min(ma.ThermalConductivity,mb.ThermalConductivity)))*
        min(CellEffectiveCapacity(first),CellEffectiveCapacity(second));
    float q=coefficient*(second.Temperature-first.Temperature);
    first=SetCellSpecificEnthalpy(first,CellSpecificEnthalpy(first)+q/first.Mass);
    second=SetCellSpecificEnthalpy(second,CellSpecificEnthalpy(second)-q/second.Mass);
    WaterGrid[a]=first;WaterGrid[b]=second;
}

void RelieveLiquidOverfill(uint x)
{
    // A nucleating bubble displaces liquid into its adjacent parcel. Carry
    // excess up through this same column until an empty volume is reached.
    // A bubble obstructing the column is displaced by one cell with its motion
    // state, never erased or teleported straight to the free surface.
    [loop]for(int y=(int)Height-2;y>1;y--)
    {
        uint source=y*Width+x,target=source-Width;
        GridCell a=WaterGrid[source],b=WaterGrid[target];
        if(a.IsActive==0 || a.Mass<=1 || (Materials[a.MaterialIndex].Flags & MaterialFlagSurfaceBoiling)==0 ||
            (a.BodyId & VapourCushionMarker)!=0)continue;
        if(!FilterAllows(source,a.MaterialIndex,SimulationKindLiquid) || !FilterAllows(target,a.MaterialIndex,SimulationKindLiquid))continue;
        if(b.IsActive!=0 && b.MaterialIndex!=a.MaterialIndex)
        {
            if(Materials[b.MaterialIndex].SimulationKind!=SimulationKindGas)continue;
            int freeY=y-1;bool route=true;
            [loop]while(freeY>1)
            {
                GridCell parcel=WaterGrid[freeY*Width+x];
                if(parcel.IsActive==0)break;
                uint kind=Materials[parcel.MaterialIndex].SimulationKind;
                if((kind!=SimulationKindGas && parcel.MaterialIndex!=a.MaterialIndex) ||
                    (parcel.BodyId & VapourCushionMarker)!=0 ||
                    !FilterAllows(freeY*Width+x,parcel.MaterialIndex,kind) ||
                    !FilterAllows((freeY-1)*Width+x,parcel.MaterialIndex,kind)){route=false;break;}
                freeY--;
            }
            if(!route || freeY<=1)continue;
            [loop]for(int row=freeY;row<y-1;row++)
            {
                uint to=row*Width+x,from=to+Width;
                GridCell parcel=WaterGrid[from];WaterGrid[to]=parcel;
                BoilingCellMaterials[to]=parcel.MaterialIndex;BoilingGasMotion[to]=BoilingGasMotion[from];
            }
            b=CreateEmptyCell();BoilingGasMotion[target]=(GasMotionState)0;
        }
        if((b.BodyId & VapourCushionMarker)!=0)continue;
        float moved=min(1,a.Mass-1),specific=CellSpecificEnthalpy(a);
        float energy=b.IsActive!=0?b.Mass*CellSpecificEnthalpy(b):0;
        if(b.IsActive==0){b=CreateEmptyCell();b.IsActive=1;b.MaterialIndex=a.MaterialIndex;}
        b.Mass+=moved;b=SetCellSpecificEnthalpy(b,(energy+moved*specific)/b.Mass);
        a.Mass-=moved;a.RestFrames=0;b.RestFrames=0;
        WaterGrid[source]=a;WaterGrid[target]=b;BoilingCellMaterials[target]=b.MaterialIndex;
        InterlockedOr(BoilingSummary[0],PhaseSummaryPhaseOccurred | PhaseSummaryTargetCellular | PhaseSummaryTargetGas | PhaseSummaryTouchesLiquid);
    }
}

// One owner per column advances a small resolved drop at the fixed thermal
// tick. This is a grid trajectory, not surface tension or a physical impact
// solver. Every swept face is checked; occupied parcels are never overwritten.
uint AdvanceDrop(uint x,uint y)
{
    uint bottom=y*Width+x;
    GridCell liquid=WaterGrid[bottom],wall=WaterGrid[bottom+Width];
    MaterialProperties m=Materials[liquid.MaterialIndex],s=Materials[wall.MaterialIndex];
    if(liquid.IsActive==0 || (m.Flags & MaterialFlagSurfaceBoiling)==0)return y;
    bool flying=(liquid.BodyId & VapourCushionMarker)!=0;
    if(wall.IsActive!=0 && wall.MaterialIndex==liquid.MaterialIndex)
    {
        if(flying && (wall.BodyId & VapourCushionMarker)==0)
        {
            liquid.BodyId&=~VapourCushionMarker;liquid.VelocityY=0;
            WaterGrid[bottom]=liquid;
        }
        return y;
    }
    if(!flying && (wall.IsActive==0 || s.SimulationKind!=SimulationKindSolid || s.ThermalConductivity<=.5 ||
        wall.Temperature<=m.TransitionAboveTemperature+SurfaceFilmTemperatureOffset ||
        liquid.Temperature<m.TransitionAboveTemperature-.001 || liquid.Lifetime<=0))return y;
    if(!flying && !IsThinFilmColumn(bottom,liquid))return y;
    uint top=y,depth=1;
    [loop]while(top>0)
    {
        GridCell above=WaterGrid[(top-1)*Width+x];
        if(above.IsActive==0 || above.MaterialIndex!=liquid.MaterialIndex ||
            (flying && (above.BodyId & VapourCushionMarker)==0))break;
        top--;depth++;
        if(!flying && depth>SurfaceFilmMaximumDepth)return y;
    }
    if(top==0)return y;
    float velocity=flying?min(60,liquid.VelocityY+5):-40;
    int wanted=(int)round(velocity*.05),delta=0;
    int direction=wanted<0?-1:1;
    [loop]for(int step=1;step<=abs(wanted);step++)
    {
        int edge=(direction<0?(int)top:(int)y)+direction*step;
        if(edge<1 || edge+1>=(int)Height || WaterGrid[edge*Width+x].IsActive!=0)break;
        bool allowed=true;
        [loop]for(uint row=top;row<=y;row++)
        {
            int to=(int)row+direction*step,from=to-direction;
            if(!FilterAllows(from*Width+x,liquid.MaterialIndex,SimulationKindLiquid) ||
                !FilterAllows(to*Width+x,liquid.MaterialIndex,SimulationKindLiquid))allowed=false;
        }
        if(!allowed)break;
        delta=direction*step;
    }
    bool landed=wanted>0 && delta!=wanted;
    if(wanted<0 && delta!=wanted)velocity=0;
    // Memmove order follows the direction, including overlapping source/target.
    [loop]for(uint i=0;i<depth;i++)
    {
        uint row=delta>0?y-i:top+i;
        GridCell parcel=WaterGrid[row*Width+x];
        parcel.VelocityY=landed?0:velocity;parcel.RestFrames=0;parcel.Pressure=0;
        if(landed)parcel.BodyId&=~VapourCushionMarker;else parcel.BodyId|=VapourCushionMarker;
        uint target=((int)row+delta)*Width+x;
        WaterGrid[target]=parcel;BoilingCellMaterials[target]=parcel.MaterialIndex;
        if(delta!=0){WaterGrid[row*Width+x]=CreateEmptyCell();BoilingCellMaterials[row*Width+x]=0;}
    }
    InterlockedOr(BoilingSummary[0],PhaseSummaryPhaseOccurred | PhaseSummaryTargetCellular | PhaseSummaryTouchesLiquid);
    return y+max(0,delta);
}

// Closed, disjoint 2x2 rotations provide a local upward path and an equal
// downward return. No empty cell, surface, wall or other material can move.
// FrameIndex is a thermal tick. Horizontal mixing blocks are disjoint too.
void MarkConvection(inout GridCell cell, float vx, float vy)
{
    cell.RestFrames = 0;
    cell.VelocityX = vx;
    cell.VelocityY = vy;
}

// Local horizontal parcel exchange represents unresolved liquid mixing.
// Its equal return stays at the SAME height: it cannot destroy stable vertical
// stratification. This is a gameplay closure, not a computed liquid velocity.
void MixLiquidRow(uint2 p, uint span)
{
    if (p.x + span - 1 >= Width || p.y >= Height) return;
    GridCell packets[16];
    uint material = WaterGrid[FlattenCoordinate(p)].MaterialIndex;
    if ((Materials[material].Flags & MaterialFlagLiquidConvection) == 0) return;
    if(!FilterPathAllows(FlattenCoordinate(p),FlattenCoordinate(p+uint2(span-1,0)),material,SimulationKindLiquid,Width))return;
    float minimum = 5000, maximum = -273.15, meanTemperature = 0;
    // All intermediate cells must contain the same liquid and equal mass.
    // Never jump an interior wall, empty pore or oil/water interface.
    for (uint i = 0; i < span; i++)
    {
        GridCell cell = WaterGrid[FlattenCoordinate(p + uint2(i,0))];
        if (cell.IsActive == 0 || cell.MaterialIndex != material || cell.Mass < .99 ||
            abs(cell.Mass - WaterGrid[FlattenCoordinate(p)].Mass) > .001 ||
            (cell.BodyId & VapourCushionMarker)!=0) return;
        packets[i] = cell;
        meanTemperature += cell.Temperature / span;
        minimum = min(minimum,cell.Temperature); maximum = max(maximum,cell.Temperature);
    }
    float contrast = maximum - minimum;
    if (contrast <= .05) return;
    uint seed = FlattenCoordinate(p) ^ HashValue(FrameIndex + 7919u * SimulationPhase);
    float mobility = LiquidMobility(Materials[material], meanTemperature);
    if (HashUnitFloat(seed) >= min(.5, contrast * .5) * min(1.0,mobility)) return;
    for (uint i = 0; i < span; i++)
    {
        uint to = (i + span/2) % span;
        GridCell cell = packets[i];
        MarkConvection(cell, to > i ? 4 : -4, 0);
        WaterGrid[FlattenCoordinate(p+uint2(to,0))] = cell;
    }
}

void WaterStep(uint3 thread,uint subStep,bool optimized)
{
    if(subStep==3)
    {
        uint2 p=thread.xy*uint2(SimulationPhase<2?2:1,SimulationPhase<2?1:2)+uint2(DispatchOffsetX,DispatchOffsetY);
        uint2 q=p+(SimulationPhase<2?uint2(1,0):uint2(0,1));
        if(q.x<Width && q.y<Height)
        {
            if(optimized)
            {
                uint columns=(Width+15)/16;
                [branch]if((QuenchTiles[(p.y/16)*columns+p.x/16] |
                    QuenchTiles[(q.y/16)*columns+q.x/16])==0)return;
            }
            QuenchPair(p.y*Width+p.x,q.y*Width+q.x,optimized);
        }
        return;
    }
    if(subStep==2)
    {
        uint x=thread.x;
        if(thread.y!=0 || x>=Width)return;
        [branch]if(DebugReserved2==0 && MovementColumns[x]==0)return;
        RelieveLiquidOverfill(x);
        [loop]for(uint y=2;y+1<Height;y++)y=AdvanceDrop(x,y);
        return;
    }
    if (subStep > 2)
    {
        uint span = subStep * 2;
        MixLiquidRow(thread.xy * uint2(span,1) + uint2(DispatchOffsetX,0),span);
        return;
    }
    uint2 p = thread.xy * 2 + uint2(DispatchOffsetX, DispatchOffsetY);
    if (p.x + 1 >= Width || p.y + 1 >= Height) return;
    uint tl = FlattenCoordinate(p), tr = tl + 1, bl = tl + Width, br = bl + 1;
    GridCell a = WaterGrid[tl], b = WaterGrid[tr];
    GridCell c = WaterGrid[bl], d = WaterGrid[br];
    if (a.IsActive == 0 || b.IsActive == 0 || c.IsActive == 0 || d.IsActive == 0 ||
        (Materials[a.MaterialIndex].Flags & MaterialFlagLiquidConvection) == 0 ||
        a.MaterialIndex != b.MaterialIndex || a.MaterialIndex != c.MaterialIndex || a.MaterialIndex != d.MaterialIndex ||
        a.Mass < 0.99 || b.Mass < 0.99 || c.Mass < 0.99 || d.Mass < 0.99 ||
        abs(a.Mass - b.Mass) > 0.001 || abs(a.Mass - c.Mass) > 0.001 ||
        abs(a.Mass - d.Mass) > 0.001) return;

    if(((a.BodyId|b.BodyId|c.BodyId|d.BodyId) & VapourCushionMarker)!=0)return;
    if(!FilterPathAllows(tl,br,a.MaterialIndex,SimulationKindLiquid,Width))return;

    // Gameplay approximation: warmer water is buoyant. A rotation is allowed
    // only when its upward packet is hotter than its downward packet, so a
    // warm upper layer and an isothermal pool cannot start stirring themselves.
    float clockwise = c.Temperature - b.Temperature;
    float counterclockwise = d.Temperature - a.Temperature;
    float contrast = max(clockwise, counterclockwise);
    if (contrast <= 0.05) return;
    uint seed = tl ^ HashValue(FrameIndex + 7919u * SimulationPhase);
    // Resolve small temperature gradients before conduction erases them.
    // The cap bounds displacement to local cells per fixed 20 Hz tick.
    float mobility = LiquidMobility(Materials[a.MaterialIndex],
        (a.Temperature+b.Temperature+c.Temperature+d.Temperature)*.25);
    float chance = min(0.5, contrast * 0.5) * min(1.0,mobility);
    if (HashUnitFloat(seed) >= chance) return;
    bool cw = clockwise > counterclockwise;
    if (abs(clockwise - counterclockwise) < 0.001)
        cw = HashUnitFloat(seed ^ 0x9e3779b9u) < 0.5;
    if (cw)
    {
        MarkConvection(a, 4, 0); MarkConvection(b, 0, 4);
        MarkConvection(d, -4, 0); MarkConvection(c, 0, -4);
        WaterGrid[tr] = a; WaterGrid[br] = b;
        WaterGrid[bl] = d; WaterGrid[tl] = c;
    }
    else
    {
        MarkConvection(b, -4, 0); MarkConvection(a, 0, 4);
        MarkConvection(c, 4, 0); MarkConvection(d, 0, -4);
        WaterGrid[tl] = b; WaterGrid[bl] = a;
        WaterGrid[br] = c; WaterGrid[tr] = d;
    }
}

// Compile each independent task without the register/local-array cost of
// the other branches. CSMain retains the original runtime selection.
[numthreads(16,16,1)]
void CSMain(uint3 thread : SV_DispatchThreadID) { WaterStep(thread,GasSubStep,false); }
[numthreads(16,16,1)]
void CSQuench(uint3 thread : SV_DispatchThreadID) { WaterStep(thread,3,true); }
[numthreads(16,16,1)]
void CSColumns(uint3 thread : SV_DispatchThreadID) { WaterStep(thread,2,true); }
[numthreads(16,16,1)]
void CSConvect(uint3 thread : SV_DispatchThreadID) { WaterStep(thread,0,true); }
[numthreads(16,16,1)]
void CSMix(uint3 thread : SV_DispatchThreadID)
{
    uint span=GasSubStep*2;
    MixLiquidRow(thread.xy*uint2(span,1)+uint2(DispatchOffsetX,0),span);
}
