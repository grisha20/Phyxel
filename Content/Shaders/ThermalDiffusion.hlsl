#include "PhysicsShared.hlsli"

cbuffer ThermalConstants : register(b0)
{
    float ThermalDeltaTime;
    float ThermalExchangeRate;
    uint ThermalWidth;
    uint ThermalHeight;
    uint ObserveThermalEnergy;
    uint ThermalReserved0;
    uint ThermalReserved1;
    uint ThermalReserved2;
};

StructuredBuffer<GridCell> SourceGrid : register(t0);
StructuredBuffer<MaterialProperties> Materials : register(t1);
StructuredBuffer<uint> BulkDegrees : register(t2);
StructuredBuffer<float> RadiantDegrees : register(t3);
RWStructuredBuffer<float> RadiantDegreeOutput : register(u2);
RWStructuredBuffer<GridCell> DestinationGrid : register(u0);
struct ThermalEnergyLedgerCell { float DeviceHeat; float AmbientHeat; };
RWStructuredBuffer<ThermalEnergyLedgerCell> EnergyLedger : register(u1);

#include "PhaseEnthalpy.hlsli"

static const float MinimumThermalMass = 0.0001;
static const float MaximumExchangeFraction = 0.80;
static const float SameGasConductivityFloor = 0.16;
static const float GasSurfaceConductivityFloor = 0.16;
// Condensable vapour also exchanges latent heat at a resolved surface. Keep
// the pair symmetric and bounded by the solid; dry gases retain their floor.
static const float PhaseVapourSurfaceConductivityFloor = 0.20;
static const float DiagonalGasContactWeight = 0.5;
// A diagonal wet contact has a smaller effective area than a shared face.
// Four faces plus four quarter contacts have total weight five: with the
// existing .80/4 exchange bound, a condensed cell cannot overshoot its neighbours.
static const float DiagonalWetContactWeight = 0.25;
static const float InteriorAmbientExposure = 0.04;

float EffectiveCapacity(GridCell cell)
{
    return CellEffectiveCapacity(cell);
}

#include "BulkThermalGeometry.hlsli"

bool ThinOpenBoilingLiquid(uint index, GridCell cell)
{
    if ((Materials[cell.MaterialIndex].Flags & MaterialFlagSurfaceBoiling) == 0) return false;
    uint x=index%ThermalWidth,span=1;
    [unroll]for(int direction=-1;direction<=1;direction+=2)
    [loop]for(int distance=1;distance<=24;distance++)
    {
        int q=(int)x+direction*distance;
        if(q<0 || q>=(int)ThermalWidth)break;
        GridCell side=SourceGrid[(index/ThermalWidth)*ThermalWidth+q];
        if(side.IsActive==0 || side.MaterialIndex!=cell.MaterialIndex)break;
        if(++span>24)return false;
    }
    uint y=index/ThermalWidth;
    [loop] for(uint depth=1;depth<=SurfaceFilmMaximumDepth;depth++)
    {
        if(y<depth) return false;
        GridCell above=SourceGrid[index-depth*ThermalWidth];
        if(above.IsActive==0 || Materials[above.MaterialIndex].SimulationKind==SimulationKindGas) return true;
        if(above.MaterialIndex!=cell.MaterialIndex) return false;
    }
    return false;
}

bool FilmSurface(GridCell liquid, uint liquidIndex, GridCell solid)
{
    MaterialProperties m=Materials[liquid.MaterialIndex], s=Materials[solid.MaterialIndex];
    return s.SimulationKind==SimulationKindSolid && s.ThermalConductivity>.5 &&
        solid.Temperature>m.TransitionAboveTemperature+SurfaceFilmTemperatureOffset &&
        liquid.Temperature>=m.TransitionAboveTemperature-.001 &&
        ThinOpenBoilingLiquid(liquidIndex,liquid);
}

bool IsSameGas(GridCell cell, GridCell neighbor)
{
    MaterialProperties material = Materials[cell.MaterialIndex];
    return cell.MaterialIndex == neighbor.MaterialIndex &&
        material.SimulationKind == SimulationKindGas &&
        (material.Flags & MaterialFlagFlame) == 0;
}

bool IsOrdinaryGasSurface(GridCell cell, GridCell neighbor)
{
    MaterialProperties a = Materials[cell.MaterialIndex];
    MaterialProperties b = Materials[neighbor.MaterialIndex];
    bool gasA = a.SimulationKind == SimulationKindGas &&
        (a.Flags & (MaterialFlagFlame | MaterialFlagSmoke)) == 0;
    bool gasB = b.SimulationKind == SimulationKindGas &&
        (b.Flags & (MaterialFlagFlame | MaterialFlagSmoke)) == 0;
    return (gasA && b.SimulationKind == SimulationKindSolid) ||
        (gasB && a.SimulationKind == SimulationKindSolid);
}

bool IsWetSurface(GridCell cell, GridCell neighbor)
{
    uint a = Materials[cell.MaterialIndex].SimulationKind;
    uint b = Materials[neighbor.MaterialIndex].SimulationKind;
    return (a == SimulationKindLiquid && b == SimulationKindSolid) ||
        (b == SimulationKindLiquid && a == SimulationKindSolid);
}

bool HasWetCornerPath(uint index, uint neighborIndex, GridCell cell, GridCell neighbor)
{
    uint2 p = uint2(index % ThermalWidth, index / ThermalWidth);
    uint2 q = uint2(neighborIndex % ThermalWidth, neighborIndex / ThermalWidth);
    GridCell first = SourceGrid[p.y * ThermalWidth + q.x];
    GridCell second = SourceGrid[q.y * ThermalWidth + p.x];
    // Do not bridge an air gap or the corner of a different insulating wall.
    bool firstPath = first.IsActive != 0 &&
        (first.MaterialIndex == cell.MaterialIndex || first.MaterialIndex == neighbor.MaterialIndex);
    bool secondPath = second.IsActive != 0 &&
        (second.MaterialIndex == cell.MaterialIndex || second.MaterialIndex == neighbor.MaterialIndex);
    return firstPath || secondPath;
}

float ContactHeatFlow(
    GridCell cell,
    float capacity,
    uint index,
    uint neighborIndex,
    float contactWeight,
    bool diagonalContact)
{
    GridCell neighbor = SourceGrid[neighborIndex];
    if (neighbor.IsActive == 0)
    {
        return 0;
    }

    bool sameGas = IsSameGas(cell, neighbor);
    bool gasSurface = IsOrdinaryGasSurface(cell, neighbor);
    bool wetSurface = IsWetSurface(cell, neighbor);
    if (diagonalContact && !sameGas && !gasSurface && !wetSurface)
    {
        return 0;
    }
    if (diagonalContact && wetSurface)
    {
        if (!HasWetCornerPath(index, neighborIndex, cell, neighbor)) return 0;
        contactWeight = DiagonalWetContactWeight;
    }

    float conductivityA = Materials[cell.MaterialIndex].ThermalConductivity;
    float conductivityB = Materials[neighbor.MaterialIndex].ThermalConductivity;
    float conductivitySum = conductivityA + conductivityB;
    if (conductivityA <= 0 || conductivityB <= 0 || conductivitySum <= 0)
    {
        return 0;
    }

    float contactConductivity =
        2 * conductivityA * conductivityB / conductivitySum;
    if (sameGas)
    {
        // Gas packets retain their identity and mass, but their temperature
        // must not retain injection-batch boundaries. This floor models local
        // molecular mixing without introducing mass redistribution here.
        contactConductivity = max(contactConductivity, SameGasConductivityFloor);
    }
    if (gasSurface)
    {
        // Particle/wall contact represents unresolved gas mixing at a surface,
        // not conduction through two stationary material slabs. Bound the
        // interface by the solid's conductivity so insulators still insulate.
        float solidConductivity = Materials[cell.MaterialIndex].SimulationKind == SimulationKindSolid
            ? conductivityA : conductivityB;
        uint gasFlags = Materials[cell.MaterialIndex].SimulationKind == SimulationKindGas
            ? Materials[cell.MaterialIndex].Flags : Materials[neighbor.MaterialIndex].Flags;
        float surfaceFloor = (gasFlags & MaterialFlagPhaseEnthalpy) != 0
            ? PhaseVapourSurfaceConductivityFloor : GasSurfaceConductivityFloor;
        contactConductivity = max(contactConductivity,
            min(solidConductivity, surfaceFloor));
    }
    float neighborCapacity = EffectiveCapacity(neighbor);
    float exchangeFraction = min(
        MaximumExchangeFraction,
        ThermalExchangeRate * ThermalDeltaTime);
    // For stronger solids, reserve additional budget for interior exchange.
    // max(kA,kB) makes the contact limit symmetric. Adjacent + interior
    // budgets still sum to <=1, so a dense plate cannot overshoot its inputs.
    float boundedExchange = contactConductivity * exchangeFraction;
    if (wetSurface &&
        ((Materials[cell.MaterialIndex].SimulationKind == SimulationKindLiquid && FilmSurface(cell,index,neighbor)) ||
         (Materials[neighbor.MaterialIndex].SimulationKind == SimulationKindLiquid && FilmSurface(neighbor,neighborIndex,cell))))
        boundedExchange *= SurfaceFilmHeatFraction;
    float maximumConductivity = max(conductivityA, conductivityB);
    if (maximumConductivity > 1)
        boundedExchange = min(MaximumExchangeFraction - .20 * saturate(maximumConductivity - 1), boundedExchange);
    // A newly connected wet surface shares its budget with bulk and radiation.
    bool surfaceA = (BulkDegrees[index] & BulkSurfaceDegreeFlag)!=0;
    bool surfaceB = (BulkDegrees[neighborIndex] & BulkSurfaceDegreeFlag)!=0;
    if((surfaceA || surfaceB) && (!wetSurface || maximumConductivity>.8))
        boundedExchange=min(boundedExchange,.48);
    float edgeCoefficient =
        min(capacity, neighborCapacity) * boundedExchange *
        contactWeight / (gasSurface ? 6 : 4);
    return edgeCoefficient * (neighbor.Temperature - cell.Temperature);
}

// Bounded game approximation of radiant heat across a resolved air gap.
// Both endpoints gather exactly the same pair from SourceGrid: no atomics,
// extra heat source, or change to air/gas momentum. Condensed matter occludes.
static const int RadiantRange = 96;

bool IsRadiantGas(MaterialProperties material)
{
    return material.SimulationKind == SimulationKindGas &&
        (material.Flags & (MaterialFlagFlame | MaterialFlagSmoke)) != 0;
}

bool IsCondensedAt(int2 p)
{
    GridCell c = SourceGrid[p.y * ThermalWidth + p.x];
    return c.IsActive != 0 && Materials[c.MaterialIndex].SimulationKind != SimulationKindGas;
}

float RadiantPair(GridCell cell, GridCell other, uint index, uint otherIndex, float weight, bool degreeOnly)
{
    MaterialProperties a = Materials[cell.MaterialIndex];
    MaterialProperties b = Materials[other.MaterialIndex];
    if (a.ThermalConductivity <= 0 || b.ThermalConductivity <= 0) return 0;
    if (degreeOnly) return weight;
    bool gasA = IsRadiantGas(a);
    uint gasFlags = gasA ? a.Flags : b.Flags;
    float solidConductivity = gasA ? b.ThermalConductivity : a.ThermalConductivity;
    // Explicit game strengths, not measured emissivity. Each endpoint has
    // the same visibility graph and coefficient; total incident budget <=.18 C.
    float coupling = saturate(solidConductivity) *
        ((gasFlags & MaterialFlagFlame) != 0 ? 1.0 : 0.25);
    float fraction = min(0.18, 10.8 * ThermalDeltaTime);
    float degree = max(1.0, max(RadiantDegrees[index], RadiantDegrees[otherIndex]));
    float coefficient = min(EffectiveCapacity(cell), EffectiveCapacity(other)) *
        coupling * fraction * weight / degree;
    return coefficient * (other.Temperature - cell.Temperature);
}

float RadiantExchange(GridCell cell, uint2 coordinate, bool degreeOnly)
{
    MaterialProperties material = Materials[cell.MaterialIndex];
    bool emitter = IsRadiantGas(material);
    if ((!emitter && material.SimulationKind != SimulationKindSolid) ||
        material.ThermalConductivity <= 0) return 0;
    uint index = coordinate.y * ThermalWidth + coordinate.x;
    float heat = 0;
    [unroll]
    for (int direction = 0; direction < 8; direction++)
    {
        int2 step = direction == 0 ? int2(-1, 0) : direction == 1 ? int2(1, 0) :
            direction == 2 ? int2(0, -1) : direction == 3 ? int2(0, 1) :
            direction == 4 ? int2(-1,-1) : direction == 5 ? int2(1,-1) :
            direction == 6 ? int2(-1,1) : int2(1,1);
        bool diagonal = direction >= 4;
        [loop]
        for (int distance = 1; distance <= RadiantRange; distance++)
        {
            int2 p = int2(coordinate) + step * distance;
            if (p.x < 0 || p.y < 0 || p.x >= (int)ThermalWidth || p.y >= (int)ThermalHeight) break;
            // The two side cells of each crossed corner are identical when
            // traversed backwards. This excludes diagonal leaks through walls.
            if (diagonal && (IsCondensedAt(p - int2(step.x,0)) || IsCondensedAt(p - int2(0,step.y)))) break;
            GridCell other = SourceGrid[p.y * ThermalWidth + p.x];
            if (other.IsActive == 0) continue;
            MaterialProperties otherMaterial = Materials[other.MaterialIndex];
            float d = distance * (diagonal ? 1.41421356237 : 1.0);
            // Euclidean range and falloff have no discontinuity at old 24-cell cutoff.
            if (d > RadiantRange) break;
            float weight = 1.0 / ((1.0 + d / 24.0) * (1.0 + d / 24.0));
            if (otherMaterial.SimulationKind == SimulationKindGas)
            {
                if (!emitter && distance > 1 && IsRadiantGas(otherMaterial))
                    heat += RadiantPair(cell, other, index, p.y * ThermalWidth + p.x, weight, degreeOnly);
                continue;
            }
            if (emitter && distance > 1 && otherMaterial.SimulationKind == SimulationKindSolid)
                heat += RadiantPair(cell, other, index, p.y * ThermalWidth + p.x, weight, degreeOnly);
            break;
        }
    }
    return heat;
}

[numthreads(16, 16, 1)]
void CSRadiantDegrees(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ThermalWidth || id.y >= ThermalHeight) return;
    uint index = id.y * ThermalWidth + id.x;
    GridCell cell = SourceGrid[index];
    RadiantDegreeOutput[index] = cell.IsActive == 0 ? 0 : RadiantExchange(cell, id.xy, true);
}


// Accelerate heat resolution inside thick high-conductivity painted walls.
// The homogeneous interior guard excludes wet/gas surface diagonals and
// radiation, leaving a .20 exchange budget beyond the local .80 bound.
float BulkHeatFlow(GridCell cell, uint2 coordinate)
{
    MaterialProperties material = Materials[cell.MaterialIndex];
    uint taggedDegree = BulkDegrees[coordinate.y * ThermalWidth + coordinate.x];
    uint degree=taggedDegree & BulkDegreeMask;
    if (material.SimulationKind != SimulationKindSolid || material.ThermalConductivity <= .5 ||
        degree == 0) return 0;
    float conductivity = pow(saturate((material.ThermalConductivity - .5) * 2), 2);
    float fraction = min(.20, 4.0 * ThermalDeltaTime) * conductivity;
    if (material.ThermalConductivity > 1)
        fraction = min(.20 * material.ThermalConductivity, 4.0 * ThermalDeltaTime * material.ThermalConductivity);
    float heat = 0;
    [unroll] for (int direction = 0; direction < 4; direction++)
    {
        int2 step = direction == 0 ? int2(-1, 0) : direction == 1 ? int2(1, 0) :
            direction == 2 ? int2(0, -1) : int2(0, 1);
        [loop] for (int distance = 2; distance <= 16; distance += 2)
        {
            int2 p = int2(coordinate) + step * distance;
            if (p.x < 1 || p.y < 1 || p.x + 1 >= (int)ThermalWidth || p.y + 1 >= (int)ThermalHeight) break;
            GridCell other = SourceGrid[p.y * ThermalWidth + p.x];
            uint taggedOtherDegree = BulkDegrees[p.y * ThermalWidth + p.x];
            uint otherDegree=taggedOtherDegree & BulkDegreeMask;
            if (other.IsActive == 0 || other.MaterialIndex != cell.MaterialIndex) continue;
            if (otherDegree == 0 || abs(other.Temperature - cell.Temperature) < .000001) continue;
            // max(endpoint degrees) is symmetric, while each endpoint's
            // incident sum is <= its bounded interior budget.
            float pairFraction=fraction;
            if(((taggedDegree | taggedOtherDegree) & BulkSurfaceDegreeFlag)!=0)
                // Wet boundary connects directly to the metal's interior.
                // Preserve k ordering without applying the dry interior's
                // additional squared mobility reduction to this boundary.
                // Local <=.80, wet bulk <=.20 and surface radiation are
                // bounded separately at the high-k interface above.
                pairFraction=.20*saturate(material.ThermalConductivity);
            if (HasBulkPath(int2(coordinate), p, cell.MaterialIndex)) heat +=
                pairFraction / max(degree, otherDegree) * min(EffectiveCapacity(cell), EffectiveCapacity(other)) *
                (other.Temperature - cell.Temperature);
        }
    }
    return heat;
}

float FilmGapHeatFlow(GridCell cell, uint2 p)
{
    bool liquid=(Materials[cell.MaterialIndex].Flags & MaterialFlagSurfaceBoiling)!=0;
    bool solid=Materials[cell.MaterialIndex].SimulationKind==SimulationKindSolid;
    if(!liquid && !solid) return 0;
    int step=liquid?1:-1;
    int y=int(p.y)+2*step;
    if(y<0 || y>=(int)ThermalHeight) return 0;
    uint otherIndex=y*ThermalWidth+p.x, gapIndex=(int(p.y)+step)*ThermalWidth+p.x;
    GridCell gap=SourceGrid[gapIndex], other=SourceGrid[otherIndex];
    if(gap.IsActive!=0 || other.IsActive==0) return 0;
    GridCell water=cell, wall=other;
    if(!liquid) {water=other;wall=cell;}
    uint waterIndex=liquid?p.y*ThermalWidth+p.x:otherIndex;
    if(!FilmSurface(water,waterIndex,wall)) return 0;
    float ka=Materials[cell.MaterialIndex].ThermalConductivity,kb=Materials[other.MaterialIndex].ThermalConductivity;
    if(ka<=0 || kb<=0) return 0;
    float coefficient=min(EffectiveCapacity(cell),EffectiveCapacity(other))*
        (2*ka*kb/(ka+kb))*min(MaximumExchangeFraction,ThermalExchangeRate*ThermalDeltaTime)*SurfaceFilmHeatFraction/4;
    return coefficient*(other.Temperature-cell.Temperature);
}

bool IsEmptyAt(int2 coordinate)
{
    if (coordinate.x < 0 || coordinate.y < 0 ||
        coordinate.x >= (int)ThermalWidth || coordinate.y >= (int)ThermalHeight)
    {
        return true;
    }
    return SourceGrid[coordinate.y * ThermalWidth + coordinate.x].IsActive == 0;
}

float AmbientSurfaceExposure(uint2 coordinate)
{
    uint immediateEmptyNeighbors = 0;
    uint localEmptyNeighbors = 0;
    [unroll]
    for (int y = -2; y <= 2; y++)
    {
        [unroll]
        for (int x = -2; x <= 2; x++)
        {
            if ((x != 0 || y != 0) && IsEmptyAt(int2(coordinate) + int2(x, y)))
            {
                localEmptyNeighbors++;
                if (abs(x) <= 1 && abs(y) <= 1)
                {
                    immediateEmptyNeighbors++;
                }
            }
        }
    }
    float surfaceOpenFraction = immediateEmptyNeighbors / 8.0;
    float localOpenFraction = localEmptyNeighbors / 24.0;
    // A partially sheltered packet must cool substantially more slowly than a
    // fully exposed one. The immediate ring measures the actual open surface;
    // the outer ring distinguishes a sparse grid cloud's core from its edge.
    float exposure = pow(surfaceOpenFraction, 4.0) * pow(localOpenFraction, 12.0);
    return lerp(InteriorAmbientExposure, 1.0, exposure);
}

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= ThermalWidth || coordinate.y >= ThermalHeight)
    {
        return;
    }

    uint index = coordinate.y * ThermalWidth + coordinate.x;
    GridCell cell = SourceGrid[index];
    if (cell.IsActive == 0)
    {
        // The coordinator clears the destination in one bulk GPU operation.
        // Sparse scenes need no per-thread 48-byte stores for empty cells.
        return;
    }

    float capacity = EffectiveCapacity(cell);
    float heatFlow = 0;
    if (coordinate.x > 0)
    {
        heatFlow += ContactHeatFlow(cell, capacity, index, index - 1, 1.0, false);
    }
    if (coordinate.x + 1 < ThermalWidth)
    {
        heatFlow += ContactHeatFlow(cell, capacity, index, index + 1, 1.0, false);
    }
    if (coordinate.y > 0)
    {
        heatFlow += ContactHeatFlow(cell, capacity, index, index - ThermalWidth, 1.0, false);
    }
    if (coordinate.y + 1 < ThermalHeight)
    {
        heatFlow += ContactHeatFlow(cell, capacity, index, index + ThermalWidth, 1.0, false);
    }


    // Include diagonal gas/surface contacts on both endpoints. The symmetric
    // coefficient conserves exchanged energy; division by six bounds the
    // four cardinal plus four half-weight gas contacts. Wet corners use
    // quarter weights; dry condensed and combustion contacts keep four faces.
    if (coordinate.x > 0 && coordinate.y > 0)
        heatFlow += ContactHeatFlow(cell, capacity, index, index - ThermalWidth - 1,
            DiagonalGasContactWeight, true);
    if (coordinate.x + 1 < ThermalWidth && coordinate.y > 0)
        heatFlow += ContactHeatFlow(cell, capacity, index, index - ThermalWidth + 1,
            DiagonalGasContactWeight, true);
    if (coordinate.x > 0 && coordinate.y + 1 < ThermalHeight)
        heatFlow += ContactHeatFlow(cell, capacity, index, index + ThermalWidth - 1,
            DiagonalGasContactWeight, true);
    if (coordinate.x + 1 < ThermalWidth && coordinate.y + 1 < ThermalHeight)
        heatFlow += ContactHeatFlow(cell, capacity, index, index + ThermalWidth + 1,
            DiagonalGasContactWeight, true);

    heatFlow += RadiantExchange(cell, coordinate, false);
    heatFlow += BulkHeatFlow(cell, coordinate);
    heatFlow += FilmGapHeatFlow(cell, coordinate);
    MaterialProperties material = Materials[cell.MaterialIndex];
    float ambientHeat = 0;
    float deviceHeat = 0;
    if (HasPhaseEnthalpy(material) || cell.MoistureMass > 0)
        cell = SetCellSpecificEnthalpy(cell,
            CellSpecificEnthalpy(cell) + heatFlow / max(cell.Mass, MinimumThermalMass));
    else
        cell.Temperature += heatFlow / capacity;
    if (material.AmbientCoolingRate > 0)
    {
        float ambientRate = material.AmbientCoolingRate * AmbientSurfaceExposure(coordinate);
        float ambientFactor = 1.0 - exp(-ambientRate * ThermalDeltaTime);
        float temperatureChange =
            (material.AmbientTemperature - cell.Temperature) * saturate(ambientFactor);
        ambientHeat = capacity * temperatureChange;
        if (HasPhaseEnthalpy(material) || cell.MoistureMass > 0)
            cell = SetCellSpecificEnthalpy(cell,
                CellSpecificEnthalpy(cell) + ambientHeat / max(cell.Mass, MinimumThermalMass));
        else
            cell.Temperature += temperatureChange;
    }
    if ((material.Flags & (MaterialFlagThermalHeater | MaterialFlagThermalCooler)) != 0)
    {
        float target = clamp(cell.Pressure, -273.15, 5000.0);
        float limit = clamp(cell.Lifetime, 0, 3600.0) * ThermalDeltaTime;
        float required = capacity * (target - cell.Temperature);
        deviceHeat = (material.Flags & MaterialFlagThermalHeater) != 0
            ? clamp(required, 0, limit) : clamp(required, -limit, 0);
        cell.Temperature += deviceHeat / capacity;
    }
    if (ObserveThermalEnergy != 0)
    {
        // One invocation owns each entry; this observer never feeds physics.
        ThermalEnergyLedgerCell ledger = EnergyLedger[index];
        ledger.DeviceHeat += deviceHeat;
        ledger.AmbientHeat += ambientHeat;
        EnergyLedger[index] = ledger;
    }
    DestinationGrid[index] = cell;
}
