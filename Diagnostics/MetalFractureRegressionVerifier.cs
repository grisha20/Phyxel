using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

public static class MetalFractureRegressionVerifier
{
    private const uint BodyId = 8401;

    public static bool Validate(SimulationWorldSnapshot snapshot, out string report)
    {
        ReadOnlySpan<LatticeParticle> particles = MemoryMarshal.Cast<byte, LatticeParticle>(snapshot.Particles);
        ReadOnlySpan<LatticeBond> bonds = MemoryMarshal.Cast<byte, LatticeBond>(snapshot.Bonds);
        int nodes = 0;
        int activeBonds = 0;
        int forced = 0;
        float maximumY = 0;
        for (int index = 0; index < particles.Length; index++)
        {
            LatticeParticle particle = particles[index];
            if (particle.IsActive == 0 || particle.BodyId != BodyId)
            {
                continue;
            }

            nodes++;
            activeBonds += BitOperations.PopCount(bonds[index].ActiveNeighborMask);
            forced += (particle.IsDynamic & 4) != 0 ? 1 : 0;
            maximumY = Math.Max(maximumY, particle.PositionY);
        }

        MeasureComponents(snapshot.Width, particles, bonds, out int components, out int largest, out int singletons);
        bool passed = nodes is >= 390 and <= 410 && activeBonds is >= 850 and < 996 &&
            forced is >= 50 and <= 250 && maximumY >= snapshot.Height - 2 &&
            components is >= 3 and <= 5 && largest >= 80 && singletons <= 2;
        report = $"PHYXEL_ACCEPTANCE_METAL_FRACTURE nodes={nodes} bonds={activeBonds} forced={forced} " +
            $"components={components} largest={largest} singletons={singletons} maximumY={maximumY:0.0}";
        return passed;
    }

    private static void MeasureComponents(
        int width,
        ReadOnlySpan<LatticeParticle> particles,
        ReadOnlySpan<LatticeBond> bonds,
        out int componentCount,
        out int largest,
        out int singletons)
    {
        bool[] visited = new bool[particles.Length];
        Stack<int> pending = new();
        componentCount = 0;
        largest = 0;
        singletons = 0;
        for (int start = 0; start < particles.Length; start++)
        {
            if (visited[start] || particles[start].IsActive == 0 || particles[start].BodyId != BodyId)
            {
                continue;
            }

            int componentSize = 0;
            visited[start] = true;
            pending.Push(start);
            while (pending.Count > 0)
            {
                int index = pending.Pop();
                componentSize++;
                for (int neighbor = 0; neighbor < 8; neighbor++)
                {
                    int otherIndex = NeighborIndex(index, neighbor, width, particles.Length);
                    if (otherIndex < 0 || visited[otherIndex] ||
                        particles[otherIndex].IsActive == 0 || particles[otherIndex].BodyId != BodyId ||
                        !Connected(index, otherIndex, neighbor, bonds))
                    {
                        continue;
                    }

                    visited[otherIndex] = true;
                    pending.Push(otherIndex);
                }
            }

            componentCount++;
            largest = Math.Max(largest, componentSize);
            singletons += componentSize == 1 ? 1 : 0;
        }
    }

    private static int NeighborIndex(int index, int neighbor, int width, int length)
    {
        int x = index % width;
        int y = index / width;
        int dx = neighbor is 0 or 3 or 5 ? -1 : neighbor is 2 or 4 or 7 ? 1 : 0;
        int dy = neighbor < 3 ? -1 : neighbor > 4 ? 1 : 0;
        int otherX = x + dx;
        int otherY = y + dy;
        if (otherX < 0 || otherX >= width || otherY < 0)
        {
            return -1;
        }

        int result = index + dy * width + dx;
        return result >= 0 && result < length ? result : -1;
    }

    private static bool Connected(
        int index,
        int otherIndex,
        int neighbor,
        ReadOnlySpan<LatticeBond> bonds)
    {
        uint mask = neighbor >= 4
            ? bonds[index].ActiveNeighborMask & (1u << neighbor)
            : bonds[otherIndex].ActiveNeighborMask & (1u << (7 - neighbor));
        return mask != 0;
    }
}
