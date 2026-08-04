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
