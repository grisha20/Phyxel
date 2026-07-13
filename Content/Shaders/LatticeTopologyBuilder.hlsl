#include "PhysicsShared.hlsli"

StructuredBuffer<LatticeParticle> SourceParticles : register(t0);
StructuredBuffer<LatticeBond> SourceBonds : register(t1);
StructuredBuffer<uint> ActivatedBodyWords : register(t2);
StructuredBuffer<MaterialProperties> Materials : register(t3);
RWStructuredBuffer<LatticeParticle> DestinationParticles : register(u0);
RWStructuredBuffer<LatticeBond> DestinationBonds : register(u1);

static const int2 NeighborOffsets[8] =
{
    int2(-1, -1), int2(0, -1), int2(1, -1), int2(-1, 0),
    int2(1, 0), int2(-1, 1), int2(0, 1), int2(1, 1)
};

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= Width || dispatchThreadId.y >= Height)
    {
        return;
    }

    uint index = FlattenCoordinate(dispatchThreadId.xy);
    LatticeParticle particle = SourceParticles[index];
    if (particle.IsActive == 0)
    {
        DestinationParticles[index] = particle;
        DestinationBonds[index] = (LatticeBond)0;
        return;
    }

    uint activeMask = 0;
    for (uint neighbor = 4; neighbor < 8; neighbor++)
    {
        int2 coordinate = int2(dispatchThreadId.xy) + NeighborOffsets[neighbor];
        if (coordinate.x < 0 || coordinate.y < 0 || coordinate.x >= int(Width) || coordinate.y >= int(Height))
        {
            continue;
        }

        LatticeParticle adjacent = SourceParticles[FlattenCoordinate(uint2(coordinate))];
        if (adjacent.IsActive != 0 && adjacent.BodyId == particle.BodyId && adjacent.MaterialId == particle.MaterialId)
        {
            activeMask |= 1u << neighbor;
        }
    }

    bool activated = false;
    if (particle.BodyId != 0)
    {
        uint bodyWord = ActivatedBodyWords[particle.BodyId >> 5];
        activated = ((bodyWord >> (particle.BodyId & 31)) & 1) != 0;
        particle.IsDynamic |= activated ? 1 : 0;
    }

    LatticeBond bond = SourceBonds[index];
    MaterialProperties material = Materials[particle.MaterialId];
    bool newParticle = (bond.ActiveNeighborMask & 0x80000000) != 0;
    bond.ParticleA = index;
    bond.ActiveNeighborMask = newParticle || particle.IsDynamic == 0 || activated
        ? activeMask
        : bond.ActiveNeighborMask & activeMask;
    if (newParticle || bond.CardinalRestLength <= 0)
    {
        bond.ActiveNeighborMask = activeMask;
        bond.CardinalRestLength = 1;
        bond.DiagonalRestLength = 1.41421356;
        bond.ElasticLimit = material.ElasticLimit;
        bond.PlasticLimit = material.PlasticLimit;
    }

    DestinationParticles[index] = particle;
    DestinationBonds[index] = bond;
}
