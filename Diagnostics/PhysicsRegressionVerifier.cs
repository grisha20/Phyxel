using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

public static class PhysicsRegressionVerifier
{
    public static bool Validate(SimulationWorldSnapshot snapshot, out string report)
    {
        ReadOnlySpan<GridCell> grid = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        ReadOnlySpan<LatticeParticle> particles = MemoryMarshal.Cast<byte, LatticeParticle>(snapshot.Particles);
        ReadOnlySpan<LatticeBond> bonds = MemoryMarshal.Cast<byte, LatticeBond>(snapshot.Bonds);
        List<int> waterTopByColumn = [];
        float waterMass = 0;
        int minimumWaterX = snapshot.Width;
        int maximumWaterX = 0;
        double sandY = 0;
        int sandCount = 0;
        double gasY = 0;
        int gasCount = 0;
        int visibleLatticeCells = 0;
        for (int x = 0; x < snapshot.Width; x++)
        {
            int top = snapshot.Height;
            for (int y = 0; y < snapshot.Height; y++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive == 0)
                {
                    continue;
                }

                MaterialId material = (MaterialId)cell.MaterialId;
                if (material == MaterialId.Water)
                {
                    waterMass += cell.Mass;
                    minimumWaterX = Math.Min(minimumWaterX, x);
                    maximumWaterX = Math.Max(maximumWaterX, x);
                    top = Math.Min(top, y);
                }
                else if (material == MaterialId.Sand)
                {
                    sandY += y;
                    sandCount++;
                }
                else if (material == MaterialId.Gas)
                {
                    gasY += y;
                    gasCount++;
                }
                else if (material is MaterialId.Metal or MaterialId.Concrete)
                {
                    visibleLatticeCells++;
                }
            }

            if (top < snapshot.Height)
            {
                waterTopByColumn.Add(top);
            }
        }

        int activeParticles = 0;
        int activeMetalParticles = 0;
        double metalY = 0;
        int activeBondCount = 0;
        float maximumStress = 0;
        for (int index = 0; index < particles.Length; index++)
        {
            LatticeParticle particle = particles[index];
            if (particle.IsActive == 0)
            {
                continue;
            }

            activeParticles++;
            if ((MaterialId)particle.MaterialId == MaterialId.Metal)
            {
                activeMetalParticles++;
                metalY += particle.PositionY;
            }

            activeBondCount += BitOperations.PopCount(bonds[index].ActiveNeighborMask);
            maximumStress = Math.Max(maximumStress, Math.Max(particle.Stress, bonds[index].MaximumStrain));
        }

        double waterSurfaceDeviation = CalculateDeviation(waterTopByColumn);
        double averageSandY = sandCount == 0 ? 0 : sandY / sandCount;
        double averageGasY = gasCount == 0 ? snapshot.Height : gasY / gasCount;
        double averageMetalY = activeMetalParticles == 0 ? 0 : metalY / activeMetalParticles;
        float projectionRatio = activeParticles == 0 ? 0 : visibleLatticeCells / (float)activeParticles;
        int waterSpan = maximumWaterX >= minimumWaterX ? maximumWaterX - minimumWaterX : 0;
        bool waterSpread = waterMass > 150 && waterSpan > snapshot.Width * 0.34f && waterSurfaceDeviation < 42;
        bool densitySorting = sandCount > 0 && averageSandY > snapshot.Height * 0.46 &&
            gasCount > 0 && averageGasY < snapshot.Height * 0.72;
        bool latticeFall = activeMetalParticles > 0 && averageMetalY > snapshot.Height * 0.32;
        bool unifiedOccupancy = activeParticles > 0 && projectionRatio > 0.78f;
        bool structuralResponse = activeBondCount > activeParticles && maximumStress > 0.0001f;
        report = $"PHYXEL_PHYSICS_METRICS waterMass={waterMass:0.0} waterSpan={waterSpan} surfaceDeviation={waterSurfaceDeviation:0.0} sandY={averageSandY:0.0} gasY={averageGasY:0.0} metalY={averageMetalY:0.0} projection={projectionRatio:0.000} bonds={activeBondCount} stress={maximumStress:0.0000}";
        return waterSpread && densitySorting && latticeFall && unifiedOccupancy && structuralResponse;
    }

    private static double CalculateDeviation(IReadOnlyList<int> values)
    {
        if (values.Count == 0)
        {
            return double.MaxValue;
        }

        double average = 0;
        foreach (int value in values)
        {
            average += value;
        }

        average /= values.Count;
        double variance = 0;
        foreach (int value in values)
        {
            double difference = value - average;
            variance += difference * difference;
        }

        return Math.Sqrt(variance / values.Count);
    }
}
