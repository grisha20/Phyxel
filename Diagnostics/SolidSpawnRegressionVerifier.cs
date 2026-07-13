using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

public static class SolidSpawnRegressionVerifier
{
    public static bool Validate(SimulationWorldSnapshot snapshot, out string report)
    {
        ReadOnlySpan<LatticeParticle> particles = MemoryMarshal.Cast<byte, LatticeParticle>(snapshot.Particles);
        ReadOnlySpan<LatticeBond> bonds = MemoryMarshal.Cast<byte, LatticeBond>(snapshot.Bonds);
        ReadOnlySpan<GridCell> grid = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        Dictionary<uint, int> counts = [];
        Dictionary<uint, int> bondCounts = [];
        int activeParticles = 0;
        int activeBonds = 0;
        for (int index = 0; index < particles.Length; index++)
        {
            LatticeParticle particle = particles[index];
            if (particle.IsActive == 0)
            {
                continue;
            }

            int particleBonds = BitOperations.PopCount(bonds[index].ActiveNeighborMask);
            counts[particle.BodyId] = counts.GetValueOrDefault(particle.BodyId) + 1;
            bondCounts[particle.BodyId] = bondCounts.GetValueOrDefault(particle.BodyId) + particleBonds;
            activeParticles++;
            activeBonds += particleBonds;
        }

        bool linesExact = ValidateBody(counts, bondCounts, 201, 10) &&
            ValidateBody(counts, bondCounts, 202, 50) &&
            ValidateBody(counts, bondCounts, 203, 100) &&
            ValidateBody(counts, bondCounts, 204, 200);
        bool multipleStrokes = true;
        for (uint bodyId = 210; bodyId < 220; bodyId++)
        {
            multipleStrokes &= ValidateBody(counts, bondCounts, bodyId, 20);
        }

        bool brushSizes = counts.GetValueOrDefault(206u) is >= 19 and <= 23 &&
            counts.GetValueOrDefault(207u) is >= 310 and <= 325;
        int diskParticles = counts.GetValueOrDefault(205u);
        bool solidDisk = diskParticles is >= 1940 and <= 1980 && HasFilledDisk(grid, particles, snapshot.Width, 300, 72, 23, 205);
        bool linearTopology = activeBonds <= activeParticles * 4;
        report = $"PHYXEL_SOLID_SPAWN_METRICS line10={counts.GetValueOrDefault(201u)}/{bondCounts.GetValueOrDefault(201u)} line50={counts.GetValueOrDefault(202u)}/{bondCounts.GetValueOrDefault(202u)} line100={counts.GetValueOrDefault(203u)}/{bondCounts.GetValueOrDefault(203u)} line200={counts.GetValueOrDefault(204u)}/{bondCounts.GetValueOrDefault(204u)} disk50={diskParticles} particles={activeParticles} bonds={activeBonds}";
        return linesExact && multipleStrokes && brushSizes && solidDisk && linearTopology;
    }

    private static bool ValidateBody(
        IReadOnlyDictionary<uint, int> counts,
        IReadOnlyDictionary<uint, int> bonds,
        uint bodyId,
        int expectedCount)
    {
        int count = counts.GetValueOrDefault(bodyId);
        int bondCount = bonds.GetValueOrDefault(bodyId);
        return count == expectedCount && bondCount <= expectedCount * 8 && bondCount >= expectedCount - 1;
    }

    private static bool HasFilledDisk(
        ReadOnlySpan<GridCell> grid,
        ReadOnlySpan<LatticeParticle> particles,
        int width,
        int centerX,
        int centerY,
        int radius,
        uint bodyId)
    {
        for (int y = centerY - radius; y <= centerY + radius; y++)
        {
            for (int x = centerX - radius; x <= centerX + radius; x++)
            {
                if ((x - centerX) * (x - centerX) + (y - centerY) * (y - centerY) > radius * radius)
                {
                    continue;
                }

                GridCell cell = grid[y * width + x];
                if (cell.IsActive == 0 || cell.LatticeParticleIndex >= particles.Length ||
                    particles[(int)cell.LatticeParticleIndex].BodyId != bodyId)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
