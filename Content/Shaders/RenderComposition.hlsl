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
    if (cell.IsActive != 0 && Materials[cell.MaterialId].SimulationKind == 1)
    {
        color = MaterialColor(cell.MaterialId);
        uint ignoredCellCount;
        InterlockedAdd(Statistics[0].ActiveCells, 1, ignoredCellCount);
    }

    float highestStress = 0;
    uint rigidMaterial = 0;
    for (int offsetY = -2; offsetY <= 2; offsetY++)
    {
        for (int offsetX = -2; offsetX <= 2; offsetX++)
        {
            int2 sourceCoordinate = int2(dispatchThreadId.xy) + int2(offsetX, offsetY);
            if (sourceCoordinate.x < 0 || sourceCoordinate.y < 0 || sourceCoordinate.x >= int(Width) || sourceCoordinate.y >= int(Height))
            {
                continue;
            }

            LatticeParticle particle = Particles[FlattenCoordinate(uint2(sourceCoordinate))];
            if (particle.IsActive != 0 && all(uint2(particle.PositionX, particle.PositionY) == dispatchThreadId.xy))
            {
                rigidMaterial = particle.MaterialId;
                highestStress = max(highestStress, particle.Stress);
            }
        }
    }

    if (rigidMaterial != 0)
    {
        color = MaterialColor(rigidMaterial);
    }

    if (StressView != 0 && highestStress > 0)
    {
        float intensity = saturate(highestStress * 8);
        color.rgb = lerp(color.rgb, float3(1, 0.08, 0.03), intensity);
    }

    LatticeParticle indexedParticle = Particles[index];
    if (indexedParticle.IsActive != 0)
    {
        uint ignoredParticleCount;
        InterlockedAdd(Statistics[0].ActiveParticles, 1, ignoredParticleCount);
    }

    LatticeBond bond = Bonds[index];
    if (bond.IsActive != 0)
    {
        uint ignoredBondCount;
        InterlockedAdd(Statistics[0].ActiveBonds, 1, ignoredBondCount);
    }

    if (index == 0)
    {
        Statistics[0].FrameIndex = FrameIndex;
    }

    OutputTexture[dispatchThreadId.xy] = color;
}
