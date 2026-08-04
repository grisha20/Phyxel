#include "PhysicsShared.hlsli"

// Post-pass observer for steam_puff. It shares no execution path with
// CellularAutomataSolver, so tracing cannot alter physical-gas compilation.
StructuredBuffer<GridCell> PreviousGrid : register(t0);
StructuredBuffer<GridCell> CurrentGrid : register(t1);
StructuredBuffer<MaterialProperties> Materials : register(t2);
StructuredBuffer<GasMotionState> PreviousMotion : register(t3);
StructuredBuffer<GasMotionState> CurrentMotion : register(t4);

struct SteamGasStepStatistics
{
    uint UpwardSteps;
    uint DownwardSteps;
    uint NoYSteps;
    uint LeftSteps;
    uint RightSteps;
    uint NoXSteps;
};

RWStructuredBuffer<SteamGasStepStatistics> Statistics : register(u0);

bool IsSteamPuffCell(GridCell cell)
{
    return cell.IsActive != 0 &&
        Materials[cell.MaterialIndex].SimulationKind == SimulationKindGas &&
        Materials[cell.MaterialIndex].GasDiffusion > 0.0;
}

bool MatchesMovedState(GasMotionState before, GasMotionState after, int stepX, int stepY)
{
    return before.VelocityX == after.VelocityX && before.VelocityY == after.VelocityY &&
        before.OffsetX - float(stepX) == after.OffsetX &&
        before.OffsetY - float(stepY) == after.OffsetY;
}

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= Width || coordinate.y >= Height) return;

    uint index = FlattenCoordinate(coordinate);
    GridCell current = CurrentGrid[index];
    if (!IsSteamPuffCell(current)) return;
    GasMotionState currentMotion = CurrentMotion[index];

    [unroll]
    for (int dy = -1; dy <= 1; dy++)
    {
        [unroll]
        for (int dx = -1; dx <= 1; dx++)
        {
            if (dx == 0 && dy == 0) continue;
            int sourceX = int(coordinate.x) - dx;
            int sourceY = int(coordinate.y) - dy;
            if (sourceX < 0 || sourceY < 0 || sourceX >= int(Width) || sourceY >= int(Height)) continue;

            uint sourceIndex = FlattenCoordinate(uint2(uint(sourceX), uint(sourceY)));
            if (!IsSteamPuffCell(PreviousGrid[sourceIndex]) ||
                !MatchesMovedState(PreviousMotion[sourceIndex], currentMotion, dx, dy)) continue;

            uint ignored;
            if (dy < 0) InterlockedAdd(Statistics[0].UpwardSteps, 1, ignored);
            else if (dy > 0) InterlockedAdd(Statistics[0].DownwardSteps, 1, ignored);
            else InterlockedAdd(Statistics[0].NoYSteps, 1, ignored);
            if (dx < 0) InterlockedAdd(Statistics[0].LeftSteps, 1, ignored);
            else if (dx > 0) InterlockedAdd(Statistics[0].RightSteps, 1, ignored);
            else InterlockedAdd(Statistics[0].NoXSteps, 1, ignored);
            return;
        }
    }
}
