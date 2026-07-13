using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

public static class CriticalRegressionVerifier
{
    public static bool Validate(SimulationWorldSnapshot snapshot, out string report)
    {
        ReadOnlySpan<GridCell> grid = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        ReadOnlySpan<LatticeParticle> particles = MemoryMarshal.Cast<byte, LatticeParticle>(snapshot.Particles);
        ReadOnlySpan<LatticeBond> bonds = MemoryMarshal.Cast<byte, LatticeBond>(snapshot.Bonds);
        int metalCount = 0;
        int metalBonds = 0;
        double metalY = 0;
        float metalMinimumX = float.MaxValue;
        float metalMaximumX = 0;
        int concreteCount = 0;
        int concreteDynamicCount = 0;
        int concreteBonds = 0;
        double concreteY = 0;
        int activeParticles = 0;
        int activeBonds = 0;
        foreach (int index in EnumerateIndices(particles.Length))
        {
            LatticeParticle particle = particles[index];
            if (particle.IsActive == 0)
            {
                continue;
            }

            int bondCount = BitOperations.PopCount(bonds[index].ActiveNeighborMask);
            activeParticles++;
            activeBonds += bondCount;
            if (particle.BodyId == CriticalRegressionScenario.MetalBodyId)
            {
                metalCount++;
                metalBonds += bondCount;
                metalY += particle.PositionY;
                metalMinimumX = Math.Min(metalMinimumX, particle.PositionX);
                metalMaximumX = Math.Max(metalMaximumX, particle.PositionX);
            }
            else if (particle.BodyId == CriticalRegressionScenario.ConcreteBodyId)
            {
                concreteCount++;
                concreteBonds += bondCount;
                concreteY += particle.PositionY;
                concreteDynamicCount += particle.IsDynamic == 0 ? 0 : 1;
            }
        }

        int visibleLattice = 0;
        foreach (GridCell cell in grid)
        {
            if (cell.IsActive != 0 && cell.LatticeParticleIndex < particles.Length &&
                particles[(int)cell.LatticeParticleIndex].IsActive != 0)
            {
                visibleLattice++;
            }
        }

        double averageMetalY = metalCount == 0 ? snapshot.Height : metalY / metalCount;
        double averageConcreteY = concreteCount == 0 ? 0 : concreteY / concreteCount;
        float projectionRatio = activeParticles == 0 ? 0 : visibleLattice / (float)activeParticles;
        bool metalLineStable = metalCount >= 95 && metalMaximumX - metalMinimumX >= 95 &&
            Math.Abs(averageMetalY - (snapshot.Height / 5f + 0.5f)) < 0.75 && metalBonds <= 800;
        bool exactTopology = activeBonds > 0 && activeBonds <= activeParticles * 4;
        bool concreteMovesAsBody = concreteCount > 80 && concreteDynamicCount == concreteCount &&
            averageConcreteY > snapshot.Height * 0.48 && concreteBonds > concreteCount / 2;
        bool visibleWorld = projectionRatio > 0.82f;
        report = $"PHYXEL_CRITICAL_METRICS metalNodes={metalCount} metalBonds={metalBonds} metalY={averageMetalY:0.00} concreteNodes={concreteCount} concreteDynamic={concreteDynamicCount} concreteY={averageConcreteY:0.0} totalBonds={activeBonds} projection={projectionRatio:0.000}";
        return metalLineStable && exactTopology && concreteMovesAsBody && visibleWorld;
    }

    private static System.Collections.Generic.IEnumerable<int> EnumerateIndices(int count)
    {
        for (int index = 0; index < count; index++)
        {
            yield return index;
        }
    }
}
