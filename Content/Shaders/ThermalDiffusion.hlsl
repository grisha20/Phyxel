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
    float edgeCoefficient =
        min(capacity, neighborCapacity) * contactConductivity * exchangeFraction *
        contactWeight / (gasSurface ? 6 : 4);
    return edgeCoefficient * (neighbor.Temperature - cell.Temperature);
}

// Bounded game approximation of radiant heat across a resolved air gap.
// Both endpoints gather exactly the same pair from SourceGrid: no atomics,
// extra heat source, or change to air/gas momentum. Condensed matter occludes.
static const int RadiantRange = 24;

bool IsRadiantGas(MaterialProperties material)
{
    return material.SimulationKind == SimulationKindGas &&
        (material.Flags & (MaterialFlagFlame | MaterialFlagSmoke)) != 0;
}

float RadiantPair(GridCell cell, GridCell other, int distance)
{
    MaterialProperties a = Materials[cell.MaterialIndex];
    MaterialProperties b = Materials[other.MaterialIndex];
    if (a.ThermalConductivity <= 0 || b.ThermalConductivity <= 0) return 0;
    bool gasA = IsRadiantGas(a);
    uint gasFlags = gasA ? a.Flags : b.Flags;
    float solidConductivity = gasA ? b.ThermalConductivity : a.ThermalConductivity;
    // Conductivity is not emissivity. Use explicit game strengths for the
    // existing luminous-flame/soot roles, bounded by the receiving surface.
    float coupling = saturate(solidConductivity) *
        ((gasFlags & MaterialFlagFlame) != 0 ? 1.0 : 0.25);
    // Four rays contain at most 46 total weights (distances 2..24).
    // .18 plus the contact bound .80 stays below unity for either endpoint.
    float fraction = min(0.18, 10.8 * ThermalDeltaTime);
    float weight = (RadiantRange + 1.0 - distance) / RadiantRange;
    float coefficient = min(EffectiveCapacity(cell), EffectiveCapacity(other)) *
        coupling * fraction * weight / 48.0;
    return coefficient * (other.Temperature - cell.Temperature);
}

float RadiantHeatFlow(GridCell cell, uint2 coordinate)
{
    MaterialProperties material = Materials[cell.MaterialIndex];
    bool emitter = IsRadiantGas(material);
    if ((!emitter && material.SimulationKind != SimulationKindSolid) ||
        material.ThermalConductivity <= 0) return 0;
    float heat = 0;
    [unroll]
    for (int direction = 0; direction < 4; direction++)
    {
        int2 step = direction == 0 ? int2(-1, 0) : direction == 1 ? int2(1, 0) :
            direction == 2 ? int2(0, -1) : int2(0, 1);
        [loop]
        for (int distance = 1; distance <= RadiantRange; distance++)
        {
            int2 p = int2(coordinate) + step * distance;
            if (p.x < 0 || p.y < 0 || p.x >= (int)ThermalWidth || p.y >= (int)ThermalHeight) break;
            GridCell other = SourceGrid[p.y * ThermalWidth + p.x];
            if (other.IsActive == 0) continue;
            MaterialProperties otherMaterial = Materials[other.MaterialIndex];
            if (otherMaterial.SimulationKind == SimulationKindGas)
            {
                if (!emitter && distance > 1 && IsRadiantGas(otherMaterial))
                    heat += RadiantPair(cell, other, distance);
                continue;
            }
            if (emitter && distance > 1 && otherMaterial.SimulationKind == SimulationKindSolid)
                heat += RadiantPair(cell, other, distance);
            break;
        }
    }
    return heat;
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

    heatFlow += RadiantHeatFlow(cell, coordinate);
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
