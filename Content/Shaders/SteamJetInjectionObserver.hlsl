#include "PhysicsShared.hlsli"

// Diagnostic-only post-brush observer. It watches precisely the brush footprint
// and counts WTRV cells that changed from non-steam to steam. The brush and
// movement shaders neither read nor bind this output.
StructuredBuffer<GridCell> PreviousGrid : register(t0);
StructuredBuffer<GridCell> CurrentGrid : register(t1);
StructuredBuffer<BrushDrawCommand> Commands : register(t2);

struct SteamJetInjectionStatistics
{
    uint CreatedSteamCells;
};

RWStructuredBuffer<SteamJetInjectionStatistics> Statistics : register(u0);

struct SteamJetInjectionDistributionFrame
{
    uint CreatedSteamCells;
    int SumOffsetX;
    int SumOffsetXSquared;
    uint Ring0;
    uint Ring1;
    uint Ring2;
    uint Ring3;
    uint Ring4;
    uint Ring5OrMore;
};

RWStructuredBuffer<SteamJetInjectionDistributionFrame> Distribution : register(u1);

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= DispatchExtentX || dispatchThreadId.y >= DispatchExtentY)
    {
        return;
    }

    BrushDrawCommand command = Commands[0];
    int radius = int(ceil(command.Radius));
    int2 position = int2(command.X - radius, command.Y - radius) + int2(dispatchThreadId.xy);
    if (position.x < 0 || position.y < 0 || position.x >= int(Width) || position.y >= int(Height))
    {
        return;
    }

    uint index = FlattenCoordinate(uint2(position));
    GridCell before = PreviousGrid[index];
    GridCell after = CurrentGrid[index];
    if (after.IsActive != 0 && after.MaterialIndex == command.MaterialIndex &&
        (before.IsActive == 0 || before.MaterialIndex != command.MaterialIndex))
    {
        uint ignored;
        InterlockedAdd(Statistics[0].CreatedSteamCells, 1, ignored);
    }
}

[numthreads(16, 16, 1)]
void CSDistribution(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (FrameIndex >= 60 || dispatchThreadId.x >= DispatchExtentX || dispatchThreadId.y >= DispatchExtentY)
    {
        return;
    }

    BrushDrawCommand command = Commands[0];
    int radius = int(ceil(command.Radius));
    int2 position = int2(command.X - radius, command.Y - radius) + int2(dispatchThreadId.xy);
    if (position.x < 0 || position.y < 0 || position.x >= int(Width) || position.y >= int(Height))
    {
        return;
    }

    uint index = FlattenCoordinate(uint2(position));
    GridCell before = PreviousGrid[index];
    GridCell after = CurrentGrid[index];
    if (after.IsActive == 0 || after.MaterialIndex != command.MaterialIndex ||
        (before.IsActive != 0 && before.MaterialIndex == command.MaterialIndex))
    {
        return;
    }

    // steam_jet diagnostics always place the source on the world centre line.
    // Fixed inflow emits radius-zero commands, so command.X would be each
    // individual selected point rather than the axis of the source disk.
    int sourceX = int(Width) / 2;
    int offsetX = position.x - sourceX;
    uint ignored;
    int ignoredSigned;
    InterlockedAdd(Distribution[FrameIndex].CreatedSteamCells, 1, ignored);
    InterlockedAdd(Distribution[FrameIndex].SumOffsetX, offsetX, ignoredSigned);
    InterlockedAdd(Distribution[FrameIndex].SumOffsetXSquared, offsetX * offsetX, ignoredSigned);
    uint ring = min(uint(abs(offsetX) / 2), 5u);
    if (ring == 0) InterlockedAdd(Distribution[FrameIndex].Ring0, 1, ignored);
    else if (ring == 1) InterlockedAdd(Distribution[FrameIndex].Ring1, 1, ignored);
    else if (ring == 2) InterlockedAdd(Distribution[FrameIndex].Ring2, 1, ignored);
    else if (ring == 3) InterlockedAdd(Distribution[FrameIndex].Ring3, 1, ignored);
    else if (ring == 4) InterlockedAdd(Distribution[FrameIndex].Ring4, 1, ignored);
    else InterlockedAdd(Distribution[FrameIndex].Ring5OrMore, 1, ignored);
}
