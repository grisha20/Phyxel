#include "PhysicsShared.hlsli"

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
RWStructuredBuffer<GridCell> Grid : register(u0);
RWStructuredBuffer<uint> CombustionSummary : register(u1);
RWStructuredBuffer<uint> EmissionClaims : register(u2);
RWStructuredBuffer<EmissionRequest> EmissionRequests : register(u3);

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
    if (IsCellularMaterial(target.SimulationKind)) flags |= TargetCellular;
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
    float temperature)
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
    request.SourceIndex = sourceIndex;
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
            emission.SmokeRate, CombustionDeltaTime, sourceIndex, sourceCell.Temperature);
    }
    if (x + 1 < width)
    {
        ProposeEmission(sourceIndex, sourceIndex + 1, emission.GasIntoMaterialIndex,
            emission.GasRate, CombustionDeltaTime, worldCellCount + sourceIndex, sourceCell.Temperature);
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
                max(sourceCell.Temperature, Materials[emission.FlameIntoMaterialIndex].InitialTemperature));
        }
    }
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
    if (source.SimulationKind != SimulationKindSolid ||
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

    if (cell.Temperature <= source.IgnitionTemperature && source.FlameSpreadRate > 0 &&
        HasLiveFlame(coordinate))
    {
        float ignitionChance = saturate(source.FlameSpreadRate * CombustionDeltaTime);
        uint ignitionSeed = index ^ (CombustionTickIndex * 0x9e3779b9u);
        if (HashUnitFloat(ignitionSeed) < ignitionChance)
        {
            cell.Temperature = min(MaximumCombustionTemperature, source.IgnitionTemperature + 1.0);
        }
    }
    if (cell.Temperature <= source.IgnitionTemperature)
    {
        return;
    }

    float burnedMass = min(availableFuel, source.BurnRate * CombustionDeltaTime);
    if (burnedMass <= 0)
    {
        return;
    }

    float capacityMass = max(cell.Mass, source.Density);
    float capacity = max(0.01, source.HeatCapacity * capacityMass);
    float generatedRise = burnedMass * source.HeatPerMass / capacity;
    float permittedRise = max(0.0, source.MaximumCombustionTemperature - cell.Temperature);
    cell.Temperature = clamp(
        cell.Temperature + min(generatedRise, permittedRise),
        MinimumCombustionTemperature,
        MaximumCombustionTemperature);
    cell.Mass = max(residueMass, cell.Mass - burnedMass);
    uint flags = CombustionOccurred | TargetFlags(source, target);
    if (availableFuel - burnedMass <= CombustionMassEpsilon)
    {
        cell.Mass = residueMass;
        flags |= BurnoutOccurred;
        NormalizeBurnout(cell, source, target, targetIndex);
    }
    Grid[index] = cell;
    InterlockedOr(CombustionSummary[0], flags);
    ProposeEmissions(index, sourceMaterialIndex, CombustionWidth, CombustionHeight, cell);
}
