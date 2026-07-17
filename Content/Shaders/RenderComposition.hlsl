#include "PhysicsShared.hlsli"

StructuredBuffer<GridCell> Grid : register(t0);
StructuredBuffer<MaterialProperties> Materials : register(t1);
StructuredBuffer<uint> WaterActivity : register(t2);
StructuredBuffer<uint> WaterDiagnostics : register(t3);
RWTexture2D<unorm float4> OutputTexture : register(u0);
RWStructuredBuffer<SimulationStatistics> Statistics : register(u1);

float4 MaterialColor(uint materialId)
{
    MaterialProperties material = Materials[materialId];
    return float4(material.ColorR, material.ColorG, material.ColorB, material.ColorA);
}

float LiquidCoverage(uint2 coordinate, out float3 liquidColor)
{
    float coverage = 0;
    float weight = 0;
    float3 weightedColor = 0;
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            int2 sample = int2(coordinate) + int2(x, y);
            if (sample.x < 0 || sample.y < 0 || sample.x >= int(Width) || sample.y >= int(Height))
            {
                continue;
            }
            float sampleWeight = x == 0 && y == 0 ? 4 : x == 0 || y == 0 ? 2 : 1;
            GridCell cell = Grid[FlattenCoordinate(uint2(sample))];
            if (cell.IsActive != 0 &&
                Materials[cell.MaterialIndex].SimulationKind == SimulationKindLiquid)
            {
                float amount = saturate(cell.Mass) * sampleWeight;
                coverage += amount;
                weightedColor += MaterialColor(cell.MaterialIndex).rgb * amount;
            }
            weight += sampleWeight;
        }
    }
    liquidColor = coverage > 0 ? weightedColor / coverage : 0;
    return coverage / max(weight, 1);
}

void Collect(GridCell cell)
{
    if (cell.IsActive == 0)
    {
        return;
    }
    uint ignored;
    uint kind = Materials[cell.MaterialIndex].SimulationKind;
    InterlockedAdd(Statistics[0].ActiveCells, 1, ignored);
    if (kind == SimulationKindSolid) InterlockedAdd(Statistics[0].SolidCells, 1, ignored);
    if (kind == SimulationKindLiquid) InterlockedAdd(Statistics[0].LiquidCells, 1, ignored);
    if (kind == SimulationKindGranular) InterlockedAdd(Statistics[0].GranularCells, 1, ignored);
    if (kind == SimulationKindGas) InterlockedAdd(Statistics[0].GasCells, 1, ignored);
    bool restingSolid = kind == SimulationKindSolid && (SolidGravity == 0 || cell.RestFrames >= 2);
    uint cellularRestThreshold = kind == SimulationKindGranular ? 30 : 60;
    bool restingCellular = kind != SimulationKindSolid &&
        (!IsCellularMaterial(kind) || cell.RestFrames >= cellularRestThreshold);
    if (restingSolid || restingCellular)
    {
        InterlockedAdd(Statistics[0].RestingCells, 1, ignored);
    }
    else
    {
        InterlockedAdd(Statistics[0].MovingCells, 1, ignored);
        if (kind == SimulationKindSolid)
        {
            InterlockedAdd(Statistics[0].MovingSolidCells, 1, ignored);
        }
    }
}

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= Width || coordinate.y >= Height)
    {
        return;
    }

    GridCell cell = Grid[FlattenCoordinate(coordinate)];
    float4 color = float4(0.035, 0.041, 0.047, 1);
    if (cell.IsActive != 0)
    {
        color = MaterialColor(cell.MaterialIndex);
        uint kind = Materials[cell.MaterialIndex].SimulationKind;
        if (IsFluidMaterial(kind))
        {
            float fill = kind == SimulationKindLiquid ? 1 : sqrt(saturate(cell.Mass));
            color.rgb = lerp(float3(0.035, 0.041, 0.047), color.rgb, fill);
        }
    }
    else
    {
        float3 liquidColor;
        float coverage = LiquidCoverage(coordinate, liquidColor);
        if (coverage > 0.02)
        {
            color.rgb = lerp(color.rgb, liquidColor, saturate(coverage * 2.5));
        }
    }
    OutputTexture[coordinate] = color;
    if (SimulationPhase != 0)
    {
        Collect(cell);
        if (coordinate.x == 0 && coordinate.y == 0)
        {
            Statistics[0].FrameIndex = FrameIndex;
            Statistics[0].PressureMoves = WaterActivity[Width * 17];
            uint blockerCount = ((Width + 31) / 32) * Height;
            Statistics[0].FarColumnMoves = WaterDiagnostics[blockerCount];
            Statistics[0].PressurePlans = WaterDiagnostics[blockerCount + 1];
        }
    }
}
