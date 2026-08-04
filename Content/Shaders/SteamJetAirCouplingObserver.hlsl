#include "PhysicsShared.hlsli"

// This observer runs immediately before AirSimulation.CSInject. It reads the
// pending impulse before CSInject clears it, and independently reproduces only
// its local airLossProduct scan. No physical buffer is written or delayed.
StructuredBuffer<MaterialProperties> Materials : register(t0);
StructuredBuffer<GridCell> Grid : register(t1);
StructuredBuffer<GasAirImpulse> PendingGasImpulse : register(t2);
RWStructuredBuffer<SteamJetAirCouplingCell> Coupling : register(u0);

uint AirIndex(uint2 coordinate, uint airWidth)
{
    return coordinate.y * airWidth + coordinate.x;
}

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint airWidth = (Width + AirCellSize - 1) / AirCellSize;
    uint airHeight = (Height + AirCellSize - 1) / AirCellSize;
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= airWidth || coordinate.y >= airHeight) return;

    uint index = AirIndex(coordinate, airWidth);
    SteamJetAirCouplingCell result = (SteamJetAirCouplingCell)0;
    GasAirImpulse impulse = PendingGasImpulse[index];
    result.ImpulseX = impulse.X;
    result.ImpulseY = impulse.Y;
    result.AirLossProduct = 1.0;

    uint left = coordinate.x * AirCellSize;
    uint top = coordinate.y * AirCellSize;
    [unroll]
    for (uint offsetY = 0; offsetY < AirCellSize; offsetY++)
    {
        uint y = top + offsetY;
        if (y >= Height) continue;
        [unroll]
        for (uint offsetX = 0; offsetX < AirCellSize; offsetX++)
        {
            uint x = left + offsetX;
            if (x >= Width) continue;
            GridCell cell = Grid[FlattenCoordinate(uint2(x, y))];
            if (cell.IsActive == 0) continue;
            MaterialProperties material = Materials[cell.MaterialIndex];
            if (material.SimulationKind != SimulationKindGas) continue;
            result.GasCellCount++;
            result.AirLossProduct *= material.MotionAirLoss;
            // steam_jet contains WTRV only. A bit-mask retains exact fine-cell
            // membership for CPU aggregation into 20-cell height bands.
            if (material.GasDiffusion > 0.0)
            {
                uint bit = offsetY * AirCellSize + offsetX;
                result.SteamMask |= 1u << bit;
                result.SteamCellCount++;
            }
        }
    }
    Coupling[index] = result;
}
