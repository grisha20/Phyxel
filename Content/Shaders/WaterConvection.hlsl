#include "PhysicsShared.hlsli"

RWStructuredBuffer<GridCell> WaterGrid : register(u0);
StructuredBuffer<MaterialProperties> Materials : register(t0);
RWStructuredBuffer<uint> BoilingSummary : register(u1);
RWStructuredBuffer<uint> BoilingCellMaterials : register(u2);

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
            abs(cell.Mass - WaterGrid[FlattenCoordinate(p)].Mass) > .001) return;
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

[numthreads(16, 16, 1)]
void CSMain(uint3 thread : SV_DispatchThreadID)
{
    if(GasSubStep==2)
    {
        // One owner per entire column: lift a resolved small drop without
        // racing a neighbour or overwriting the parcels above it.
        uint x=thread.x;
        if(thread.y!=0 || x>=Width) return;
        [loop] for(uint y=2;y+1<Height;y++)
        {
            uint bottom=y*Width+x;
            GridCell liquid=WaterGrid[bottom],wall=WaterGrid[bottom+Width];
            MaterialProperties m=Materials[liquid.MaterialIndex],s=Materials[wall.MaterialIndex];
            if(liquid.IsActive!=0 && (liquid.BodyId & VapourCushionMarker)!=0 && y+2<Height)
            {
                GridCell support=WaterGrid[bottom+2*Width];
                if(wall.IsActive!=0 || support.IsActive==0 || Materials[support.MaterialIndex].SimulationKind!=SimulationKindSolid ||
                    support.Temperature<=m.TransitionAboveTemperature+SurfaceFilmTemperatureOffset)
                {liquid.BodyId &= ~VapourCushionMarker;WaterGrid[bottom]=liquid;}
            }
            if(liquid.IsActive==0 || (m.Flags & MaterialFlagSurfaceBoiling)==0 || wall.IsActive==0 ||
                s.SimulationKind!=SimulationKindSolid || s.ThermalConductivity<=.5 ||
                wall.Temperature<=m.TransitionAboveTemperature+SurfaceFilmTemperatureOffset ||
                liquid.Temperature<m.TransitionAboveTemperature-.001 || liquid.Lifetime<=0) continue;
            uint top=y,depth=1;
            [loop] while(top>0 && depth<=SurfaceFilmMaximumDepth)
            {
                GridCell above=WaterGrid[(top-1)*Width+x];
                if(above.IsActive==0) break;
                if(above.MaterialIndex!=liquid.MaterialIndex) {depth=SurfaceFilmMaximumDepth+1;break;}
                top--;depth++;
            }
            if(top==0 || depth>SurfaceFilmMaximumDepth) continue;
            bool allowed=true;
            [loop] for(uint row=top;row<=y;row++)
                if(!FilterAllows(row*Width+x,liquid.MaterialIndex,SimulationKindLiquid) ||
                   !FilterAllows((row-1)*Width+x,liquid.MaterialIndex,SimulationKindLiquid))allowed=false;
            if(!allowed)continue;
            [loop] for(uint row=top;row<=y;row++)
            {
                GridCell lifted=WaterGrid[row*Width+x];
                lifted.VelocityY=-20;lifted.RestFrames=0;lifted.Pressure=0;lifted.BodyId|=VapourCushionMarker;
                WaterGrid[(row-1)*Width+x]=lifted;
                BoilingCellMaterials[(row-1)*Width+x]=lifted.MaterialIndex;
            }
            WaterGrid[bottom]=CreateEmptyCell();
            BoilingCellMaterials[bottom]=0;
            InterlockedOr(BoilingSummary[0],PhaseSummaryPhaseOccurred | PhaseSummaryTargetCellular | PhaseSummaryTouchesLiquid);
        }
        return;
    }
    if (GasSubStep > 2)
    {
        uint span = GasSubStep * 2;
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
