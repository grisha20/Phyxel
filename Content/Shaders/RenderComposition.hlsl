#include "PhysicsShared.hlsli"

StructuredBuffer<GridCell> Grid : register(t0);
StructuredBuffer<LatticeParticle> Particles : register(t1);
StructuredBuffer<LatticeBond> Bonds : register(t2);
StructuredBuffer<MaterialProperties> Materials : register(t3);
RWTexture2D<unorm float4> OutputTexture : register(u0);
RWStructuredBuffer<SimulationStatistics> Statistics : register(u1);

float4 MaterialColor(uint materialId)
{
    if (materialId == 1) return float4(0.855, 0.722, 0.361, 1);
    if (materialId == 2) return float4(0.169, 0.518, 0.812, 0.92);
    if (materialId == 3) return float4(0.557, 0.612, 0.651, 1);
    if (materialId == 4) return float4(0.49, 0.475, 0.439, 1);
    if (materialId == 6) return float4(0.608, 0.769, 0.824, 0.58);
    return float4(0.035, 0.041, 0.047, 1);
}

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= Width || dispatchThreadId.y >= Height)
    {
        return;
    }

    uint index = FlattenCoordinate(dispatchThreadId.xy);
    GridCell cell = Grid[index];
    float4 color = float4(0.035, 0.041, 0.047, 1);
    if (cell.IsActive != 0)
    {
        uint kind = Materials[cell.MaterialId].SimulationKind;
        color = MaterialColor(cell.MaterialId);
        if (IsFluidMaterial(kind))
        {
            float fill = saturate(cell.Mass);
            color.rgb = lerp(float3(0.035, 0.041, 0.047), color.rgb, fill);
            color.a = lerp(0.3, color.a, fill);
        }

        if (kind == 2)
        {
            uint particleIndex = min(cell.LatticeParticleIndex, ParticleCount - 1);
            LatticeParticle particle = Particles[particleIndex];
            LatticeBond bond = Bonds[particleIndex];
            float stress = max(particle.Stress, bond.MaximumStrain);
            if (StressView != 0)
            {
                color.rgb = lerp(color.rgb, float3(1, 0.08, 0.03), saturate(stress * 8));
            }

            uint ignoredParticleCount;
            uint ignoredBondCount;
            uint ignoredStressSum;
            uint ignoredLoadSum;
            uint ignoredSampleCount;
            InterlockedAdd(Statistics[0].ActiveParticles, 1, ignoredParticleCount);
            InterlockedAdd(Statistics[0].ActiveBonds, countbits(bond.ActiveNeighborMask), ignoredBondCount);
            InterlockedAdd(Statistics[0].StressSumMilli, uint(saturate(stress) * 1000), ignoredStressSum);
            InterlockedAdd(Statistics[0].LoadSumMilli, uint(min(bond.AccumulatedLoad, 100) * 10), ignoredLoadSum);
            InterlockedAdd(Statistics[0].StressSampleCount, 1, ignoredSampleCount);
        }
        else
        {
            uint ignoredCellCount;
            InterlockedAdd(Statistics[0].ActiveCells, 1, ignoredCellCount);
        }
    }

    if (index == 0)
    {
        Statistics[0].FrameIndex = FrameIndex;
    }

    OutputTexture[dispatchThreadId.xy] = color;
}
