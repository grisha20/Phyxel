#include "PhysicsShared.hlsli"

StructuredBuffer<GridCell> Grid : register(t0);
StructuredBuffer<MaterialProperties> Materials : register(t1);
StructuredBuffer<uint> WaterActivity : register(t2);
RWTexture2D<unorm float4> OutputTexture : register(u0);
RWStructuredBuffer<SimulationStatistics> Statistics : register(u1);

float4 MaterialColor(uint materialId)
{
    if (materialId == 1) return float4(0.855, 0.722, 0.361, 1);
    if (materialId == 2) return float4(0.169, 0.518, 0.812, 1);
    if (materialId == 3) return float4(0.557, 0.612, 0.651, 1);
    if (materialId == 4) return float4(0.361, 0.376, 0.396, 1);
    if (materialId == 6) return float4(0.608, 0.769, 0.824, 0.58);
    if (materialId == 7) return float4(0.322, 0.357, 0.388, 1);
    return float4(0.035, 0.041, 0.047, 1);
}

float WaterCoverage(uint2 coordinate)
{
    float coverage = 0;
    float weight = 0;
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
            coverage += cell.IsActive != 0 && cell.MaterialId == 2 ? saturate(cell.Mass) * sampleWeight : 0;
            weight += sampleWeight;
        }
    }
    return coverage / max(weight, 1);
}

void Collect(GridCell cell)
{
    if (cell.IsActive == 0)
    {
        return;
    }
    uint ignored;
    uint kind = Materials[cell.MaterialId].SimulationKind;
    InterlockedAdd(Statistics[0].ActiveCells, 1, ignored);
    if (kind == 2) InterlockedAdd(Statistics[0].SolidCells, 1, ignored);
    if (cell.MaterialId == 2) InterlockedAdd(Statistics[0].WaterCells, 1, ignored);
    if (cell.MaterialId == 1) InterlockedAdd(Statistics[0].SandCells, 1, ignored);
    if (cell.MaterialId == 6) InterlockedAdd(Statistics[0].GasCells, 1, ignored);
    bool restingSolid = kind == 2 && (SolidGravity == 0 || cell.RestFrames >= 2);
    uint cellularRestThreshold = kind == 1 ? 30 : 60;
    bool restingCellular = kind != 2 &&
        (!IsCellularMaterial(kind) || cell.RestFrames >= cellularRestThreshold);
    if (restingSolid || restingCellular)
    {
        InterlockedAdd(Statistics[0].RestingCells, 1, ignored);
    }
    else
    {
        InterlockedAdd(Statistics[0].MovingCells, 1, ignored);
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
        color = MaterialColor(cell.MaterialId);
        if (IsFluidMaterial(Materials[cell.MaterialId].SimulationKind))
        {
            float fill = cell.MaterialId == 2 ? 1 : sqrt(saturate(cell.Mass));
            color.rgb = lerp(float3(0.035, 0.041, 0.047), color.rgb, fill);
        }
    }
    else
    {
        float coverage = WaterCoverage(coordinate);
        if (coverage > 0.02)
        {
            color.rgb = lerp(color.rgb, MaterialColor(2).rgb, saturate(coverage * 2.5));
        }
    }
    OutputTexture[coordinate] = color;
    if (SimulationPhase != 0)
    {
        Collect(cell);
        if (coordinate.x == 0 && coordinate.y == 0)
        {
            Statistics[0].FrameIndex = FrameIndex;
            Statistics[0].PressureMoves = WaterActivity[Width * 9];
            Statistics[0].Reserved0 = 0;
            Statistics[0].Reserved1 = 0;
            Statistics[0].Reserved2 = 0;
        }
    }
}
