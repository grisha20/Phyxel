#include "PhysicsShared.hlsli"

// This observer is dispatched immediately before phase 89. It duplicates the
// vertical expression in IntegrateGasMotion from the same pre-integration
// buffers, but writes only a diagnostic buffer. It is intentionally a
// separate shader so the cellular solver's compiled physical path is intact.
StructuredBuffer<MaterialProperties> Materials : register(t0);
StructuredBuffer<AirCell> Air : register(t1);
StructuredBuffer<GridCell> Grid : register(t2);
StructuredBuffer<GasMotionState> GasMotion : register(t3);
RWStructuredBuffer<SteamJetGasMotionContribution> Contributions : register(u0);

static const float GasMaximumSpeed = 7.2;
static const float GasAirVelocityScale = 1.0;
static const uint ContributionFlagSteam = 1u;
static const uint ContributionFlagSurfaceCarrier = 2u;

float2 SampleAirDrift(uint2 coordinate)
{
    uint airWidth = (Width + AirCellSize - 1) / AirCellSize;
    uint airHeight = (Height + AirCellSize - 1) / AirCellSize;
    uint2 airCoordinate = min(
        coordinate / AirCellSize,
        uint2(airWidth - 1, airHeight - 1));
    AirCell air = Air[airCoordinate.y * airWidth + airCoordinate.x];
    return float2(air.VelocityX, air.VelocityY);
}

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= Width || coordinate.y >= Height) return;

    uint index = FlattenCoordinate(coordinate);
    SteamJetGasMotionContribution result = (SteamJetGasMotionContribution)0;
    GridCell cell = Grid[index];
    if (cell.IsActive == 0) { Contributions[index] = result; return; }

    MaterialProperties material = Materials[cell.MaterialIndex];
    // WTRV is the only diffusing gas in the steam_jet scene. This observer is
    // intentionally scoped to that diagnostic and does not need a material ID.
    if (material.SimulationKind != SimulationKindGas || material.GasDiffusion <= 0.0)
    {
        Contributions[index] = result;
        return;
    }

    GasMotionState state = GasMotion[index];
    result.Flags = ContributionFlagSteam;
    result.PreviousVelocityY = state.VelocityY;

    bool surfaceCarrierMarker = abs(state.OffsetY - 0.5) < 0.001 &&
        abs(state.VelocityX) >= material.MotionAdvection;
    bool carriesAlongSurface = coordinate.y > 0 &&
        (state.OffsetY <= -1.0 || surfaceCarrierMarker);
    if (carriesAlongSurface)
    {
        GridCell aboveCell = Grid[index - Width];
        carriesAlongSurface = aboveCell.IsActive != 0 &&
            Materials[aboveCell.MaterialIndex].SimulationKind == SimulationKindSolid;
    }
    if (carriesAlongSurface && abs(state.VelocityX) > 0.0001)
    {
        result.Flags |= ContributionFlagSurfaceCarrier;
        result.IntegratedVelocityY = 0.0;
        Contributions[index] = result;
        return;
    }

    float2 drift = SampleAirDrift(coordinate);
    uint diffusionSeed = index ^ (FrameIndex * 0x9e3779b9u);
    float diffusionY = (HashUnitFloat(diffusionSeed ^ 0x85ebca6bu) * 2.0 - 1.0) * material.GasDiffusion;
    result.RetainedVelocityY = state.VelocityY * material.MotionLoss;
    result.AirAdvectionY = drift.y * material.MotionAdvection * GasAirVelocityScale;
    result.BuoyancyY = material.GasBuoyancy;
    result.DiffusionY = diffusionY;
    result.UnclampedVelocityY = result.RetainedVelocityY + result.AirAdvectionY +
        result.BuoyancyY + result.DiffusionY;
    result.IntegratedVelocityY = clamp(result.UnclampedVelocityY, -GasMaximumSpeed, GasMaximumSpeed);
    Contributions[index] = result;
}
