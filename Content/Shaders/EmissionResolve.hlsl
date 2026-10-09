#include "PhysicsShared.hlsli"
#include "CoalFlameShared.hlsli"
#include "OxidizerShared.hlsli"

cbuffer EmissionConstants : register(b0)
{
    uint EmissionWidth;
    uint EmissionHeight;
    uint EmissionMaterialCount;
    uint EmissionRequestCount;
};

StructuredBuffer<EmissionRequest> EmissionRequests : register(t0);
StructuredBuffer<uint> EmissionClaims : register(t1);
StructuredBuffer<MaterialProperties> Materials : register(t2);
RWStructuredBuffer<GridCell> Grid : register(u0);
RWStructuredBuffer<uint> CombustionSummary : register(u1);
RWStructuredBuffer<float4> ReactionPending : register(u2);

static const uint CombustionOccurred = 1u << 0;
static const uint TargetCellular = 1u << 2;
static const uint TargetGas = 1u << 4;

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= EmissionWidth || coordinate.y >= EmissionHeight)
    {
        return;
    }
    uint destinationIndex = coordinate.y * EmissionWidth + coordinate.x;
    uint requestIndex = EmissionClaims[destinationIndex];
    if (requestIndex == 0xffffffffu || requestIndex >= EmissionRequestCount)
    {
        return;
    }
    EmissionRequest request = EmissionRequests[requestIndex];
    if (request.DestinationIndex != destinationIndex || request.MaterialIndex >= EmissionMaterialCount)
    {
        return;
    }
    GridCell destination = Grid[destinationIndex];
    if (destination.IsActive != 0)
    {
        return;
    }
    MaterialProperties product = Materials[request.MaterialIndex];
    if (product.SimulationKind != SimulationKindGas)
    {
        return;
    }
    if(!FilterPathAllows(request.SourceIndex&0x1fffffffu,destinationIndex,request.MaterialIndex,SimulationKindGas,EmissionWidth))return;
    destination.MaterialIndex = request.MaterialIndex;
    destination.Mass = min(product.Density, max(0, request.Mass));
    destination.VelocityX = 0;
    destination.VelocityY = -8;
    destination.Pressure = 0;
    destination.IsActive = destination.Mass > 0 ? 1 : 0;
    destination.BodyId = (product.Flags & MaterialFlagFlame) != 0
        ? request.SourceIndex & (SelfOxidizingFlameMarker | ReactedFuelFlameMarker | FiniteHeatEmissionMarker) : 0;
    destination.RestFrames = 0;
    destination.Temperature = request.Temperature;
    destination.Lifetime = InitialMaterialLifetime(
        product,
        (request.SourceIndex & ~ReactedFuelFlameMarker) ^ destinationIndex ^ request.MaterialIndex) *
        ((product.Flags & MaterialFlagFlame) != 0 && request.FlameLifetimeMultiplier > 0 ? request.FlameLifetimeMultiplier : 1);
    Grid[destinationIndex] = destination;
    InterlockedOr(CombustionSummary[0], CombustionOccurred | TargetCellular | TargetGas);
}

// One owner per source; only winning, actually accepted products spend heat.
// Losing claims leave the energy for the carrier, including blocked outlets.
[numthreads(16,16,1)]
void CSConsumeHeat(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= EmissionWidth || id.y >= EmissionHeight) return;
    uint sourceIndex = id.y*EmissionWidth+id.x;
    uint count = EmissionWidth*EmissionHeight;
    float2 spent = 0;
    [unroll] for (uint slot=0;slot<3;slot++)
    {
        uint requestIndex = sourceIndex+slot*count;
        EmissionRequest request = EmissionRequests[requestIndex];
        if ((request.SourceIndex & FiniteHeatEmissionMarker)==0 || request.Mass<=0 ||
            request.MaterialIndex>=EmissionMaterialCount || request.DestinationIndex>=count) continue;
        if (EmissionClaims[request.DestinationIndex]!=requestIndex) continue;
        GridCell product = Grid[request.DestinationIndex];
        if (product.IsActive==0 || product.MaterialIndex!=request.MaterialIndex) continue;
        float capacity = product.Mass*Materials[product.MaterialIndex].HeatCapacity;
        spent += float2(capacity*(product.Temperature+273.15),capacity);
    }
    ReactionPending[sourceIndex].yz = max(0,ReactionPending[sourceIndex].yz-spent);
}
