#include "PhysicsShared.hlsli"
#include "OxidizerShared.hlsli"

cbuffer CombustionConstants : register(b0)
{
    float CombustionDeltaTime;
    uint CombustionWidth;
    uint CombustionHeight;
    uint CombustionMaterialCount;
    uint CombustionTickIndex;
    uint CombustionReserved0;
    uint CombustionReserved1;
    uint CombustionReserved2;
};

StructuredBuffer<MaterialProperties> Materials : register(t0);
StructuredBuffer<MaterialEmissionProperties> Emissions : register(t1);
StructuredBuffer<float> Oxidizer : register(t2);
RWStructuredBuffer<GridCell> Grid : register(u0);
RWStructuredBuffer<uint> CombustionSummary : register(u1);
RWStructuredBuffer<uint> EmissionClaims : register(u2);
RWStructuredBuffer<EmissionRequest> EmissionRequests : register(u3);
RWStructuredBuffer<float> OxidizerDemand : register(u4);

static const float CombustionMassEpsilon = 0.0001;
static const float MinimumCombustionTemperature = -273.15;
static const float MaximumCombustionTemperature = 5000.0;
static const uint CombustionOccurred = 1u << 0;
static const uint BurnoutOccurred = 1u << 1;
static const uint TargetCellular = 1u << 2;
static const uint TargetLiquid = 1u << 3;
static const uint TargetGas = 1u << 4;
static const uint TouchesLiquid = 1u << 5;
static const uint TouchesSolid = 1u << 6;
static const uint TargetMovableSolid = 1u << 7;

float ResidueMass(MaterialProperties target, uint targetIndex)
{
    return targetIndex == 0 || target.SimulationKind == SimulationKindNone
        ? 0
        : max(0, target.Density);
}

uint TargetFlags(MaterialProperties source, MaterialProperties target)
{
    uint flags = 0;
    // TargetCellular drives the cellular wake-up on the CPU side. It must be
    // raised when the SOURCE is cellular too, not only the target: a burning
    // powder that turns into empty space leaves a hole its neighbours have to
    // fall into. Looking at the target alone, granular -> empty produced no
    // flag at all, so the pile above a burnt-out cell stayed asleep in mid-air.
    if (IsCellularMaterial(source.SimulationKind) ||
        IsCellularMaterial(target.SimulationKind)) flags |= TargetCellular;
    if (target.SimulationKind == SimulationKindLiquid) flags |= TargetLiquid;
    if (target.SimulationKind == SimulationKindGas) flags |= TargetGas;
    if (source.SimulationKind == SimulationKindLiquid || target.SimulationKind == SimulationKindLiquid)
        flags |= TouchesLiquid;
    if (source.SimulationKind == SimulationKindSolid || target.SimulationKind == SimulationKindSolid)
        flags |= TouchesSolid;
    if (IsMovableSolidMaterial(target)) flags |= TargetMovableSolid;
    return flags;
}

void NormalizeBurnout(inout GridCell cell, MaterialProperties source, MaterialProperties target, uint targetIndex)
{
    if (targetIndex == 0 || target.SimulationKind == SimulationKindNone)
    {
        cell = (GridCell)0;
        return;
    }

    bool sourceCellular = IsCellularMaterial(source.SimulationKind);
    bool targetCellular = IsCellularMaterial(target.SimulationKind);
    cell.MaterialIndex = targetIndex;
    cell.IsActive = 1;
    cell.Mass = target.Density;
    cell.BodyId = 0;
    if (!sourceCellular || !targetCellular)
    {
        cell.VelocityX = 0;
        cell.VelocityY = 0;
    }
    if (source.SimulationKind != SimulationKindLiquid || target.SimulationKind != SimulationKindLiquid)
    {
        cell.Pressure = 0;
    }
    cell.RestFrames = target.SimulationKind == SimulationKindSolid && !IsMovableSolidMaterial(target)
        ? 2u
        : 0u;
    cell.Lifetime = InitialMaterialLifetime(target, targetIndex ^ cell.MaterialIndex);
}

void ProposeEmission(
    uint sourceIndex,
    uint destinationIndex,
    uint productIndex,
    float rate,
    float elapsedSeconds,
    uint requestIndex,
    float temperature,
    bool selfOxidizing)
{
    if (productIndex == 0xffffffffu || productIndex >= CombustionMaterialCount || rate <= 0 ||
        destinationIndex == sourceIndex)
    {
        return;
    }
    GridCell destination = Grid[destinationIndex];
    if (destination.IsActive != 0)
    {
        return;
    }
    MaterialProperties product = Materials[productIndex];
    if (product.SimulationKind != SimulationKindGas || product.Density <= 0)
    {
        return;
    }
    bool discreteFlame = (product.Flags & MaterialFlagFlame) != 0;
    if (discreteFlame)
    {
        // Powder Toy FIRE is a discrete particle. FlameRate is a spawn
        // probability per second, not a conserved gas-mass rate.
        uint flameSeed = sourceIndex ^ destinationIndex ^ productIndex ^
            (CombustionTickIndex * 0x9e3779b9u);
        if (HashUnitFloat(flameSeed) >= saturate(rate * elapsedSeconds))
        {
            return;
        }
    }
    EmissionRequest request;
    request.DestinationIndex = destinationIndex;
    request.MaterialIndex = productIndex;
    request.Mass = discreteFlame ? product.Density : min(product.Density, rate * elapsedSeconds);
    request.Temperature = temperature;
    request.SourceIndex = sourceIndex | (discreteFlame && selfOxidizing ? SelfOxidizingFlameMarker : 0);
    EmissionRequests[requestIndex] = request;
    uint ignored;
    InterlockedMin(EmissionClaims[destinationIndex], requestIndex, ignored);
}

void ProposeEmissions(uint sourceIndex, uint sourceMaterialIndex, uint width, uint height, GridCell sourceCell)
{
    MaterialEmissionProperties emission = Emissions[sourceMaterialIndex];
    uint worldCellCount = width * height;
    uint x = sourceIndex % width;
    uint y = sourceIndex / width;
    if (y > 0 && emission.SmokeIntoMaterialIndex < CombustionMaterialCount)
    {
        ProposeEmission(sourceIndex, sourceIndex - width, emission.SmokeIntoMaterialIndex,
            emission.SmokeRate, CombustionDeltaTime, sourceIndex, sourceCell.Temperature, (Materials[sourceMaterialIndex].Flags & MaterialFlagSelfOxidizing) != 0);
    }
    if (x + 1 < width)
    {
        ProposeEmission(sourceIndex, sourceIndex + 1, emission.GasIntoMaterialIndex,
            emission.GasRate, CombustionDeltaTime, worldCellCount + sourceIndex, sourceCell.Temperature, (Materials[sourceMaterialIndex].Flags & MaterialFlagSelfOxidizing) != 0);
    }
    if (emission.FlameIntoMaterialIndex < CombustionMaterialCount)
    {
        int horizontal = (sourceIndex & 1u) == 0 ? -1 : 1;
        uint flameDestination = sourceIndex;
        uint candidate;

        // Prefer an upward plume, but fall back around the source surface.
        // A burning cell on the underside of a solid must emit into the empty
        // space below it instead of silently losing every flame proposal.
        if (y > 0)
        {
            int flameX = clamp(int(x) + horizontal, 0, int(width) - 1);
            candidate = (y - 1) * width + uint(flameX);
            if (Grid[candidate].IsActive == 0) flameDestination = candidate;
        }
        if (flameDestination == sourceIndex && y > 0)
        {
            candidate = sourceIndex - width;
            if (Grid[candidate].IsActive == 0) flameDestination = candidate;
        }
        if (flameDestination == sourceIndex)
        {
            int sideX = int(x) + horizontal;
            if (sideX >= 0 && sideX < int(width))
            {
                candidate = y * width + uint(sideX);
                if (Grid[candidate].IsActive == 0) flameDestination = candidate;
            }
        }
        if (flameDestination == sourceIndex && y + 1 < height)
        {
            int flameX = clamp(int(x) + horizontal, 0, int(width) - 1);
            candidate = (y + 1) * width + uint(flameX);
            if (Grid[candidate].IsActive == 0) flameDestination = candidate;
        }
        if (flameDestination == sourceIndex && y + 1 < height)
        {
            candidate = sourceIndex + width;
            if (Grid[candidate].IsActive == 0) flameDestination = candidate;
        }
        if (flameDestination != sourceIndex)
        {
            ProposeEmission(sourceIndex, flameDestination, emission.FlameIntoMaterialIndex,
                emission.FlameRate, CombustionDeltaTime, worldCellCount * 2 + sourceIndex,
                max(sourceCell.Temperature, Materials[emission.FlameIntoMaterialIndex].InitialTemperature),
                (Materials[sourceMaterialIndex].Flags & MaterialFlagSelfOxidizing) != 0);
        }
    }
}

// Four face-connected donors; walls and full inert-gas cells were already
// excluded by the immutable fine-grid oxidizer transport pass.
float AvailableOxidizer(uint2 p)
{
    uint i = p.y * CombustionWidth + p.x;
    float sum = 0;
    if (p.x > 0) sum += Oxidizer[i - 1];
    if (p.x + 1 < CombustionWidth) sum += Oxidizer[i + 1];
    if (p.y > 0) sum += Oxidizer[i - CombustionWidth];
    if (p.y + 1 < CombustionHeight) sum += Oxidizer[i + CombustionWidth];
    return sum;
}

bool HasLiveFlame(uint2 coordinate)
{
    [unroll]
    for (int offsetY = -2; offsetY <= 2; offsetY++)
    {
        [unroll]
        for (int offsetX = -2; offsetX <= 2; offsetX++)
        {
            if (offsetX == 0 && offsetY == 0)
            {
                continue;
            }
            int2 sample = int2(coordinate) + int2(offsetX, offsetY);
            if (sample.x < 0 || sample.y < 0 ||
                sample.x >= int(CombustionWidth) || sample.y >= int(CombustionHeight))
            {
                continue;
            }
            GridCell neighbor = Grid[uint(sample.y) * CombustionWidth + uint(sample.x)];
            if (neighbor.IsActive != 0 && neighbor.MaterialIndex < CombustionMaterialCount &&
                neighbor.Lifetime > 0 &&
                (Materials[neighbor.MaterialIndex].Flags & MaterialFlagFlame) != 0)
            {
                return true;
            }
        }
    }
    return false;
}

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= CombustionWidth || coordinate.y >= CombustionHeight)
    {
        return;
    }

    uint index = coordinate.y * CombustionWidth + coordinate.x;
    GridCell cell = Grid[index];
    if (cell.IsActive == 0 || cell.MaterialIndex >= CombustionMaterialCount)
    {
        return;
    }

    MaterialProperties source = Materials[cell.MaterialIndex];
    uint sourceMaterialIndex = cell.MaterialIndex;
    uint targetIndex = source.BurnedIntoMaterialIndex;

    // Combustion is available to solids and to granular powders. Restricting it
    // to solids forced every combustible powder to be declared as a solid,
    // which made gunpowder hang in mid-air instead of pouring, and left coal
    // unable to burn at all. Liquids and gases keep their own models: liquid
    // fuel needs a separate spread rule and flame is a transient gas.
    bool combustibleKind =
        source.SimulationKind == SimulationKindSolid ||
        source.SimulationKind == SimulationKindGranular;
    if (!combustibleKind ||
        targetIndex == 0xffffffffu || targetIndex >= CombustionMaterialCount)
    {
        return;
    }

    MaterialProperties target = Materials[targetIndex];
    float residueMass = ResidueMass(target, targetIndex);
    float availableFuel = max(0, cell.Mass - residueMass);
    if (availableFuel <= CombustionMassEpsilon || CombustionDeltaTime <= 0)
    {
        return;
    }

    bool selfOxidizing = (source.Flags & MaterialFlagSelfOxidizing) != 0;
    float oxygen = selfOxidizing ? 4 : AvailableOxidizer(coordinate);
    // Gate ignition as well as fuel loss/heat/emissions. A flame in inert gas
    // must not ignite an otherwise cold piece of wood for one frame.
    if (!selfOxidizing && oxygen / 4 <= OxidizerExtinctionThreshold) return;

    if (cell.Temperature <= source.IgnitionTemperature && source.FlameSpreadRate > 0 &&
        HasLiveFlame(coordinate))
    {
        float ignitionChance = saturate(source.FlameSpreadRate * CombustionDeltaTime);
        uint ignitionSeed = index ^ (CombustionTickIndex * 0x9e3779b9u);
        if (HashUnitFloat(ignitionSeed) < ignitionChance)
            cell.Temperature = min(MaximumCombustionTemperature, source.IgnitionTemperature + 1.0);
    }
    if (cell.Temperature <= source.IgnitionTemperature) return;

    float exposure = selfOxidizing ? 1 : saturate(oxygen / 3);
    float burnedMass = min(availableFuel, source.BurnRate * exposure * CombustionDeltaTime);
    if (!selfOxidizing)
    {
        // A donor has at most four fuel neighbours and one flame. Reserving
        // 1/5 per consumer prevents overdraw without float atomics or races.
        burnedMass = min(burnedMass, oxygen / (5 * OxidizerPerFuelMass));
        OxidizerDemand[index] = burnedMass * OxidizerPerFuelMass;
    }
    if (burnedMass <= 0) return;

    float capacityMass = max(cell.Mass, source.Density);
    float capacity = max(0.01, source.HeatCapacity * capacityMass);
    float generatedRise = burnedMass * source.HeatPerMass / capacity;
    float permittedRise = max(0.0, source.MaximumCombustionTemperature - cell.Temperature);
    cell.Temperature = clamp(
        cell.Temperature + min(generatedRise, permittedRise),
        MinimumCombustionTemperature,
        MaximumCombustionTemperature);
    float burnoutTemperature = cell.Temperature;
    cell.Mass = max(residueMass, cell.Mass - burnedMass);
    uint flags = CombustionOccurred | TargetFlags(source, target);
    if (availableFuel - burnedMass <= CombustionMassEpsilon)
    {
        cell.Mass = residueMass;
        flags |= BurnoutOccurred;
        NormalizeBurnout(cell, source, target, targetIndex);

        // Fuel that leaves no residue becomes a flame in its own cell instead
        // of vanishing. This is what The Powder Toy does: COAL turns into
        // PT_FIRE when its life runs out, and an ignited GUNP grain is
        // converted straight to PT_FIRE rather than being deleted.
        // Zeroing the cell threw away all the heat the reaction had just
        // produced, so a chain reaction died wherever there was no adjacent
        // empty cell for a flame to be emitted into: scattered grains lying on
        // the ground would heat up, consume themselves and quietly disappear
        // without ever igniting their neighbours.
        if (targetIndex == 0)
        {
            uint flameIndex = Emissions[sourceMaterialIndex].FlameIntoMaterialIndex;
            if (flameIndex != 0xffffffffu && flameIndex < CombustionMaterialCount)
            {
                MaterialProperties flame = Materials[flameIndex];
                if (flame.SimulationKind == SimulationKindGas &&
                    (flame.Flags & MaterialFlagFlame) != 0 && flame.Density > 0)
                {
                    cell.MaterialIndex = flameIndex;
                    cell.IsActive = 1;
                    cell.Mass = flame.Density;
                    cell.Temperature = max(burnoutTemperature, flame.InitialTemperature);
                    cell.Lifetime = InitialMaterialLifetime(
                        flame,
                        index ^ (CombustionTickIndex * 0x9e3779b9u));
                    cell.RestFrames = 0;
                    cell.BodyId = selfOxidizing ? SelfOxidizingFlameMarker : 0;
                    cell.VelocityX = 0;
                    cell.VelocityY = 0;
                    cell.Pressure = 0;
                    flags |= TargetGas | TargetCellular;
                }
            }
        }
    }
    Grid[index] = cell;
    InterlockedOr(CombustionSummary[0], flags);
    ProposeEmissions(index, sourceMaterialIndex, CombustionWidth, CombustionHeight, cell);
}
