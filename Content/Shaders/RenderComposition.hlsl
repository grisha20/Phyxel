#include "PhysicsShared.hlsli"

StructuredBuffer<GridCell> Grid : register(t0);
StructuredBuffer<LatticeParticle> Particles : register(t1);
StructuredBuffer<LatticeBond> Bonds : register(t2);
StructuredBuffer<MaterialProperties> Materials : register(t3);
RWTexture2D<unorm float4> OutputTexture : register(u0);
RWStructuredBuffer<SimulationStatistics> Statistics : register(u1);
groupshared uint GroupActiveCells;
groupshared uint GroupSleepingCells;

float4 MaterialColor(uint materialId)
{
    if (materialId == 1) return float4(0.855, 0.722, 0.361, 1);
    if (materialId == 2) return float4(0.169, 0.518, 0.812, 0.92);
    if (materialId == 3) return float4(0.557, 0.612, 0.651, 1);
    if (materialId == 4) return float4(0.49, 0.475, 0.439, 1);
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
            int2 sampleCoordinate = int2(coordinate) + int2(x, y);
            if (sampleCoordinate.x < 0 || sampleCoordinate.y < 0 ||
                sampleCoordinate.x >= int(Width) || sampleCoordinate.y >= int(Height))
            {
                continue;
            }

            float sampleWeight = x == 0 && y == 0 ? 4 : x == 0 || y == 0 ? 2 : 1;
            GridCell sampleCell = Grid[FlattenCoordinate(uint2(sampleCoordinate))];
            if (sampleCell.IsActive != 0 && sampleCell.MaterialId == 2)
            {
                coverage += saturate(sampleCell.Mass) * sampleWeight;
            }
            weight += sampleWeight;
        }
    }

    return coverage / max(weight, 1);
}

void ComposePixel(uint2 coordinate, bool collectStatistics)
{
    uint index = FlattenCoordinate(coordinate);
    GridCell cell = Grid[index];
    bool exactLatticeCell = cell.IsActive != 0;
    float visualWater = cell.IsActive == 0 ? WaterCoverage(coordinate) : 0;
    if (cell.IsActive == 0 && visualWater <= 0.02)
    {
        for (int neighborY = -1; neighborY <= 1 && cell.IsActive == 0; neighborY++)
        {
            for (int neighborX = -1; neighborX <= 1; neighborX++)
            {
                int2 neighborCoordinate = int2(coordinate) + int2(neighborX, neighborY);
                if (neighborCoordinate.x < 0 || neighborCoordinate.y < 0 ||
                    neighborCoordinate.x >= int(Width) || neighborCoordinate.y >= int(Height))
                {
                    continue;
                }

                GridCell neighborCell = Grid[FlattenCoordinate(uint2(neighborCoordinate))];
                if (neighborCell.IsActive != 0 && Materials[neighborCell.MaterialId].SimulationKind == 2)
                {
                    cell = neighborCell;
                    exactLatticeCell = false;
                    break;
                }
            }
        }
    }
    float4 color = float4(0.035, 0.041, 0.047, 1);
    if (visualWater > 0.02)
    {
        float fill = saturate(visualWater * 2.5);
        color = MaterialColor(2);
        color.rgb = lerp(float3(0.035, 0.041, 0.047), color.rgb, fill);
        color.a = lerp(0.3, color.a, fill);
    }
    else if (cell.IsActive != 0)
    {
        uint kind = Materials[cell.MaterialId].SimulationKind;
        color = MaterialColor(cell.MaterialId);
        if (IsFluidMaterial(kind))
        {
            float fill = saturate(cell.Mass);
            if (kind == 4)
            {
                fill = lerp(fill, WaterCoverage(coordinate), 0.35);
            }
            color.rgb = lerp(float3(0.035, 0.041, 0.047), color.rgb, fill);
            color.a = lerp(0.3, color.a, fill);
        }

        if (kind == 2)
        {
            uint particleIndex = min(cell.LatticeParticleIndex, ParticleCount - 1);
            LatticeParticle particle = Particles[particleIndex];
            LatticeBond bond = Bonds[particleIndex];
            float stress = max(particle.Stress, bond.MaximumStrain);
            uint failureMode = Materials[cell.MaterialId].FailureMode;
            if (exactLatticeCell && failureMode != 0 && stress > bond.ElasticLimit)
            {
                float overload = saturate((stress - bond.ElasticLimit) /
                    max(bond.PlasticLimit - bond.ElasticLimit, 0.001));
                float overlay = failureMode == 1 ? 0.18 + overload * 0.5 : overload;
                color.rgb = lerp(color.rgb, float3(0.95, 0.025, 0.015), overlay);
            }
            if (StressView != 0 && exactLatticeCell)
            {
                color.rgb = lerp(color.rgb, float3(1, 0.08, 0.03), saturate(stress * 8));
            }

            if (collectStatistics && Materials[cell.MaterialId].FailureMode != 0)
            {
            uint ignoredParticleCount;
            uint ignoredBondCount;
            uint ignoredStressSum;
            uint ignoredLoadSum;
            uint ignoredSampleCount;
            uint ignoredMotionCount;
            uint ignoredCrackCount;
            uint ignoredBrokenCount;
            InterlockedAdd(Statistics[0].ActiveParticles, 1, ignoredParticleCount);
            InterlockedAdd(Statistics[0].ActiveBonds, countbits(bond.ActiveNeighborMask), ignoredBondCount);
            InterlockedAdd(Statistics[0].StressSumMilli, uint(saturate(stress) * 1000), ignoredStressSum);
            InterlockedAdd(Statistics[0].LoadSumMilli, uint(min(bond.AccumulatedLoad, 100) * 10), ignoredLoadSum);
            InterlockedAdd(Statistics[0].StressSampleCount, 1, ignoredSampleCount);
            float speed = abs(particle.VelocityX) + abs(particle.VelocityY);
            if (particle.IsDynamic != 0 && speed > 0.02)
            {
                InterlockedAdd(Statistics[0].MovingParticles, 1, ignoredMotionCount);
            }
            else
            {
                InterlockedAdd(Statistics[0].SleepingParticles, 1, ignoredMotionCount);
            }
            if (stress > bond.ElasticLimit)
            {
                InterlockedAdd(Statistics[0].CrackedParticles, 1, ignoredCrackCount);
            }
            if (Materials[cell.MaterialId].FailureMode == 2 &&
                stress >= bond.PlasticLimit && countbits(bond.ActiveNeighborMask) < 2)
            {
                InterlockedAdd(Statistics[0].BrokenParticles, 1, ignoredBrokenCount);
            }
            }
        }
        else
        {
            if (collectStatistics)
            {
            uint ignoredCellCount;
            InterlockedAdd(GroupActiveCells, 1, ignoredCellCount);
            uint restingThreshold = kind == 1 ? 30 : 240;
            if (cell.Reserved >= restingThreshold)
            {
                uint ignoredSleepingCount;
                InterlockedAdd(GroupSleepingCells, 1, ignoredSleepingCount);
            }
            }
        }
    }

    OutputTexture[coordinate] = color;
}

void CollectGroupStatistics(uint2 origin)
{
    uint activeCells = 0;
    uint sleepingCells = 0;
    uint activeParticles = 0;
    uint activeBonds = 0;
    uint stressSum = 0;
    uint loadSum = 0;
    uint stressSamples = 0;
    uint movingParticles = 0;
    uint sleepingParticles = 0;
    uint crackedParticles = 0;
    uint brokenParticles = 0;
    for (uint y = 0; y < 16 && origin.y + y < Height; y++)
    {
        for (uint x = 0; x < 16 && origin.x + x < Width; x++)
        {
            uint index = FlattenCoordinate(origin + uint2(x, y));
            GridCell cell = Grid[index];
            if (cell.IsActive == 0)
            {
                continue;
            }

            MaterialProperties material = Materials[cell.MaterialId];
            if (material.SimulationKind != 2)
            {
                activeCells++;
                uint threshold = material.SimulationKind == 1 ? 30 : 240;
                sleepingCells += cell.Reserved >= threshold ? 1 : 0;
                continue;
            }
            if (material.FailureMode == 0)
            {
                continue;
            }

            uint particleIndex = min(cell.LatticeParticleIndex, ParticleCount - 1);
            LatticeParticle particle = Particles[particleIndex];
            LatticeBond bond = Bonds[particleIndex];
            float stress = max(particle.Stress, bond.MaximumStrain);
            activeParticles++;
            activeBonds += countbits(bond.ActiveNeighborMask);
            stressSum += uint(saturate(stress) * 1000);
            loadSum += uint(min(bond.AccumulatedLoad, 100) * 10);
            stressSamples++;
            float speed = abs(particle.VelocityX) + abs(particle.VelocityY);
            movingParticles += particle.IsDynamic != 0 && speed > 0.02 ? 1 : 0;
            sleepingParticles += particle.IsDynamic == 0 || speed <= 0.02 ? 1 : 0;
            crackedParticles += stress > bond.ElasticLimit ? 1 : 0;
            brokenParticles += material.FailureMode == 2 && stress >= bond.PlasticLimit &&
                countbits(bond.ActiveNeighborMask) < 2 ? 1 : 0;
        }
    }

    uint ignored;
    if (activeCells > 0) InterlockedAdd(Statistics[0].ActiveCells, activeCells, ignored);
    if (sleepingCells > 0) InterlockedAdd(Statistics[0].Reserved, sleepingCells, ignored);
    if (activeParticles > 0) InterlockedAdd(Statistics[0].ActiveParticles, activeParticles, ignored);
    if (activeBonds > 0) InterlockedAdd(Statistics[0].ActiveBonds, activeBonds, ignored);
    if (stressSum > 0) InterlockedAdd(Statistics[0].StressSumMilli, stressSum, ignored);
    if (loadSum > 0) InterlockedAdd(Statistics[0].LoadSumMilli, loadSum, ignored);
    if (stressSamples > 0) InterlockedAdd(Statistics[0].StressSampleCount, stressSamples, ignored);
    if (movingParticles > 0) InterlockedAdd(Statistics[0].MovingParticles, movingParticles, ignored);
    if (sleepingParticles > 0) InterlockedAdd(Statistics[0].SleepingParticles, sleepingParticles, ignored);
    if (crackedParticles > 0) InterlockedAdd(Statistics[0].CrackedParticles, crackedParticles, ignored);
    if (brokenParticles > 0) InterlockedAdd(Statistics[0].BrokenParticles, brokenParticles, ignored);
}

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID, uint3 groupId : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    if (dispatchThreadId.x < Width && dispatchThreadId.y < Height)
    {
        ComposePixel(dispatchThreadId.xy, false);
    }

    if (SimulationPhase != 0 && groupIndex == 0)
    {
        CollectGroupStatistics(groupId.xy * 16);
        if (groupId.x == 0 && groupId.y == 0)
        {
            Statistics[0].FrameIndex = FrameIndex;
        }
    }
}
