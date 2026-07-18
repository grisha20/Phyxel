#include "PhysicsShared.hlsli"

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
    destination.MaterialIndex = request.MaterialIndex;
    destination.Mass = min(product.Density, max(0, request.Mass));
    destination.VelocityX = 0;
    destination.VelocityY = -8;
    destination.Pressure = 0;
    destination.IsActive = destination.Mass > 0 ? 1 : 0;
    destination.BodyId = 0;
    destination.RestFrames = 0;
    destination.Temperature = request.Temperature;
    destination.Lifetime = InitialMaterialLifetime(
        product,
        request.SourceIndex ^ destinationIndex ^ request.MaterialIndex);
    Grid[destinationIndex] = destination;
    InterlockedOr(CombustionSummary[0], CombustionOccurred | TargetCellular | TargetGas);
}
