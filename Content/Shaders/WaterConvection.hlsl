#include "PhysicsShared.hlsli"

RWStructuredBuffer<GridCell> WaterGrid : register(u0);

// Closed, disjoint 2x2 rotations provide a local upward path and an equal
// downward return. No empty cell, surface, wall or other material can move.
// CommandCount is the core water runtime index; FrameIndex is a thermal tick.
void MarkConvection(inout GridCell cell, float vx, float vy)
{
    cell.RestFrames = 0;
    cell.VelocityX = vx;
    cell.VelocityY = vy;
}

[numthreads(16, 16, 1)]
void CSMain(uint3 thread : SV_DispatchThreadID)
{
    uint2 p = thread.xy * 2 + uint2(DispatchOffsetX, DispatchOffsetY);
    if (p.x + 1 >= Width || p.y + 1 >= Height) return;
    uint tl = FlattenCoordinate(p), tr = tl + 1, bl = tl + Width, br = bl + 1;
    GridCell a = WaterGrid[tl], b = WaterGrid[tr];
    GridCell c = WaterGrid[bl], d = WaterGrid[br];
    if (a.IsActive == 0 || b.IsActive == 0 || c.IsActive == 0 || d.IsActive == 0 ||
        a.MaterialIndex != CommandCount || b.MaterialIndex != CommandCount ||
        c.MaterialIndex != CommandCount || d.MaterialIndex != CommandCount ||
        a.Mass < 0.99 || b.Mass < 0.99 || c.Mass < 0.99 || d.Mass < 0.99 ||
        abs(a.Mass - b.Mass) > 0.001 || abs(a.Mass - c.Mass) > 0.001 ||
        abs(a.Mass - d.Mass) > 0.001) return;

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
    float chance = min(0.5, contrast * 0.5);
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
