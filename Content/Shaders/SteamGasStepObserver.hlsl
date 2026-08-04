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

struct SteamJetBlockingSubstepStatistics
{
    uint Rejected;
    uint Successful;
    uint FreedNextSubstep;
    uint SameMaterial;
    uint OtherMaterial;
    uint Solid;
};

struct SteamJetBlockingFrameStatistics
{
    uint SteamCellFrames;
    uint StalledWithWholeOffset;
};

struct SteamJetBlockingMarker
{
    uint TargetIndexPlusOne;
    uint StatisticsSlotPlusOne;
    uint OriginFrame;
    uint OriginSubstep;
};

// These UAVs are allocated only for PHYXEL_STEAM_JET_BLOCKING_TRACE=1 and
// are never bound by a physical solver. They intentionally have their own
// register range, separate from the older lateral observer's u0/u1.
RWStructuredBuffer<SteamJetBlockingSubstepStatistics> BlockingSubsteps : register(u2);
RWStructuredBuffer<SteamJetBlockingMarker> BlockingMarkers : register(u3);
RWStructuredBuffer<SteamJetBlockingFrameStatistics> BlockingFrames : register(u4);
RWStructuredBuffer<uint> BlockingMovedFrames : register(u5);

struct SteamJetDiagonalIntentStatistics
{
    uint SteamCells;
    uint DiagonalIntents;
    uint ValidIntentTargets;
    uint LateralTargetOccupied;
    uint DiagonalTargetOccupied;
    uint LeftOccupied;
    uint RightOccupied;
    uint UpOccupied;
    uint DownOccupied;
    uint UpLeftOccupied;
    uint UpRightOccupied;
    uint DownLeftOccupied;
    uint DownRightOccupied;
};

// Allocated only when PHYXEL_STEAM_JET_DIAGONAL_TRACE=1.  This output is
// sampled after phase 89 has formed OffsetX/Y and before either axis consumes
// those offsets, so it observes intent rather than a later retained state.
RWStructuredBuffer<SteamJetDiagonalIntentStatistics> DiagonalIntents : register(u6);

int GetSteamJetBlockingGroup(uint2 coordinate)
{
    int heightAboveSource = int(Height) - 30 - int(coordinate.y);
    if (heightAboveSource >= 0 && heightAboveSource < 80) return 0;
    if (heightAboveSource >= 200 && heightAboveSource < 280) return 1;
    return -1;
}

bool IsLaterGasSubstep(SteamJetBlockingMarker marker)
{
    return marker.TargetIndexPlusOne != 0 &&
        (FrameIndex > marker.OriginFrame ||
            (FrameIndex == marker.OriginFrame && GasSubStep > marker.OriginSubstep));
}

[numthreads(16, 16, 1)]
void CSBlocking(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= Width || coordinate.y >= Height) return;
    uint index = FlattenCoordinate(coordinate);

    // Resolve markers first. A marker is checked only after a strictly later
    // gas substep, so a target freed in the following checkerboard turn is not
    // mistaken for a contemporaneous swap.
    [unroll]
    for (uint phaseSlot = 0; phaseSlot < 2; phaseSlot++)
    {
        uint markerIndex = index * 2 + phaseSlot;
        SteamJetBlockingMarker marker = BlockingMarkers[markerIndex];
        if (!IsLaterGasSubstep(marker)) continue;
        if (CurrentGrid[marker.TargetIndexPlusOne - 1].IsActive == 0)
        {
            uint ignored;
            InterlockedAdd(BlockingSubsteps[marker.StatisticsSlotPlusOne - 1].FreedNextSubstep, 1, ignored);
        }
        BlockingMarkers[markerIndex].TargetIndexPlusOne = 0;
        BlockingMarkers[markerIndex].StatisticsSlotPlusOne = 0;
        BlockingMarkers[markerIndex].OriginFrame = 0;
        BlockingMarkers[markerIndex].OriginSubstep = 0;
    }

    // Gas horizontal pair phases are the only paths measured here. The current
    // source location is retained on a rejected intent; its whole OffsetX
    // identifies the intended destination without touching solver state.
    if ((SimulationPhase != 82 && SimulationPhase != 83) || GasSubStep == 0) return;
    int group = GetSteamJetBlockingGroup(coordinate);
    if (group < 0) return;
    uint statisticsSlot = uint(group) * 8 + GasSubStep - 1;
    GridCell beforeCell = PreviousGrid[index];
    GridCell currentCell = CurrentGrid[index];
    GasMotionState beforeMotion = PreviousMotion[index];

    if (IsSteamPuffCell(beforeCell) && IsSteamPuffCell(currentCell))
    {
        int targetX = beforeMotion.OffsetX <= -1.0 ? int(coordinate.x) - 1 :
            beforeMotion.OffsetX >= 1.0 ? int(coordinate.x) + 1 : -1;
        if (targetX >= 0 && targetX < int(Width))
        {
            uint targetIndex = FlattenCoordinate(uint2(uint(targetX), coordinate.y));
            GridCell target = PreviousGrid[targetIndex];
            if (target.IsActive != 0)
            {
                uint ignored;
                InterlockedAdd(BlockingSubsteps[statisticsSlot].Rejected, 1, ignored);
                if (target.MaterialIndex == beforeCell.MaterialIndex)
                    InterlockedAdd(BlockingSubsteps[statisticsSlot].SameMaterial, 1, ignored);
                else if (Materials[target.MaterialIndex].SimulationKind == SimulationKindSolid)
                    InterlockedAdd(BlockingSubsteps[statisticsSlot].Solid, 1, ignored);
                else
                    InterlockedAdd(BlockingSubsteps[statisticsSlot].OtherMaterial, 1, ignored);

                uint phaseSlot = SimulationPhase - 82;
                uint markerIndex = index * 2 + phaseSlot;
                BlockingMarkers[markerIndex].TargetIndexPlusOne = targetIndex + 1;
                BlockingMarkers[markerIndex].StatisticsSlotPlusOne = statisticsSlot + 1;
                BlockingMarkers[markerIndex].OriginFrame = FrameIndex;
                BlockingMarkers[markerIndex].OriginSubstep = GasSubStep;
            }
        }
    }

    // Attribute a successful lateral move to its pre-step source band. The
    // same exact state equality used by the existing step observer avoids
    // inferring movement from merely similar neighbouring cells.
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
        InterlockedAdd(BlockingSubsteps[statisticsSlot].Successful, 1, ignored);
        BlockingMovedFrames[index] = FrameIndex + 1;
        return;
    }
}

[numthreads(16, 16, 1)]
void CSBlockingFrame(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= Width || coordinate.y >= Height) return;
    int group = GetSteamJetBlockingGroup(coordinate);
    if (group < 0) return;
    uint index = FlattenCoordinate(coordinate);
    GridCell cell = CurrentGrid[index];
    if (!IsSteamPuffCell(cell)) return;

    uint ignored;
    InterlockedAdd(BlockingFrames[group].SteamCellFrames, 1, ignored);
    GasMotionState motion = CurrentMotion[index];
    if (BlockingMovedFrames[index] != FrameIndex + 1 &&
        (abs(motion.OffsetX) >= 1.0 || abs(motion.OffsetY) >= 1.0))
    {
        InterlockedAdd(BlockingFrames[group].StalledWithWholeOffset, 1, ignored);
    }
}

void RecordOccupiedNeighbour(uint group, uint fieldOffset, bool occupied)
{
    if (!occupied) return;
    uint ignored;
    if (fieldOffset == 0) InterlockedAdd(DiagonalIntents[group].LeftOccupied, 1, ignored);
    else if (fieldOffset == 1) InterlockedAdd(DiagonalIntents[group].RightOccupied, 1, ignored);
    else if (fieldOffset == 2) InterlockedAdd(DiagonalIntents[group].UpOccupied, 1, ignored);
    else if (fieldOffset == 3) InterlockedAdd(DiagonalIntents[group].DownOccupied, 1, ignored);
    else if (fieldOffset == 4) InterlockedAdd(DiagonalIntents[group].UpLeftOccupied, 1, ignored);
    else if (fieldOffset == 5) InterlockedAdd(DiagonalIntents[group].UpRightOccupied, 1, ignored);
    else if (fieldOffset == 6) InterlockedAdd(DiagonalIntents[group].DownLeftOccupied, 1, ignored);
    else InterlockedAdd(DiagonalIntents[group].DownRightOccupied, 1, ignored);
}

[numthreads(16, 16, 1)]
void CSDiagonalIntent(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= Width || coordinate.y >= Height) return;
    int group = GetSteamJetBlockingGroup(coordinate);
    if (group < 0) return;
    uint index = FlattenCoordinate(coordinate);
    if (!IsSteamPuffCell(CurrentGrid[index])) return;

    uint ignored;
    InterlockedAdd(DiagonalIntents[group].SteamCells, 1, ignored);
    [unroll]
    for (int neighbour = 0; neighbour < 8; neighbour++)
    {
        int2 delta = int2(1, 1);
        if (neighbour == 0) delta = int2(-1, 0);
        else if (neighbour == 1) delta = int2(1, 0);
        else if (neighbour == 2) delta = int2(0, -1);
        else if (neighbour == 3) delta = int2(0, 1);
        else if (neighbour == 4) delta = int2(-1, -1);
        else if (neighbour == 5) delta = int2(1, -1);
        else if (neighbour == 6) delta = int2(-1, 1);
        int2 target = int2(coordinate) + delta;
        bool valid = target.x >= 0 && target.y >= 0 && target.x < int(Width) && target.y < int(Height);
        bool occupied = valid && CurrentGrid[FlattenCoordinate(uint2(target))].IsActive != 0;
        RecordOccupiedNeighbour(group, neighbour, occupied);
    }

    GasMotionState motion = CurrentMotion[index];
    // Offset, not raw velocity, is the discrete solver's actual step intent:
    // it reaches +/-1 only once a full fine-cell move is ready to consume.
    if (abs(motion.OffsetX) < 1.0 || abs(motion.OffsetY) < 1.0) return;
    int stepX = motion.OffsetX > 0.0 ? 1 : -1;
    int stepY = motion.OffsetY > 0.0 ? 1 : -1;
    int2 lateralTarget = int2(coordinate) + int2(stepX, 0);
    int2 diagonalTarget = int2(coordinate) + int2(stepX, stepY);
    bool targetsValid = lateralTarget.x >= 0 && lateralTarget.x < int(Width) &&
        diagonalTarget.x >= 0 && diagonalTarget.x < int(Width) &&
        diagonalTarget.y >= 0 && diagonalTarget.y < int(Height);
    InterlockedAdd(DiagonalIntents[group].DiagonalIntents, 1, ignored);
    if (!targetsValid) return;
    InterlockedAdd(DiagonalIntents[group].ValidIntentTargets, 1, ignored);
    if (CurrentGrid[FlattenCoordinate(uint2(lateralTarget))].IsActive != 0)
        InterlockedAdd(DiagonalIntents[group].LateralTargetOccupied, 1, ignored);
    if (CurrentGrid[FlattenCoordinate(uint2(diagonalTarget))].IsActive != 0)
        InterlockedAdd(DiagonalIntents[group].DiagonalTargetOccupied, 1, ignored);
}
