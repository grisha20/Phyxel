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

bool IsFlameCell(GridCell cell)
{
    return cell.IsActive != 0 && cell.Lifetime > 0 &&
        (Materials[cell.MaterialIndex].Flags & MaterialFlagFlame) != 0;
}

bool IsCombustibleCell(GridCell cell)
{
    if (cell.IsActive == 0 || cell.MaterialIndex >= 256)
    {
        return false;
    }
    MaterialProperties material = Materials[cell.MaterialIndex];
    if (material.SimulationKind != SimulationKindSolid ||
        material.BurnedIntoMaterialIndex == 0xffffffffu ||
        cell.Temperature <= material.IgnitionTemperature)
    {
        return false;
    }
    MaterialProperties residue = Materials[material.BurnedIntoMaterialIndex];
    return cell.Mass > residue.Density + 0.0001;
}

float3 FlameColor(GridCell cell, uint seed)
{
    MaterialProperties material = Materials[cell.MaterialIndex];
    float life = material.MaximumLifetime > 0
        ? saturate(cell.Lifetime / material.MaximumLifetime)
        : 1;
    float flicker = 0.78 + 0.22 * HashUnitFloat(seed + FrameIndex * 17);
    float3 orange = float3(1.0, 0.10, 0.005);
    float3 yellow = float3(1.0, 0.72, 0.06);
    return lerp(orange, yellow, saturate(life * 1.25)) * flicker;
}

float3 FlameSourceColor(GridCell cell, uint seed)
{
    if (IsFlameCell(cell))
    {
        return FlameColor(cell, seed);
    }
    MaterialProperties material = Materials[cell.MaterialIndex];
    float heat = saturate((cell.Temperature - material.IgnitionTemperature) /
        max(1.0, material.IgnitionTemperature * 0.45));
    float flicker = 0.75 + 0.25 * HashUnitFloat(seed + FrameIndex * 17);
    return lerp(float3(1.0, 0.06, 0.005), float3(1.0, 0.62, 0.03), heat) * flicker;
}

float3 FlameGlow(uint2 coordinate)
{
    float3 glow = 0;
    for (int y = -2; y <= 2; y++)
    {
        for (int x = -2; x <= 2; x++)
        {
            int2 sample = int2(coordinate) + int2(x, y);
            if (sample.x < 0 || sample.y < 0 || sample.x >= int(Width) || sample.y >= int(Height))
            {
                continue;
            }
            GridCell flame = Grid[FlattenCoordinate(uint2(sample))];
            if (!IsFlameCell(flame) && !IsCombustibleCell(flame))
            {
                continue;
            }
            // Stretch the render-only light upward so a one-cell FIRE source
            // reads as a small tongue instead of a round glowing dot.
            float verticalDistance = y > 0 ? y * 0.72 : -y * 1.45;
            float distance = length(float2(x, verticalDistance));
            float influence = saturate(1.0 - distance / 2.8);
            float verticalWeight = y >= 0 ? 1.0 : 0.18;
            glow = max(glow, FlameSourceColor(
                flame,
                uint(sample.x * 31 + sample.y * 131)) * influence * verticalWeight);
        }
    }
    return glow;
}

float3 FlameTrail(uint2 coordinate)
{
    float3 trail = 0;
    // A sparse discrete FIRE particle receives a narrow render-only wake.
    // This connects successive rising particles without adding simulation
    // cells, emission races, or save-state data.
    for (int y = 3; y <= 8; y++)
    {
        for (int x = -2; x <= 2; x++)
        {
            int2 sample = int2(coordinate) + int2(x, y);
            if (sample.x < 0 || sample.y < 0 || sample.x >= int(Width) || sample.y >= int(Height))
            {
                continue;
            }
            GridCell flame = Grid[FlattenCoordinate(uint2(sample))];
            if (!IsFlameCell(flame) && !IsCombustibleCell(flame))
            {
                continue;
            }
            float verticalWeight = saturate(1.0 - (float(y) - 2.0) / 7.0);
            float lateralWeight = x == 0 ? 1.0 : abs(x) == 1 ? 0.55 : 0.28;
            trail = max(trail, FlameSourceColor(
                flame,
                uint(sample.x * 31 + sample.y * 131)) * verticalWeight * lateralWeight * 0.68);
        }
    }
    return trail;
}

float3 CombustionHeatGlow(GridCell cell)
{
    if (cell.IsActive == 0 || cell.MaterialIndex >= 256 ||
        Materials[cell.MaterialIndex].BurnedIntoMaterialIndex == 0xffffffffu)
    {
        return 0;
    }
    MaterialProperties material = Materials[cell.MaterialIndex];
    float ignition = material.IgnitionTemperature;
    if (ignition <= 0)
    {
        return 0;
    }
    float heat = saturate((cell.Temperature - ignition * 0.55) / (ignition * 0.45));
    return float3(1.0, 0.09, 0.01) * heat * 0.30;
}

float3 HotMaterialIncandescence(GridCell cell)
{
    if (cell.IsActive == 0 || cell.MaterialIndex >= 256)
    {
        return 0;
    }
    MaterialProperties material = Materials[cell.MaterialIndex];
    if (material.SimulationKind != SimulationKindSolid &&
        material.SimulationKind != SimulationKindGranular)
    {
        return 0;
    }
    float redHeat = saturate((cell.Temperature - 400.0) / 500.0);
    float yellowHeat = saturate((cell.Temperature - 700.0) / 500.0);
    return lerp(float3(0.72, 0.015, 0.002), float3(1.0, 0.42, 0.025), yellowHeat) *
        redHeat * 0.48;
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
        color.rgb += CombustionHeatGlow(cell);
        color.rgb += HotMaterialIncandescence(cell);
        uint kind = Materials[cell.MaterialIndex].SimulationKind;
        if (IsFluidMaterial(kind))
        {
            float fill = kind == SimulationKindLiquid ? 1 : sqrt(saturate(cell.Mass));
            color.rgb = lerp(float3(0.035, 0.041, 0.047), color.rgb, fill);
        }
        if (IsFlameCell(cell))
        {
            float3 flame = FlameColor(cell, coordinate.x * 17 + coordinate.y * 131);
            color.rgb = flame + float3(0.28, 0.06, 0.0);
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
    float3 glow = max(FlameGlow(coordinate), FlameTrail(coordinate));
    color.rgb = saturate(color.rgb + glow * 0.78);
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
