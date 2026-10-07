#include "PhysicsShared.hlsli"
#include "OxidizerShared.hlsli"

cbuffer CombustionConstants : register(b0)
{
    float CombustionDeltaTime;
    uint CombustionWidth;
    uint CombustionHeight;
    uint CombustionMaterialCount;
    uint CombustionTickIndex;
    uint FiniteOxidizer;
    uint CombustionHasReactionSources;
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
// Pending (pressure quantity, gas energy, gas heat capacity, reserved).
// A reaction writes its own fine cell, independently of product spawn claims.
RWStructuredBuffer<float4> ReactionPending : register(u5);

#include "PhaseEnthalpy.hlsli"

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
static const uint PressurePowderPresent = 1u << 8;

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
    bool selfOxidizing,
    float flameLifetimeMultiplier)
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
    if(!FilterPathAllows(sourceIndex,destinationIndex,productIndex,SimulationKindGas,CombustionWidth))return;
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
    request.FlameLifetimeMultiplier = discreteFlame ? flameLifetimeMultiplier : 1;
    request.SourceIndex = sourceIndex | (discreteFlame
        ? (selfOxidizing ? SelfOxidizingFlameMarker : (FiniteOxidizer != 0 ? ReactedFuelFlameMarker : 0)) : 0);
    // Host and retained fuel share this cell's three product slots. Combine
    // matching proposals; overwriting a slot would lose the first reaction's
    // product and could leave a claim pointing at another destination.
    EmissionRequest previous = EmissionRequests[requestIndex];
    if (previous.Mass > 0)
    {
        if (previous.DestinationIndex != destinationIndex || previous.MaterialIndex != productIndex) return;
        request.Temperature = discreteFlame ? max(previous.Temperature, temperature) :
            (previous.Temperature * previous.Mass + temperature * request.Mass) / (previous.Mass + request.Mass);
        request.Mass = min(product.Density, previous.Mass + request.Mass);
        request.FlameLifetimeMultiplier = max(previous.FlameLifetimeMultiplier, request.FlameLifetimeMultiplier);
        request.SourceIndex |= previous.SourceIndex;
    }
    EmissionRequests[requestIndex] = request;
    uint ignored;
    InterlockedMin(EmissionClaims[destinationIndex], requestIndex, ignored);
}

void ProposeEmissions(uint sourceIndex, uint sourceMaterialIndex, uint width, uint height, GridCell sourceCell,
    float reactionFraction)
{
    MaterialEmissionProperties emission = Emissions[sourceMaterialIndex];
    MaterialProperties source = Materials[sourceMaterialIndex];
    if (FiniteOxidizer != 0 && (source.Flags & MaterialFlagPersistentCoalIgnition) != 0 &&
        emission.GasIntoMaterialIndex < CombustionMaterialCount &&
        (Materials[emission.GasIntoMaterialIndex].Flags & MaterialFlagThermalCarbonDioxide) != 0)
    {
        // Carbon -> CO2 adds oxygen mass: 44/12 is the maximum product/fuel
        // ratio. The old rates created 10..17 times the burned carbon mass.
        emission.GasRate = min(emission.GasRate, source.BurnRate * (44.0 / 12.0));
    }
    // Rates describe full-rate combustion. Starved fuel cannot keep producing
    // full-rate CO2/soot after its actual reaction and heat have nearly stopped.
    emission.SmokeRate *= reactionFraction;
    emission.GasRate *= reactionFraction;
    // FlameRate controls discrete heat-tracer births/contact ignition, not a
    // product mass flux. Fuel heat and CO2/soot follow the actual reaction;
    // no flame is proposed unless that reaction consumed a positive amount.
    uint worldCellCount = width * height;
    uint x = sourceIndex % width;
    uint y = sourceIndex / width;
    if (y > 0 && emission.SmokeIntoMaterialIndex < CombustionMaterialCount)
    {
        ProposeEmission(sourceIndex, sourceIndex - width, emission.SmokeIntoMaterialIndex,
            emission.SmokeRate, CombustionDeltaTime, worldCellCount + sourceIndex, sourceCell.Temperature, (Materials[sourceMaterialIndex].Flags & MaterialFlagSelfOxidizing) != 0,source.ReactionFlameLifetimeMultiplier);
    }
    if (x + 1 < width)
    {
        ProposeEmission(sourceIndex, sourceIndex + 1, emission.GasIntoMaterialIndex,
            emission.GasRate, CombustionDeltaTime, worldCellCount * 2 + sourceIndex, sourceCell.Temperature, (Materials[sourceMaterialIndex].Flags & MaterialFlagSelfOxidizing) != 0,source.ReactionFlameLifetimeMultiplier);
    }
    if (emission.FlameIntoMaterialIndex < CombustionMaterialCount)
    {
        int horizontal = (sourceIndex & 1u) == 0 ? -1 : 1;
        uint flameDestination = sourceIndex;
        uint candidate;

        if (FiniteOxidizer != 0)
        {
            horizontal = HashUnitFloat(sourceIndex ^ (CombustionTickIndex * 0x85ebca6bu)) < .5 ? -1 : 1;
            // Both exposed sides must be considered. The old preferred-side
            // fallback could reject every proposal on half an inclined heap.
            int2 offsets[8] = { int2(horizontal,-1),int2(-horizontal,-1),int2(0,-1),
                int2(horizontal,0),int2(-horizontal,0),int2(horizontal,1),int2(-horizontal,1),int2(0,1) };
            [unroll] for (int k=0;k<8;k++)
            {
                int2 p=int2(x,y)+offsets[k];
                if (p.x<0 || p.y<0 || p.x>=int(width) || p.y>=int(height)) continue;
                uint target=uint(p.y)*width+uint(p.x);
                if (Grid[target].IsActive==0 && FilterPathAllows(sourceIndex,target,emission.FlameIntoMaterialIndex,SimulationKindGas,width)) { flameDestination=target; break; }
            }
        }

        // Prefer an upward plume, but fall back around the source surface.
        // A burning cell on the underside of a solid must emit into the empty
        // space below it instead of silently losing every flame proposal.
        if (flameDestination == sourceIndex && y > 0)
        {
            int flameX = clamp(int(x) + horizontal, 0, int(width) - 1);
            candidate = (y - 1) * width + uint(flameX);
            if (Grid[candidate].IsActive == 0 && FilterPathAllows(sourceIndex,candidate,emission.FlameIntoMaterialIndex,SimulationKindGas,width)) flameDestination = candidate;
        }
        if (flameDestination == sourceIndex && y > 0)
        {
            candidate = sourceIndex - width;
            if (Grid[candidate].IsActive == 0 && FilterPathAllows(sourceIndex,candidate,emission.FlameIntoMaterialIndex,SimulationKindGas,width)) flameDestination = candidate;
        }
        if (flameDestination == sourceIndex)
        {
            int sideX = int(x) + horizontal;
            if (sideX >= 0 && sideX < int(width))
            {
                candidate = y * width + uint(sideX);
                if (Grid[candidate].IsActive == 0 && FilterPathAllows(sourceIndex,candidate,emission.FlameIntoMaterialIndex,SimulationKindGas,width)) flameDestination = candidate;
            }
        }
        if (flameDestination == sourceIndex && y + 1 < height)
        {
            int flameX = clamp(int(x) + horizontal, 0, int(width) - 1);
            candidate = (y + 1) * width + uint(flameX);
            if (Grid[candidate].IsActive == 0 && FilterPathAllows(sourceIndex,candidate,emission.FlameIntoMaterialIndex,SimulationKindGas,width)) flameDestination = candidate;
        }
        if (flameDestination == sourceIndex && y + 1 < height)
        {
            candidate = sourceIndex + width;
            if (Grid[candidate].IsActive == 0 && FilterPathAllows(sourceIndex,candidate,emission.FlameIntoMaterialIndex,SimulationKindGas,width)) flameDestination = candidate;
        }
        if (flameDestination != sourceIndex)
        {
            ProposeEmission(sourceIndex, flameDestination, emission.FlameIntoMaterialIndex,
                // Claims select the lowest request index. A surface with only
                // one open cell must not lose every flame to its own smoke or
                // CO2 proposal; flame contacts are what propagate ignition.
                emission.FlameRate, CombustionDeltaTime, sourceIndex,
                max(sourceCell.Temperature, Materials[emission.FlameIntoMaterialIndex].InitialTemperature),
                (Materials[sourceMaterialIndex].Flags & MaterialFlagSelfOxidizing) != 0,source.ReactionFlameLifetimeMultiplier);
        }
    }
}

// Four face-connected donors. Transport masks unavailable oxidizer; count
// gas space separately so walls do not dilute concentration, while CO2 does.
float2 OxidizerAt(uint index)
{
    GridCell cell = Grid[index];
    return float2(Oxidizer[index], OxidizerSpace(cell, Materials[cell.MaterialIndex]));
}

float2 AvailableOxidizer(uint2 p)
{
    if(!FilterAirAllows(p.y*CombustionWidth+p.x))return 0;
    uint i = p.y * CombustionWidth + p.x;
    float2 sum = 0;
    if (p.x > 0) sum += OxidizerAt(i - 1);
    if (p.x + 1 < CombustionWidth) sum += OxidizerAt(i + 1);
    if (p.y > 0) sum += OxidizerAt(i - CombustionWidth);
    if (p.y + 1 < CombustionHeight) sum += OxidizerAt(i + CombustionWidth);
    return sum;
}

static const uint MaterialFlagProgressiveIgnition = 1u << 15;

uint LiveFlameCount(uint2 coordinate, bool immediateOnly)
{
    uint count = 0;
    [unroll]
    for (int offsetY = -2; offsetY <= 2; offsetY++)
    {
        [unroll]
        for (int offsetX = -2; offsetX <= 2; offsetX++)
        {
            if ((offsetX == 0 && offsetY == 0) ||
                (immediateOnly && (abs(offsetX) > 1 || abs(offsetY) > 1)))
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
                (Materials[neighbor.MaterialIndex].Flags & MaterialFlagFlame) != 0 &&
                neighbor.Temperature > Materials[neighbor.MaterialIndex].FlameExtinctionTemperature)
            {
                count++;
            }
        }
    }
    return count;
}

// A newly lit cell has elapsed time dt, below the source delay: parallel
// threads cannot ignite an entire connected cord during the same dispatch.
bool HasMatureIgnitionNeighbor(uint2 coordinate)
{
    [unroll] for (int dy = -1; dy <= 1; dy++)
    [unroll] for (int dx = -1; dx <= 1; dx++)
    {
        if (dx == 0 && dy == 0) continue;
        int2 p = int2(coordinate) + int2(dx,dy);
        if (p.x < 0 || p.y < 0 || p.x >= int(CombustionWidth) || p.y >= int(CombustionHeight)) continue;
        GridCell c = Grid[uint(p.y) * CombustionWidth + uint(p.x)];
        if (c.IsActive == 0 || c.MaterialIndex >= CombustionMaterialCount || c.MoistureMass > 0) continue;
        MaterialProperties m = Materials[c.MaterialIndex];
        if ((m.Flags & MaterialFlagProgressiveIgnition) != 0 &&
            c.Lifetime >= max(1 / max(m.FlameSpreadRate,.0001),2 * CombustionDeltaTime) && c.Temperature > m.ContactIgnitionTemperature)
            return true;
    }
    return false;
}

void ReactFuel(uint2 coordinate, bool absorbedFuel)
{
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
    if (source.SimulationKind == SimulationKindGranular && source.ReactionPressurePerMass > 0)
    {
        uint ignored;
        InterlockedOr(CombustionSummary[0], PressurePowderPresent, ignored);
    }
    if (cell.MoistureMass > 0)
    {
        cell.Lifetime = 0;
        Grid[index] = cell;
        return;
    }
    if (absorbedFuel && (cell.FuelMass <= 0 || source.FuelCapacity <= 0)) return;
    uint sourceMaterialIndex = absorbedFuel ? RetainedLiquidIndex(cell,source) : cell.MaterialIndex;
    if(absorbedFuel) {
        source=Materials[sourceMaterialIndex];
        // Residue-producing pore reactions need a separate ledger; never erase their stock.
        if(source.BurnedIntoMaterialIndex != 0) return;
    }
    uint targetIndex = source.BurnedIntoMaterialIndex;

    // Liquid fuel reacts only at a gas face, including in Sandbox. Buried or
    // submerged hot liquid must not consume fuel through a liquid/solid cover.
    // Flame remains a transient gas with its own lifecycle.
    bool liquidFuel = source.SimulationKind == SimulationKindLiquid;
    bool combustibleKind =
        source.SimulationKind == SimulationKindSolid ||
        source.SimulationKind == SimulationKindGranular || liquidFuel ||
        source.SimulationKind == SimulationKindGas;
    if (!combustibleKind ||
        targetIndex == 0xffffffffu || targetIndex >= CombustionMaterialCount)
    {
        return;
    }

    MaterialProperties target = Materials[targetIndex];
    float residueMass = ResidueMass(target, targetIndex);
    float availableFuel = absorbedFuel ? cell.FuelMass : max(0, cell.Mass - residueMass);
    if (availableFuel <= (absorbedFuel ? 0 : CombustionMassEpsilon) || CombustionDeltaTime <= 0)
    {
        return;
    }

    bool selfOxidizing = (source.Flags & MaterialFlagSelfOxidizing) != 0;
    bool needsOxidizer = FiniteOxidizer != 0 && !selfOxidizing;
    bool persistentIgnition = (source.Flags & MaterialFlagPersistentCoalIgnition) != 0;
    bool separateContact = source.ContactIgnitionTemperature > MinimumCombustionTemperature;
    bool retainFuelIgnition = separateContact && !absorbedFuel &&
        (liquidFuel || source.SimulationKind == SimulationKindGas);
    if (retainFuelIgnition && cell.Temperature < source.ContactIgnitionTemperature &&
        (cell.BodyId & FuelBurningMarker) != 0)
    {
        cell.BodyId &= ~FuelBurningMarker;
        Grid[index] = cell;
    }
    float2 supply = needsOxidizer || persistentIgnition || liquidFuel
        ? AvailableOxidizer(coordinate) : float2(4, 4);
    if (liquidFuel && supply.y < 0.5)
    {
        if (retainFuelIgnition && (cell.BodyId & FuelBurningMarker) != 0)
        { cell.BodyId &= ~FuelBurningMarker; Grid[index] = cell; }
        return;
    }
    // Warm coal is not automatically burning throughout a closed mound.
    // Start its reaction on a gas face; retain an existing Sandbox latch even
    // if a falling grain buries it, like TPT's already-running coal life.
    if (persistentIgnition && cell.Lifetime <= 0 && supply.y < 0.5) return;
    float oxygen = supply.x;
    // Gate ignition as well as fuel loss/heat/emissions. A flame in inert gas
    // must not ignite an otherwise cold piece of wood for one frame.
    if (needsOxidizer && oxygen <= max(supply.y, 1) * OxidizerExtinctionThreshold)
    {
        if (persistentIgnition && cell.Lifetime != 0) { cell.Lifetime = 0; Grid[index] = cell; }
        if (retainFuelIgnition && (cell.BodyId & FuelBurningMarker) != 0)
        { cell.BodyId &= ~FuelBurningMarker; Grid[index] = cell; }
        return;
    }

    bool progressiveIgnition = !absorbedFuel && (source.Flags & MaterialFlagProgressiveIgnition) != 0;
    if (progressiveIgnition && cell.Lifetime >= 1 / source.FlameSpreadRate &&
        cell.Temperature <= source.ContactIgnitionTemperature)
    {
        cell.Lifetime = 0;
        Grid[index] = cell;
    }
    bool contactIgnited = progressiveIgnition &&
        (cell.Lifetime > 0 || HasMatureIgnitionNeighbor(coordinate));
    uint flameContacts = cell.Temperature <= source.IgnitionTemperature && source.FlameSpreadRate > 0 &&
        (!separateContact || cell.Temperature >= source.ContactIgnitionTemperature)
        ? LiveFlameCount(coordinate, progressiveIgnition) : 0;
    if (flameContacts > 0)
    {
        // TPT checks coal ignition from every nearby FIRE particle, rather
        // than giving one particle and a dense burning front the same chance.
        // Keep the existing contact rate for other fuels.
        float contacts = persistentIgnition ? float(flameContacts) : 1.0;
        float ignitionChance = 1.0 - exp(-source.FlameSpreadRate * contacts * CombustionDeltaTime);
        uint ignitionSeed = index ^ (CombustionTickIndex * 0x9e3779b9u);
        if (HashUnitFloat(ignitionSeed) < ignitionChance)
        {
            if (separateContact) contactIgnited = true;
            else cell.Temperature = min(MaximumCombustionTemperature, source.IgnitionTemperature + 1.0);
        }
    }
    if (cell.Temperature <= source.IgnitionTemperature &&
        !(persistentIgnition && cell.Lifetime > 0) && !contactIgnited &&
        !(retainFuelIgnition && (cell.BodyId & FuelBurningMarker) != 0)) return;
    if (persistentIgnition) cell.Lifetime = 1;
    if (retainFuelIgnition) cell.BodyId |= FuelBurningMarker;

    // Only open gas faces count. A fuel surface with one fresh-air face must
    // burn at the same concentration as one with four, not at one third rate.
    float exposure = needsOxidizer ? saturate(oxygen / max(supply.y, 1)) : 1;
    float burnedMass = min(availableFuel, source.BurnRate * exposure * CombustionDeltaTime);
    if(absorbedFuel)
    {
        // Stored liquid is a separate fuel ledger. Bound heat before reacting;
        // neither the dry grain nor capped-away reaction heat is consumed.
        float rise=max(0,source.MaximumCombustionTemperature-cell.Temperature);
        burnedMass=min(burnedMass,rise*CellEffectiveCapacity(cell)/max(.0001,source.HeatPerMass+rise*source.HeatCapacity));
    }
    if (needsOxidizer)
    {
        // A donor has at most four fuel neighbours and one flame. Reserving
        // 1/5 per consumer prevents overdraw without float atomics or races.
        float oxidizerPerMass = Emissions[sourceMaterialIndex].OxidizerPerMass;
        if (oxidizerPerMass <= 0) oxidizerPerMass = OxidizerPerFuelMass;
        float reserved = OxidizerDemand[index];
        burnedMass = min(burnedMass, max(0, oxygen / 5 - reserved) / oxidizerPerMass);
        OxidizerDemand[index] = reserved + burnedMass * oxidizerPerMass;
    }
    if (burnedMass <= 0) return;
    if (progressiveIgnition) cell.Lifetime += CombustionDeltaTime;

    if(absorbedFuel)
    {
        cell.FuelMass=max(0,cell.FuelMass-burnedMass);
        if(cell.FuelMass==0)cell.RetainedLiquidMaterialIndex=0;
        cell.Temperature+=burnedMass*source.HeatPerMass/CellEffectiveCapacity(cell);
        cell.RestFrames=0;
        Grid[index]=cell;
        InterlockedOr(CombustionSummary[0],CombustionOccurred | TargetCellular);
        ProposeEmissions(index,sourceMaterialIndex,CombustionWidth,CombustionHeight,cell,
            saturate(burnedMass/max(source.BurnRate*CombustionDeltaTime,.0000001)));
        return;
    }

    float capacity = max(0.01, CellEffectiveCapacity(cell));
    float generatedRise = burnedMass * source.HeatPerMass / capacity;
    float permittedRise = max(0.0, source.MaximumCombustionTemperature - cell.Temperature);
    if (CombustionHasReactionSources != 0 && source.ReactionPressurePerMass > 0)
    {
        // Partition reaction heat before the fuel disappears. The released
        // portion remains pending until this location has a visible gas node.
        // It cannot vanish just because a smoke/flame spawn lost its claim.
        float remainingCapacity = max(0, CellEffectiveCapacity(cell) - burnedMass * source.HeatCapacity);
        float rise = remainingCapacity > 0 ? min(permittedRise,
            burnedMass * source.HeatPerMass / remainingCapacity) : 0;
        float releasedCapacity = burnedMass * source.HeatCapacity;
        float releasedEnergy = releasedCapacity * (cell.Temperature + 273.15) +
            burnedMass * source.HeatPerMass - remainingCapacity * rise;
        // Exceptional pre-heated fuel may carry more than the supported gas
        // temperature. Preserve energy using additional gameplay capacity.
        releasedCapacity = max(releasedCapacity, releasedEnergy / 5273.15);
        ReactionPending[index] += float4(burnedMass * source.ReactionPressurePerMass,
            releasedEnergy, releasedCapacity, 0);
        generatedRise = rise;
    }
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
        bool releaseLiquid = targetIndex == 0 && cell.FuelMass > 0;
        if (releaseLiquid)
        {
            // Release the actual stock in this same cell, without a neighbour
            // claim or any lost liquid. This also recovers old retained metals.
            uint retained = RetainedLiquidIndex(cell, source);
            float retainedEnergy = Materials[retained].HeatCapacity * cell.Temperature;
            float retainedMass = cell.FuelMass;
            cell = (GridCell)0;
            cell.IsActive = 1;
            cell.MaterialIndex = retained;
            cell.Mass = retainedMass;
            cell = SetCellSpecificEnthalpy(cell, retainedEnergy);
            flags |= TargetLiquid | TargetCellular;
        }
        else NormalizeBurnout(cell, source, target, targetIndex);

        // Fuel that leaves no residue becomes a flame in its own cell instead
        // of vanishing. This is what The Powder Toy does: COAL turns into
        // PT_FIRE when its life runs out, and an ignited GUNP grain is
        // converted straight to PT_FIRE rather than being deleted.
        // Zeroing the cell threw away all the heat the reaction had just
        // produced, so a chain reaction died wherever there was no adjacent
        // empty cell for a flame to be emitted into: scattered grains lying on
        // the ground would heat up, consume themselves and quietly disappear
        // without ever igniting their neighbours.
        if (targetIndex == 0 && !releaseLiquid)
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
                        index ^ (CombustionTickIndex * 0x9e3779b9u)) * source.ReactionFlameLifetimeMultiplier;
                    cell.RestFrames = 0;
                    cell.BodyId = selfOxidizing ? SelfOxidizingFlameMarker : (FiniteOxidizer != 0 ? ReactedFuelFlameMarker : 0);
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
    float reactionFraction = FiniteOxidizer != 0
        ? saturate(burnedMass / max(source.BurnRate * CombustionDeltaTime, 0.0000001)) : 1;
    ProposeEmissions(index, sourceMaterialIndex, CombustionWidth, CombustionHeight, cell, reactionFraction);
}

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    // Oil supplements the dry host's reaction. Selecting the oil material as
    // the ONLY reaction switched off a latched coal as soon as the first drop
    // entered its pores, including below oil's contact-ignition threshold.
    ReactFuel(dispatchThreadId.xy, false);
    ReactFuel(dispatchThreadId.xy, true);
}
