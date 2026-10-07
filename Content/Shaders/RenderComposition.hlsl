#include "PhysicsShared.hlsli"
static const uint PressureFragmentMarker = 0x40000000u;

StructuredBuffer<GridCell> Grid : register(t0);
StructuredBuffer<MaterialProperties> Materials : register(t1);
StructuredBuffer<uint> WaterActivity : register(t2);
StructuredBuffer<uint> WaterDiagnostics : register(t3);
StructuredBuffer<FireGlowCell> FireGlowField : register(t4);
StructuredBuffer<AirCell> AirField : register(t5);
StructuredBuffer<FireGlowCell> GasVisualField : register(t6);
RWTexture2D<unorm float4> OutputTexture : register(u0);
RWStructuredBuffer<SimulationStatistics> Statistics : register(u1);

float4 MaterialColor(uint materialId)
{
    MaterialProperties material = Materials[materialId];
    return float4(material.ColorR, material.ColorG, material.ColorB, material.ColorA);
}

float4 AbsorbentColor(GridCell cell)
{
    float4 color = MaterialColor(cell.MaterialIndex);
    MaterialProperties material = Materials[cell.MaterialIndex];
    // Colour actual retained stock in both render modes. Early impregnation
    // must be readable even on nearly black grains; this changes no mass.
    if (cell.Mass > 0 && material.MoistureCapacity > 0)
    {
        float water = material.MoistureCapacity > 0
            ? saturate(cell.MoistureMass / (cell.Mass * material.MoistureCapacity)) : 0;
        color.rgb *= 1.0 - 0.5 * sqrt(water);
    }
    if (cell.Mass > 0 && material.FuelCapacity > 0 && cell.FuelMass > 0)
    {
        float oil = saturate(cell.FuelMass / (cell.Mass * material.FuelCapacity));
        color.rgb = lerp(color.rgb, MaterialColor(RetainedLiquidIndex(cell,material)).rgb * .6, .85 * pow(oil, .4));
    }
    return color;
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

// Splat the coarse light field over the fine grid. The Powder Toy spreads each
// coarse cell across 12x12 pixels with a gaussian kernel built from
// exp(-0.1 * (i*i + j*j)); gathering the neighbourhood per pixel is the same
// thing computed from the other side, and avoids any scatter or atomics.
// Renderer::prepare_alpha starts with fireIntensity = 1.0.  A 1.35 multiplier
// was compensating for the old weak deposit and drove a packed collision front
// to white before the flame table could age it through orange and red.
static const float FireGlowIntensity = 1.0;

float3 ReadFireGlow(int2 coordinate, uint glowWidth)
{
    FireGlowCell glow = FireGlowField[coordinate.y * glowWidth + coordinate.x];
    return float3(glow.Red, glow.Green, glow.Blue);
}

bool IsSmokeCell(GridCell cell)
{
    return cell.IsActive != 0 &&
        (Materials[cell.MaterialIndex].Flags & MaterialFlagSmoke) != 0;
}

// Sum exp(-.1*k*k) over the four source pixels, with the existing [-4,4)
// crop. The 2-D Gaussian is separable: its 16-term sum is S(dx)*S(dy).
// Constants cover every offset used below (-4..7). Quantising the product
// gives exactly the same 8-bit weights; no per-pixel exp calls are needed.
float FireKernelSum(int delta)
{
    if (delta==-4) return .2018965180;
    if (delta==-3) return .6084661777;
    if (delta==-2) return 1.2787862238;
    if (delta==-1) return 2.1836236418;
    if (delta==0 || delta==3) return 2.9817271238;
    if (delta==1 || delta==2) return 3.4799948821;
    if (delta==4) return 1.9817271238;
    if (delta==5) return 1.0768897058;
    if (delta==6) return .4065696597;
    return 0;
}

float3 SampleFireGlow(uint2 coordinate, out float smokeCoverage, out float3 gasColor)
{
    uint glowWidth = (Width + AirCellSize - 1) / AirCellSize;
    uint glowHeight = (Height + AirCellSize - 1) / AirCellSize;
    // This is Renderer::prepare_alpha + render_fire expressed as a gather.
    // TPT builds fire_alpha by summing exp(-0.1*(i*i+j*j)) for all 4x4 pixels
    // in the source cell, divides by CELL*CELL, then *adds* every overlapping
    // coarse fire value.  It is a sum, not a normalized interpolation; the
    // latter was responsible for an unrelated gain knob and for smoke dots.
    int2 base = int2(coordinate / AirCellSize);
    float3 total = 0;
    float smokeTotal = 0;
    gasColor = 0;
    for (int offsetY = -1; offsetY <= 1; offsetY++)
    {
        for (int offsetX = -1; offsetX <= 1; offsetX++)
        {
            int2 sample = base + int2(offsetX, offsetY);
            if (sample.x < 0 || sample.y < 0 ||
                sample.x >= int(glowWidth) || sample.y >= int(glowHeight))
            {
                continue;
            }
            FireGlowCell glow = FireGlowField[sample.y * glowWidth + sample.x];
            FireGlowCell gas = GasVisualField[sample.y * glowWidth + sample.x];
            if (all(float4(glow.Red,glow.Green,glow.Blue,glow.Smoke)==0) &&
                all(float3(gas.Red,gas.Green,gas.Blue)==0)) continue;
            float2 delta = float2(int(coordinate.x), int(coordinate.y)) -
                float2(sample * int(AirCellSize));
            float weight = FireKernelSum(int(delta.x))*FireKernelSum(int(delta.y));
            // Renderer::prepare_alpha quantises the normalized gaussian to
            // an 8-bit alpha before AddFirePixel consumes it.
            weight = floor(255.0 * weight / float(AirCellSize * AirCellSize)) / 255.0;
            total += float3(glow.Red, glow.Green, glow.Blue) * weight;
            smokeTotal += glow.Smoke * weight;
            gasColor += float3(gas.Red, gas.Green, gas.Blue) * weight;
        }
    }
    smokeCoverage = smokeTotal;
    return total;
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
    // Накал начинается примерно с 200 градусов, как в The Powder Toy: там
    // PROP_HOT_GLOW включается при HighTemperature минус 800 K, что для металла
    // с плавлением около 1000 C даёт ровно 200. Порог в 400 означал, что при
    // одинаковой фактической температуре TPT уже показывает красный металл,
    // а у нас он ещё обычный серый.
    float redHeat = saturate((cell.Temperature - 200.0) / 400.0);
    float yellowHeat = saturate((cell.Temperature - 600.0) / 500.0);
    return lerp(float3(0.72, 0.015, 0.002), float3(1.0, 0.42, 0.025), yellowHeat) *
        redHeat * 0.48;
}

void FluidCoverage(
    uint2 coordinate,
    out float liquidCoverage,
    out float3 liquidColor,
    out float gasCoverage,
    out float3 gasColor)
{
    float liquidAmount = 0;
    float liquidWeight = 0;
    float3 weightedLiquidColor = 0;
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            int2 sample = int2(coordinate) + int2(x, y);
            if (sample.x < 0 || sample.y < 0 ||
                sample.x >= int(Width) || sample.y >= int(Height))
            {
                continue;
            }
            float liquidSampleWeight = x == 0 && y == 0 ? 4 :
                x == 0 || y == 0 ? 2 : 1;
            GridCell sampleCell = Grid[FlattenCoordinate(uint2(sample))];
            if (sampleCell.IsActive != 0)
            {
                MaterialProperties material = Materials[sampleCell.MaterialIndex];
                if (material.SimulationKind == SimulationKindLiquid)
                {
                    float amount = saturate(sampleCell.Mass) * liquidSampleWeight;
                    liquidAmount += amount;
                    weightedLiquidColor +=
                        MaterialColor(sampleCell.MaterialIndex).rgb * amount;
                }
            }
            liquidWeight += liquidSampleWeight;
        }
    }
    liquidCoverage = liquidAmount / max(liquidWeight, 1);
    liquidColor = liquidAmount > 0 ? weightedLiquidColor / liquidAmount : 0;
    GridCell gasCell = Grid[FlattenCoordinate(coordinate)];
    bool visibleGas = gasCell.IsActive != 0 &&
        Materials[gasCell.MaterialIndex].SimulationKind == SimulationKindGas &&
        !IsFlameCell(gasCell);
    float4 directGasColor = visibleGas ? MaterialColor(gasCell.MaterialIndex) : 0;
    gasCoverage = visibleGas
        ? saturate(gasCell.Mass) * directGasColor.a
        : 0;
    gasColor = directGasColor.rgb;
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
    bool densityBody = (Materials[cell.MaterialIndex].Flags & MaterialFlagDensityBody) != 0;
    if (kind == SimulationKindSolid && (densityBody || (cell.BodyId & PressureFragmentMarker) != 0)) InterlockedAdd(Statistics[0].FreeBodyCells, 1, ignored);
    bool restingSolid = kind == SimulationKindSolid && ((!densityBody && (cell.BodyId & PressureFragmentMarker) == 0 && SolidGravity == 0) || cell.RestFrames >= 2);
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

float4 FilterOverlay(uint2 coordinate,float4 color)
{
    if(FilterCells[0]==0)return color;
    uint filter=FilterCells[coordinate.y*Width+coordinate.x+1];
    if(filter!=0){
        float3 tint=float3(.25,.75,.95);
        uint exact=filter&FilterIdMask;
        // Exact-species brushes follow the registered colour, including mods.
        // Lift dark materials so their passage overlay stays visible.
        if(exact!=0) tint=lerp(MaterialColor(exact-1).rgb,float3(1,1,1),.18);
        if((filter&FilterGas)!=0)tint=float3(.37,.82,.61);
        if((filter&FilterClosed)!=0)tint=float3(.65,.68,.72);
        if(filter==FilterAir)tint=float3(.6,.8,.95);
        if((filter&FilterAllParticles)!=0)tint=float3(.9,.45,.65);
        if((filter&FilterPowder)!=0)tint=float3(.95,.65,.25);
        bool mesh=(coordinate.x+coordinate.y)%4<2;
        color.rgb=lerp(color.rgb,tint,mesh?.6:.18);
    }
    return color;
}

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= Width || coordinate.y >= Height)
    {
        return;
    }

    // Диагностический режим. Каждая попытка настроить огонь на глаз промахивалась
    // на порядок именно потому, что поле скоростей не видно.
    if (DebugView == DebugViewAir)
    {
        uint airWidth = (Width + AirCellSize - 1) / AirCellSize;
        uint airHeight = (Height + AirCellSize - 1) / AirCellSize;
        uint2 airCoordinate = min(
            coordinate / AirCellSize,
            uint2(airWidth - 1, airHeight - 1));
        AirCell air = AirField[airCoordinate.y * airWidth + airCoordinate.x];
        float up = max(0.0, -air.VelocityY);
        float down = max(0.0, air.VelocityY);
        float sideways = abs(air.VelocityX);
        // Логарифмическая шкала: интересен диапазон от сотых до десятков.
        float3 debug = float3(
            saturate(log2(1.0 + up * 8.0) / 6.0),
            saturate(log2(1.0 + sideways * 8.0) / 6.0),
            saturate(log2(1.0 + down * 8.0) / 6.0));
        if (air.Blocked > 0.5)
        {
            debug = float3(0.25, 0.25, 0.25);
        }
        // Colours encode magnitudes; arrows also expose the horizontal sign
        // and the actual route through a furnace. Sample one visible carrier
        // per 16-pixel tile, and never draw the glyph on solid/liquid cells.
        uint2 anchor = (coordinate / 16) * 16 + 10;
        uint2 vectorNode = min(anchor / AirCellSize,uint2(airWidth-1,airHeight-1));
        AirCell vectorAir = AirField[vectorNode.y*airWidth+vectorNode.x];
        float2 velocity = float2(vectorAir.VelocityX,vectorAir.VelocityY);
        float speed = length(velocity);
        GridCell atPixel = Grid[FlattenCoordinate(coordinate)];
        bool gasPixel = atPixel.IsActive==0 || Materials[atPixel.MaterialIndex].SimulationKind==SimulationKindGas;
        if(vectorAir.Blocked<=.5 && speed>.05 && gasPixel)
        {
            float2 axis=velocity/speed;
            float2 local=float2(coordinate)-float2(anchor);
            float along=dot(local,axis),across=dot(local,float2(-axis.y,axis.x));
            float halfLength=2+2*saturate(log2(1+speed*4)/3);
            bool shaft=abs(across)<.6 && along>=-halfLength && along<=halfLength;
            float head=halfLength-along;
            bool arrow=head>=0 && head<=2 && abs(abs(across)-head)<.6;
            if(shaft||arrow)debug=lerp(debug,float3(.9,.94,1),.85);
        }
        OutputTexture[coordinate] = FilterOverlay(coordinate,float4(debug,1));
        return;
    }

    GridCell cell = Grid[FlattenCoordinate(coordinate)];
    // TPT's Nothing Display equivalent. Keep this before every composition
    // path: an occupied simulation cell maps to precisely one output pixel
    // of its material colour. FIRE is deliberately drawn here too, unlike
    // the normal mode where it is represented only by FireGlow.
    if (DebugView == DebugViewWithoutEffects)
    {
        float4 flatColor = float4(0.035, 0.041, 0.047, 1);
        if (cell.IsActive != 0)
        {
            flatColor = AbsorbentColor(cell);
            flatColor.a = 1;
        }
        OutputTexture[coordinate] = FilterOverlay(coordinate,flatColor);
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
        return;
    }

    float4 color = float4(0.035, 0.041, 0.047, 1);
    bool continuumGas = cell.IsActive != 0 &&
        Materials[cell.MaterialIndex].SimulationKind == SimulationKindGas &&
        !IsFlameCell(cell);
    // A flame cell is never painted as a pixel of its own. The Powder Toy sets
    // pixel_mode = PMODE_NONE for FIRE and draws nothing but the accumulated
    // light field, which is exactly why its flame has no visible grain: what
    // you see is the glow, not the particles. Painting the cells as well left
    // a lattice of hard dots showing through the glow.
    bool flameCell = cell.IsActive != 0 && IsFlameCell(cell);
    if (cell.IsActive != 0 && !continuumGas && !flameCell)
    {
        color = AbsorbentColor(cell);
        color.rgb += CombustionHeatGlow(cell);
        color.rgb += HotMaterialIncandescence(cell);
    }
    if (cell.IsActive == 0 || continuumGas || flameCell)
    {
        float liquidCoverage;
        float3 liquidColor;
        float gasCoverage;
        float3 gasColor;
        FluidCoverage(coordinate, liquidCoverage, liquidColor, gasCoverage, gasColor);
        if (cell.IsActive == 0 && liquidCoverage > 0.02)
        {
            color.rgb = lerp(color.rgb, liquidColor, saturate(liquidCoverage * 2.5));
        }
        if (gasCoverage > 0 && Materials[cell.MaterialIndex].GasHazeStrength <= 0)
        {
            // Explicit zero-haze external gases keep their legacy flat path.
            // Soft gases, including smoke, use the coarse fields below.
            // Painting smoke's occupied pixel as well exposed a hard grain
            // inside every soft splat, especially for whole flame packets.
            float edgeNoise = (HashUnitFloat(
                FlattenCoordinate(coordinate) ^ 0x6d2b79f5u) - 0.5) * 0.016;
            float gasOpacity = saturate(smoothstep(
                GasVisibleMassThreshold - 0.02 + edgeNoise,
                GasVisibleMassThreshold + 0.04 + edgeNoise,
                gasCoverage) * 1.35);
            color.rgb = lerp(color.rgb, gasColor, gasOpacity);
        }
    }
    // SMKE has one soft FIRE_BLEND field, as in TPT. The old additional
    // mass-threshold haze produced pale, jagged patches over dense packets.
    float smokeCoverage;
    float3 gasCloudColor;
    float3 fireGlow = SampleFireGlow(coordinate, smokeCoverage, gasCloudColor);
    // Occlude render-only gas trails at non-gas occupied cells.
    if (cell.IsActive == 0 || continuumGas || flameCell)
    {
        color.rgb += gasCloudColor;
    }
    // Our smoke buffer is separate from TPT's shared RGB fire buffer. Convert
    // its smooth density proxy to bounded opacity: adding independent grey
    // luminance to the fire washed out its orange edges. This is render-only
    // optical coverage, not physical aerosol density or extra smoke mass.
    bool smokeVisibleHere = cell.IsActive == 0 ||
        Materials[cell.MaterialIndex].SimulationKind == SimulationKindGas;
    if (smokeVisibleHere && smokeCoverage > 0)
    {
        float smokeOpacity = smokeCoverage / (0.28 + smokeCoverage);
        color.rgb = lerp(color.rgb, float3(0.38, 0.37, 0.35), smokeOpacity);
    }

    // TPT's additive flame display: bright yellow/white cores, orange/red edges.
    // No per-particle dots, random flicker, new particles or physical forces.
    // The old FlameGlow/FlameTrail pair is gone. Both painted a fixed shape
    // around each cell and combined with max(), so two flames were exactly as
    // bright as one and the result could only ever look like separate sparks.
    // Keep the flame on the gas side of occupied matter. This includes coal
    // grains and water, not just rigid solids: a glow splat through fuel made
    // the entire mound look like a painted orange plate. Its own temperature
    // still supplies incandescence above; this changes no heat exchange.
    float fireGlowTransmission = 1.0;
    if (cell.IsActive != 0 &&
        Materials[cell.MaterialIndex].SimulationKind != SimulationKindGas)
    {
        fireGlowTransmission = 0.0;
    }
    color.rgb += fireGlow * FireGlowIntensity * fireGlowTransmission;
    color.rgb = saturate(color.rgb);
    OutputTexture[coordinate] = FilterOverlay(coordinate,color);
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
