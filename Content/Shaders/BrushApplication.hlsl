#include "PhysicsShared.hlsli"

StructuredBuffer<BrushDrawCommand> Commands : register(t0);
StructuredBuffer<MaterialProperties> Materials : register(t1);
RWStructuredBuffer<GridCell> Grid : register(u0);
RWStructuredBuffer<LatticeParticle> Particles : register(u1);
RWStructuredBuffer<LatticeBond> Bonds : register(u2);
RWStructuredBuffer<uint> ActivatedBodyWords : register(u3);

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint commandIndex = dispatchThreadId.z;
    if (commandIndex >= CommandCount || dispatchThreadId.x >= MaximumBrushDiameter || dispatchThreadId.y >= MaximumBrushDiameter)
    {
        return;
    }

    BrushDrawCommand command = Commands[commandIndex];
    int halfDiameter = int(MaximumBrushDiameter / 2);
    int2 offset = int2(dispatchThreadId.xy) - int2(halfDiameter, halfDiameter);
    if (dot(float2(offset), float2(offset)) > command.Radius * command.Radius)
    {
        return;
    }

    int2 position = int2(command.X, command.Y) + offset;
    if (position.x < 0 || position.y < 0 || position.x >= int(Width) || position.y >= int(Height))
    {
        return;
    }

    uint index = FlattenCoordinate(uint2(position));
    if (command.Mode == 0 && HashUnitFloat(index ^ command.Seed ^ FrameIndex) > command.Density)
    {
        return;
    }

    if (command.Mode != 0 || command.MaterialId == 5)
    {
        LatticeParticle sourceParticle = Particles[index];
        if (sourceParticle.IsActive != 0 && sourceParticle.BodyId != 0)
        {
            uint wordIndex = sourceParticle.BodyId >> 5;
            uint bodyBit = 1u << (sourceParticle.BodyId & 31);
            uint ignoredWord;
            InterlockedOr(ActivatedBodyWords[wordIndex], bodyBit, ignoredWord);
        }

        GridCell emptyCell = (GridCell)0;
        LatticeParticle emptyParticle = (LatticeParticle)0;
        LatticeBond emptyBond = (LatticeBond)0;
        Grid[index] = emptyCell;
        Particles[index] = emptyParticle;
        Bonds[index] = emptyBond;
        return;
    }

    MaterialProperties material = Materials[command.MaterialId];
    GridCell cell = (GridCell)0;
    cell.MaterialId = command.MaterialId;
    cell.Mass = material.SimulationKind == 2 ? material.Density : 1;
    cell.IsActive = 1;
    cell.LatticeParticleIndex = index;
    Grid[index] = cell;

    if (material.SimulationKind == 2)
    {
        LatticeParticle particle = (LatticeParticle)0;
        particle.PositionX = position.x + 0.5;
        particle.PositionY = position.y + 0.5;
        particle.Mass = material.Density;
        particle.MaterialId = command.MaterialId;
        particle.IsActive = 1;
        particle.BodyId = command.Reserved;
        particle.IsDynamic = 0;
        Particles[index] = particle;

        LatticeBond bond = (LatticeBond)0;
        bond.ParticleA = index;
        bond.ActiveNeighborMask = 0x80000000;
        bond.CardinalRestLength = 1;
        bond.DiagonalRestLength = 1.41421356;
        bond.ElasticLimit = material.ElasticLimit;
        bond.PlasticLimit = material.PlasticLimit;
        Bonds[index] = bond;
        return;
    }

    Particles[index] = (LatticeParticle)0;
    Bonds[index] = (LatticeBond)0;
}
