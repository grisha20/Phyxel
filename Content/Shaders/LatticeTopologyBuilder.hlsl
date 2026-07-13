#include "PhysicsShared.hlsli"

StructuredBuffer<uint> ActivatedBodyWords : register(t0);
StructuredBuffer<MaterialProperties> Materials : register(t1);
RWStructuredBuffer<LatticeParticle> Particles : register(u0);
RWStructuredBuffer<LatticeBond> Bonds : register(u1);

static const int2 NeighborOffsets[8] =
{
    int2(-1, -1), int2(0, -1), int2(1, -1), int2(-1, 0),
    int2(1, 0), int2(-1, 1), int2(0, 1), int2(1, 1)
};

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy + uint2(DispatchOffsetX, DispatchOffsetY);
    if (coordinate.x >= Width || coordinate.y >= Height)
    {
        return;
    }

    uint index = FlattenCoordinate(coordinate);
    LatticeParticle particle = Particles[index];
    if (particle.IsActive == 0)
    {
        Particles[index] = particle;
        Bonds[index] = (LatticeBond)0;
        return;
    }

    uint activeMask = 0;
    for (uint neighbor = 4; neighbor < 8; neighbor++)
    {
        int2 neighborCoordinate = int2(coordinate) + NeighborOffsets[neighbor];
        if (neighborCoordinate.x < 0 || neighborCoordinate.y < 0 || neighborCoordinate.x >= int(Width) || neighborCoordinate.y >= int(Height))
        {
            continue;
        }

        LatticeParticle adjacent = Particles[FlattenCoordinate(uint2(neighborCoordinate))];
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

    LatticeBond bond = Bonds[index];
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

    Particles[index] = particle;
    Bonds[index] = bond;
}
