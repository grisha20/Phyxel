using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

public static class SpecificationRegressionVerifier
{
    public static bool Validate(
        SpecificationScenarioMode mode,
        bool loadedBeamCapture,
        SimulationWorldSnapshot snapshot,
        SimulationStatistics statistics,
        SimulationDispatchCoordinator coordinator,
        string artifactDirectory,
        out string report)
    {
        return mode switch
        {
            SpecificationScenarioMode.WaterRest => ValidateWater(snapshot, statistics, coordinator, out report),
            SpecificationScenarioMode.Funnel => ValidateFunnels(snapshot, out report),
            SpecificationScenarioMode.WaterSlope => ValidateSlope(snapshot, statistics, coordinator, out report),
            SpecificationScenarioMode.SandSlope => ValidateSandSlope(snapshot, statistics, coordinator, out report),
            SpecificationScenarioMode.AcceptanceBowl or SpecificationScenarioMode.AcceptanceBeam or
                SpecificationScenarioMode.AcceptanceSand or SpecificationScenarioMode.AcceptanceColors or
                SpecificationScenarioMode.AcceptanceMetalCritical =>
                AcceptanceRegressionVerifier.Validate(mode, snapshot, statistics, coordinator, artifactDirectory, out report),
            _ => ValidateBeam(mode, loadedBeamCapture, snapshot, out report)
        };
    }

    private static bool ValidateSandSlope(
        SimulationWorldSnapshot snapshot,
        SimulationStatistics statistics,
        SimulationDispatchCoordinator coordinator,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        int minimumX = snapshot.Width;
        int maximumX = 0;
        int apexX = 0;
        int apexY = snapshot.Height;
        int baseY = 0;
        int sandCells = 0;
        int restingSand = 0;
        float maximumSpeed = 0;
        List<(int X, int Y)> surface = [];
        foreach (GridCell cell in grid)
        {
            if (cell.IsActive == 0 || (MaterialId)cell.MaterialId != MaterialId.Sand)
            {
                continue;
            }

            sandCells++;
            restingSand += cell.Reserved >= 30 ? 1 : 0;
            maximumSpeed = Math.Max(maximumSpeed, Math.Abs(cell.VelocityX) + Math.Abs(cell.VelocityY));
        }
        for (int x = 300; x < Math.Min(snapshot.Width, 1620); x++)
        {
            int top = snapshot.Height;
            for (int y = 0; y < snapshot.Height; y++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive != 0 && (MaterialId)cell.MaterialId == MaterialId.Sand)
                {
                    top = y;
                    break;
                }
            }

            if (top == snapshot.Height)
            {
                continue;
            }

            minimumX = Math.Min(minimumX, x);
            maximumX = Math.Max(maximumX, x);
            if (top < apexY)
            {
                apexY = top;
                apexX = x;
            }
            baseY = Math.Max(baseY, top);
            surface.Add((x, top));
        }

        int pileHeight = baseY - apexY;
        float leftAngle = FitSurfaceAngle(surface, apexX, baseY, pileHeight, true);
        float rightAngle = FitSurfaceAngle(surface, apexX, baseY, pileHeight, false);
        uint unsettledTolerance = Math.Max(1u, statistics.ActiveCells / 50000);
        bool passed = pileHeight >= 280 && leftAngle is >= 30 and <= 45 && rightAngle is >= 30 and <= 45 &&
            coordinator.CellularSleeping && statistics.Reserved + unsettledTolerance >= statistics.ActiveCells;
        report = $"PHYXEL_SPEC_SAND_SLOPE height={pileHeight} leftAngle={leftAngle:0.0} rightAngle={rightAngle:0.0} width={maximumX - minimumX + 1} sand={sandCells} resting={restingSand} maximumSpeed={maximumSpeed:0.000} activeStats={statistics.ActiveCells} restingStats={statistics.Reserved} sleeping={coordinator.CellularSleeping}";
        return passed;
    }

    private static float FitSurfaceAngle(
        IReadOnlyList<(int X, int Y)> surface,
        int apexX,
        int baseY,
        int pileHeight,
        bool left)
    {
        double sumX = 0;
        double sumY = 0;
        double sumXX = 0;
        double sumXY = 0;
        int count = 0;
        foreach ((int x, int y) in surface)
        {
            int elevation = baseY - y;
            if (left != x < apexX || elevation < pileHeight * 0.2f || elevation > pileHeight * 0.8f)
            {
                continue;
            }

            sumX += x;
            sumY += y;
            sumXX += x * (double)x;
            sumXY += x * (double)y;
            count++;
        }

        double denominator = count * sumXX - sumX * sumX;
        double slope = Math.Abs(denominator) < 0.001 ? 0 : (count * sumXY - sumX * sumY) / denominator;
        return MathF.Atan(MathF.Abs((float)slope)) * 180 / MathF.PI;
    }

    private static bool ValidateSlope(
        SimulationWorldSnapshot snapshot,
        SimulationStatistics statistics,
        SimulationDispatchCoordinator coordinator,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        List<int> surface = [];
        int puddleCells = 0;
        int slopeCells = 0;
        float slopeMass = 0;
        float puddleMass = 0;
        int rampParticles = 0;
        ReadOnlySpan<LatticeParticle> particles = MemoryMarshal.Cast<byte, LatticeParticle>(snapshot.Particles);
        foreach (LatticeParticle particle in particles)
        {
            rampParticles += particle.IsActive != 0 && particle.BodyId == 7501 ? 1 : 0;
        }
        for (int x = 810; x < 1195; x++)
        {
            int top = snapshot.Height;
            for (int y = 0; y < snapshot.Height; y++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive != 0 && (MaterialId)cell.MaterialId == MaterialId.Water && cell.Mass >= 0.05f)
                {
                    puddleCells++;
                    puddleMass += cell.Mass;
                    top = Math.Min(top, y);
                }
            }

            if (top < snapshot.Height)
            {
                surface.Add(top);
            }
        }

        for (int y = 280; y < 700; y++)
        {
            for (int x = 90; x < 805; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                slopeCells += cell.IsActive != 0 && (MaterialId)cell.MaterialId == MaterialId.Water ? 1 : 0;
                slopeMass += cell.IsActive != 0 && (MaterialId)cell.MaterialId == MaterialId.Water ? cell.Mass : 0;
            }
        }

        int range = surface.Count == 0 ? int.MaxValue : Max(surface) - Min(surface);
        bool passed = puddleCells > 5000 && surface.Count > 300 && range <= 3 && slopeCells < 400;
        report = $"PHYXEL_SPEC_WATER_SLOPE rampParticles={rampParticles} puddleCells={puddleCells} puddleMass={puddleMass:0.0} columns={surface.Count} surfaceRange={range} slopeRemainder={slopeCells} slopeMass={slopeMass:0.0} sleeping={coordinator.CellularSleeping}";
        return passed;
    }

    private static bool ValidateWater(
        SimulationWorldSnapshot snapshot,
        SimulationStatistics statistics,
        SimulationDispatchCoordinator coordinator,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        List<int> surface = [];
        int water = 0;
        int sleeping = 0;
        float maximumSpeed = 0;
        for (int x = 104; x <= 597; x++)
        {
            int top = snapshot.Height;
            for (int y = 0; y < snapshot.Height; y++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive == 0 || (MaterialId)cell.MaterialId != MaterialId.Water)
                {
                    continue;
                }

                top = Math.Min(top, y);
                water++;
                sleeping += cell.Reserved >= 240 ? 1 : 0;
                maximumSpeed = Math.Max(maximumSpeed, Math.Abs(cell.VelocityX) + Math.Abs(cell.VelocityY));
            }

            if (top < snapshot.Height)
            {
                surface.Add(top);
            }
        }

        int surfaceRange = surface.Count == 0 ? int.MaxValue : Max(surface) - Min(surface);
        bool passed = water >= 8500 && surface.Count >= 490 && surfaceRange <= 1 &&
            sleeping == water && maximumSpeed == 0 && coordinator.CellularSleeping &&
            statistics.Reserved >= statistics.ActiveCells;
        report = $"PHYXEL_SPEC_WATER_REST cells={water} columns={surface.Count} surfaceRange={surfaceRange} sleeping={sleeping} maximumSpeed={maximumSpeed:0.0000} gpuSleeping={coordinator.CellularSleeping}";
        return passed;
    }

    private static bool ValidateFunnels(SimulationWorldSnapshot snapshot, out string report)
    {
        ReadOnlySpan<GridCell> grid = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        int waterBelow = 0;
        int sandBelow = 0;
        int sandAbove = 0;
        int totalWater = 0;
        int totalSand = 0;
        int channelSand = 0;
        int channelSandBelow = 0;
        int funnelMetal = 0;
        int maximumSandY = 0;
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 100; x < Math.Min(snapshot.Width, 1060); x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive == 0)
                {
                    continue;
                }

                MaterialId material = (MaterialId)cell.MaterialId;
                totalWater += material == MaterialId.Water ? 1 : 0;
                totalSand += material == MaterialId.Sand ? 1 : 0;
                channelSand += material == MaterialId.Sand && x is >= 846 and <= 874 ? 1 : 0;
                channelSandBelow += material == MaterialId.Sand && x is >= 846 and <= 874 && y > 540 ? 1 : 0;
                funnelMetal += material == MaterialId.Fixture && x is >= 840 and <= 880 ? 1 : 0;
                maximumSandY = material == MaterialId.Sand ? Math.Max(maximumSandY, y) : maximumSandY;
                if (material == MaterialId.Water && x < 580 && y > 540)
                {
                    waterBelow++;
                }
                else if (material == MaterialId.Sand && x > 650)
                {
                    sandBelow += y > 540 ? 1 : 0;
                    sandAbove += y <= 540 ? 1 : 0;
                }
            }
        }

        bool passed = waterBelow > 500 && sandAbove > 500 && sandBelow < sandAbove * 0.08;
        report = $"PHYXEL_SPEC_FUNNEL water={totalWater} sand={totalSand} waterBelow={waterBelow} sandAbove={sandAbove} sandBelow={sandBelow} channelSand={channelSand} channelSandBelow={channelSandBelow} funnelMetal={funnelMetal} maximumSandY={maximumSandY} sandPassRatio={sandBelow / (float)Math.Max(sandAbove + sandBelow, 1):0.000}";
        return passed;
    }

    private static bool ValidateBeam(
        SpecificationScenarioMode mode,
        bool loadedBeamCapture,
        SimulationWorldSnapshot snapshot,
        out string report)
    {
        ReadOnlySpan<LatticeParticle> particles = MemoryMarshal.Cast<byte, LatticeParticle>(snapshot.Particles);
        ReadOnlySpan<LatticeBond> bonds = MemoryMarshal.Cast<byte, LatticeBond>(snapshot.Bonds);
        int nodes = 0;
        int activeBonds = 0;
        int cracked = 0;
        int fragments = 0;
        int dynamicFragments = 0;
        int plastic = 0;
        float maximumPlasticOffset = 0;
        float maximumLoad = 0;
        float maximumStress = 0;
        float maximumVelocity = 0;
        float maximumY = 0;
        float centerY = 0;
        int centerCount = 0;
        for (int index = 0; index < particles.Length; index++)
        {
            LatticeParticle particle = particles[index];
            if (particle.IsActive == 0 || particle.BodyId != 7301)
            {
                continue;
            }

            LatticeBond bond = bonds[index];
            int bondCount = BitOperations.PopCount(bond.ActiveNeighborMask);
            nodes++;
            activeBonds += bondCount;
            cracked += Math.Max(particle.Stress, bond.MaximumStrain) > bond.ElasticLimit ? 1 : 0;
            bool isolated = CountConnections(index, snapshot.Width, particles, bonds) == 0;
            fragments += isolated ? 1 : 0;
            dynamicFragments += isolated && particle.IsDynamic != 0 ? 1 : 0;
            plastic += (particle.IsDynamic & 2) != 0 ? 1 : 0;
            maximumPlasticOffset = Math.Max(maximumPlasticOffset, particle.PlasticOffsetY);
            maximumLoad = Math.Max(maximumLoad, bond.AccumulatedLoad);
            maximumStress = Math.Max(maximumStress, Math.Max(particle.Stress, bond.MaximumStrain));
            maximumVelocity = Math.Max(maximumVelocity, Math.Abs(particle.VelocityX) + Math.Abs(particle.VelocityY));
            maximumY = Math.Max(maximumY, particle.PositionY);
            if (particle.PositionX is > 190 and < 250)
            {
                centerY += particle.PositionY;
                centerCount++;
            }
        }

        float averageCenterY = centerY / Math.Max(centerCount, 1);
        float sag = averageCenterY - 150f;
        bool economy = nodes <= 400 && activeBonds <= 1600;
        bool behavior = loadedBeamCapture
            ? mode is SpecificationScenarioMode.MetalElastic or SpecificationScenarioMode.MetalPlastic &&
                sag >= 10 && fragments == 0
            : mode switch
        {
            SpecificationScenarioMode.MetalElastic => Math.Abs(sag) <= 2 && fragments == 0,
            SpecificationScenarioMode.MetalPlastic => maximumY >= 153 && plastic > 0 && fragments == 0,
            SpecificationScenarioMode.ConcreteCrack => cracked > 5 && fragments == 0,
            SpecificationScenarioMode.ConcreteBreak => fragments > 2 && maximumY > 165,
            _ => false
        };
        report = $"PHYXEL_SPEC_SOLID mode={mode} loaded={loadedBeamCapture} nodes={nodes} bonds={activeBonds} cracked={cracked} plastic={plastic} fragments={fragments} dynamicFragments={dynamicFragments} centerY={averageCenterY:0.00} sag={sag:0.00} maximumY={maximumY:0.00} maximumVelocity={maximumVelocity:0.00} plasticOffset={maximumPlasticOffset:0.00} maximumLoad={maximumLoad:0.00} maximumStress={maximumStress:0.000}";
        return economy && behavior;
    }

    private static int Min(IReadOnlyList<int> values)
    {
        int result = int.MaxValue;
        foreach (int value in values) result = Math.Min(result, value);
        return result;
    }

    private static int CountConnections(
        int index,
        int width,
        ReadOnlySpan<LatticeParticle> particles,
        ReadOnlySpan<LatticeBond> bonds)
    {
        int x = index % width;
        int y = index / width;
        int[] offsets = [-width - 1, -width, -width + 1, -1, 1, width - 1, width, width + 1];
        int result = 0;
        for (int neighbor = 0; neighbor < offsets.Length; neighbor++)
        {
            int otherIndex = index + offsets[neighbor];
            int otherX = x + (neighbor is 0 or 3 or 5 ? -1 : neighbor is 2 or 4 or 7 ? 1 : 0);
            int otherY = y + (neighbor < 3 ? -1 : neighbor > 4 ? 1 : 0);
            if (otherX < 0 || otherY < 0 || otherX >= width || otherIndex < 0 || otherIndex >= particles.Length)
            {
                continue;
            }

            uint mask = neighbor >= 4
                ? bonds[index].ActiveNeighborMask & (1u << neighbor)
                : bonds[otherIndex].ActiveNeighborMask & (1u << (7 - neighbor));
            result += mask != 0 ? 1 : 0;
        }

        return result;
    }

    private static int Max(IReadOnlyList<int> values)
    {
        int result = int.MinValue;
        foreach (int value in values) result = Math.Max(result, value);
        return result;
    }
}
