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

struct SteamJetLateralBandStatistics
{
    uint LeftSteps;
    uint RightSteps;
    uint RejectedLeft;
    uint RejectedRight;
};

RWStructuredBuffer<SteamGasStepStatistics> Statistics : register(u0);
// Bound only when PHYXEL_STEAM_JET_LATERAL_TRACE=1. The observer is entirely
// post-phase: no physical solver reads this buffer.
RWStructuredBuffer<SteamJetLateralBandStatistics> LateralBands : register(u1);

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

[numthreads(16, 16, 1)]
void CSLateralBands(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= Width || coordinate.y >= Height) return;
    // Only phases 82/83 execute ResolveGasHorizontalPair. In particular, do
    // not count a retained OffsetX again after a vertical or collision phase.
    if (SimulationPhase != 82 && SimulationPhase != 83) return;

    // RunSteamJetDiagnostics places its source at Height-30. A 20-cell band
    // is indexed by distance upward from that source; bands below it are not
    // part of the measured jet.
    int heightAboveSource = int(Height) - 30 - int(coordinate.y);
    if (heightAboveSource < 0) return;
    uint band = uint(heightAboveSource / 20);

    uint index = FlattenCoordinate(coordinate);
    GridCell beforeCell = PreviousGrid[index];
    GridCell currentCell = CurrentGrid[index];
    GasMotionState beforeMotion = PreviousMotion[index];

    // A retained whole-cell offset after a horizontal pair phase is the
    // observable signature of an attempted side step whose target was already
    // occupied. Count it only if the pre-phase destination was active.
    if (IsSteamPuffCell(beforeCell) && IsSteamPuffCell(currentCell))
    {
        if (beforeMotion.OffsetX <= -1.0 && coordinate.x > 0 &&
            PreviousGrid[index - 1].IsActive != 0)
        {
            uint ignored;
            InterlockedAdd(LateralBands[band].RejectedLeft, 1, ignored);
        }
        if (beforeMotion.OffsetX >= 1.0 && coordinate.x + 1 < Width &&
            PreviousGrid[index + 1].IsActive != 0)
        {
            uint ignored;
            InterlockedAdd(LateralBands[band].RejectedRight, 1, ignored);
        }
    }

    if (!IsSteamPuffCell(currentCell)) return;
    GasMotionState currentMotion = CurrentMotion[index];
    [unroll]
    for (int dx = -1; dx <= 1; dx += 2)
    {
        int sourceX = int(coordinate.x) - dx;
        if (sourceX < 0 || sourceX >= int(Width)) continue;
        uint sourceIndex = FlattenCoordinate(uint2(uint(sourceX), coordinate.y));
        if (!IsSteamPuffCell(PreviousGrid[sourceIndex]) ||
            !MatchesMovedState(PreviousMotion[sourceIndex], currentMotion, dx, 0)) continue;
        uint ignored;
        if (dx < 0) InterlockedAdd(LateralBands[band].LeftSteps, 1, ignored);
        else InterlockedAdd(LateralBands[band].RightSteps, 1, ignored);
        return;
    }
}
