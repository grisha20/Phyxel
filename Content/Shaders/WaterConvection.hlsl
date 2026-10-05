#include "PhysicsShared.hlsli"

RWStructuredBuffer<GridCell> WaterGrid : register(u0);
StructuredBuffer<MaterialProperties> Materials : register(t0);

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
