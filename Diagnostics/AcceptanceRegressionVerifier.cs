using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

public static class AcceptanceRegressionVerifier
{
    private readonly record struct ColorMetrics(
        int Water,
        int Sand,
        int WrongWater,
        int WrongSand,
        int RedWater,
        int RedSand,
        int RedMetal);

    public static bool Validate(
        SpecificationScenarioMode mode,
        SimulationWorldSnapshot snapshot,
        SimulationStatistics statistics,
        SimulationDispatchCoordinator coordinator,
        string artifactDirectory,
        out string report)
    {
        return mode switch
        {
            SpecificationScenarioMode.AcceptanceBowl =>
                ValidateBowl(snapshot, statistics, coordinator, artifactDirectory, out report),
            SpecificationScenarioMode.AcceptanceBeam =>
                ValidateBeam(snapshot, artifactDirectory, out report),
            SpecificationScenarioMode.AcceptanceSand =>
                ValidateSand(snapshot, statistics, coordinator, artifactDirectory, out report),
            SpecificationScenarioMode.AcceptanceColors =>
                ValidateColors(snapshot, artifactDirectory, out report),
            SpecificationScenarioMode.AcceptanceMetalCritical =>
                MetalFractureRegressionVerifier.Validate(snapshot, out report),
            _ => Fail(out report)
        };
    }

    private static bool ValidateBowl(
        SimulationWorldSnapshot snapshot,
        SimulationStatistics statistics,
        SimulationDispatchCoordinator coordinator,
        string artifactDirectory,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        float[] bottomBoundary = BuildBottomBoundary(snapshot, 8001, 220);
        int water = 0;
        int sand = 0;
        int leakedWater = 0;
        int lateralWater = 0;
        int waterBelowBottom = 0;
        int missingBottom = 0;
        float waterY = 0;
        float sandY = 0;
        float maximumFluidSpeed = 0;
        float maximumSandSpeed = 0;
        int speedX = 0;
        int speedY = 0;
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                MaterialId material = cell.IsActive == 0 ? MaterialId.Empty : (MaterialId)cell.MaterialId;
                if (material == MaterialId.Water)
                {
                    water++;
                    waterY += y;
                    bool lateral = x is < 110 or > 329;
                    bool missing = bottomBoundary[x] < 0;
                    bool below = !missing && y > bottomBoundary[x] + 1;
                    lateralWater += lateral ? 1 : 0;
                    missingBottom += missing ? 1 : 0;
                    waterBelowBottom += below ? 1 : 0;
                    leakedWater += lateral || missing || below ? 1 : 0;
                    float speed = Math.Abs(cell.VelocityX) + Math.Abs(cell.VelocityY);
                    if (speed > maximumFluidSpeed)
                    {
                        maximumFluidSpeed = speed;
                        speedX = x;
                        speedY = y;
                    }
                }
                else if (material == MaterialId.Sand)
                {
                    sand++;
                    sandY += y;
                    maximumSandSpeed = Math.Max(
                        maximumSandSpeed,
                        Math.Abs(cell.VelocityX) + Math.Abs(cell.VelocityY));
                }
            }
        }

        BodyMetrics body = MeasureBody(snapshot, 8001, 220, 120, 319);
        string finalImage = ImagePath(artifactDirectory, "AcceptanceBowl_A_water_sand.png");
        string waterImage = ImagePath(artifactDirectory, "AcceptanceBowl_A_water_2s.png");
        ColorMetrics colors = AnalyzeColors(snapshot, finalImage, 8001);
        int waterStageRed = CountRedPixels(waterImage, new Rectangle(100, 100, 240, 145));
        float averageWaterY = waterY / Math.Max(water, 1);
        float averageSandY = sandY / Math.Max(sand, 1);
        bool passed = water >= 7000 && sand >= 3000 && leakedWater == 0 &&
            averageSandY >= averageWaterY + 10 && maximumFluidSpeed <= 2 && maximumSandSpeed <= 0.02f &&
            body.Nodes >= 3000 && body.Fragments == 0 && body.Bonds >= body.Nodes * 2 &&
            body.MaximumSag is >= 2 and <= 45 && waterStageRed == 0 &&
            colors.Water >= 5000 && colors.Sand >= 2000 &&
            colors.RedWater == 0 && colors.RedSand == 0 &&
            colors.WrongWater == 0 && colors.WrongSand == 0;
        report = $"PHYXEL_ACCEPTANCE_A water={water} sand={sand} leaked={leakedWater} " +
            $"lateral={lateralWater} below={waterBelowBottom} missingBottom={missingBottom} " +
            $"waterY={averageWaterY:0.0} sandY={averageSandY:0.0} speed={maximumFluidSpeed:0.000}@{speedX},{speedY} " +
            $"sandSpeed={maximumSandSpeed:0.000} " +
            $"metalNodes={body.Nodes} bonds={body.Bonds} fragments={body.Fragments} sag={body.MaximumSag:0.0} " +
            $"waterStageRed={waterStageRed} redWater={colors.RedWater} redSand={colors.RedSand} " +
            $"wrongWater={colors.WrongWater} wrongSand={colors.WrongSand} sleeping={coordinator.CellularSleeping}";
        return passed;
    }

    private static bool ValidateBeam(
        SimulationWorldSnapshot snapshot,
        string artifactDirectory,
        out string report)
    {
        BodyMetrics body = MeasureBody(snapshot, 8101, 150, 190, 250);
        BodyMetrics leftSupport = MeasureBody(snapshot, 8102, 150, 110, 122);
        BodyMetrics rightSupport = MeasureBody(snapshot, 8103, 150, 318, 330);
        int supportCells = CountMaterial(
            snapshot,
            MaterialId.Fixture,
            new Rectangle(105, 140, 230, snapshot.Height - 140));
        ColorMetrics loaded50 = AnalyzeColors(
            snapshot,
            ImagePath(artifactDirectory, "AcceptanceBeam_B_load_50.png"),
            8101);
        ColorMetrics loaded100 = AnalyzeColors(
            snapshot,
            ImagePath(artifactDirectory, "AcceptanceBeam_B_load_100.png"),
            8101);
        bool passed = body.Nodes is >= 390 and <= 410 && body.Bonds >= 900 &&
            body.Fragments == 0 && body.Plastic > 0 && body.AverageCenterSag >= 10 &&
            body.MaximumSag <= 60 && loaded50.RedSand == 0 && loaded100.RedSand == 0 &&
            loaded100.RedMetal > 5 && loaded100.WrongSand == 0 &&
            leftSupport.Nodes >= 800 && rightSupport.Nodes >= 800 && supportCells >= 1600;
        report = $"PHYXEL_ACCEPTANCE_B nodes={body.Nodes} bonds={body.Bonds} fragments={body.Fragments} " +
            $"plastic={body.Plastic} centerSag={body.AverageCenterSag:0.0} maximumSag={body.MaximumSag:0.0} " +
            $"redMetal={loaded100.RedMetal} redSand50={loaded50.RedSand} redSand100={loaded100.RedSand} " +
            $"wrongSand={loaded100.WrongSand} supports={leftSupport.Nodes}/{rightSupport.Nodes} " +
            $"supportCells={supportCells}";
        return passed;
    }

    private static bool ValidateSand(
        SimulationWorldSnapshot snapshot,
        SimulationStatistics statistics,
        SimulationDispatchCoordinator coordinator,
        string artifactDirectory,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        List<(int X, int Y)> surface = [];
        int sand = 0;
        int minimumX = snapshot.Width;
        int maximumX = 0;
        int apexX = 0;
        int apexY = snapshot.Height;
        int baseY = 0;
        int restingSand = 0;
        float maximumSandSpeed = 0;
        for (int x = 40; x < snapshot.Width - 40; x++)
        {
            int top = snapshot.Height;
            for (int y = 0; y < snapshot.Height; y++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive != 0 && (MaterialId)cell.MaterialId == MaterialId.Sand)
                {
                    sand++;
                    restingSand += cell.Reserved >= 30 ? 1 : 0;
                    maximumSandSpeed = Math.Max(
                        maximumSandSpeed,
                        Math.Abs(cell.VelocityX) + Math.Abs(cell.VelocityY));
                    top = Math.Min(top, y);
                }
            }

            if (top == snapshot.Height)
            {
                continue;
            }

            minimumX = Math.Min(minimumX, x);
            maximumX = Math.Max(maximumX, x);
            baseY = Math.Max(baseY, top);
            if (top < apexY)
            {
                apexY = top;
                apexX = x;
            }
            surface.Add((x, top));
        }

        int height = baseY - apexY;
        float leftAngle = FitAngle(surface, apexX, baseY, height, true);
        float rightAngle = FitAngle(surface, apexX, baseY, height, false);
        float roughness = SurfaceRoughness(surface);
        int gaps = maximumX >= minimumX ? maximumX - minimumX + 1 - surface.Count : int.MaxValue;
        ColorMetrics colors = AnalyzeColors(
            snapshot,
            ImagePath(artifactDirectory, "AcceptanceSand_C_pile_3s.png"),
            0);
        bool passed = sand is >= 850 and <= 1150 && height >= 15 &&
            leftAngle is >= 30 and <= 45 && rightAngle is >= 30 and <= 45 &&
            gaps == 0 && roughness <= 1.5f && colors.RedSand == 0 && colors.WrongSand == 0 &&
            maximumSandSpeed <= 0.02f && restingSand >= sand * 0.99f;
        report = $"PHYXEL_ACCEPTANCE_C sand={sand} height={height} width={maximumX - minimumX + 1} " +
            $"leftAngle={leftAngle:0.0} rightAngle={rightAngle:0.0} roughness={roughness:0.00} gaps={gaps} " +
            $"redSand={colors.RedSand} wrongSand={colors.WrongSand} resting={restingSand}/{sand} " +
            $"speed={maximumSandSpeed:0.000} gpuSleeping={coordinator.CellularSleeping}";
        return passed;
    }

    private static bool ValidateColors(
        SimulationWorldSnapshot snapshot,
        string artifactDirectory,
        out string report)
    {
        BodyMetrics body = MeasureBody(snapshot, 8303, 150, 330, 400);
        ColorMetrics colors = AnalyzeColors(
            snapshot,
            ImagePath(artifactDirectory, "AcceptanceColors_D_colors.png"),
            8303);
        bool passed = colors.Water >= 1500 && colors.Sand >= 2500 &&
            colors.WrongWater == 0 && colors.WrongSand == 0 &&
            colors.RedWater == 0 && colors.RedSand == 0 && colors.RedMetal > 5 &&
            body.Nodes is >= 390 and <= 410 && body.Fragments == 0 && body.MaximumStress > 0.045f;
        report = $"PHYXEL_ACCEPTANCE_D water={colors.Water} sand={colors.Sand} " +
            $"wrongWater={colors.WrongWater} wrongSand={colors.WrongSand} redWater={colors.RedWater} " +
            $"redSand={colors.RedSand} redMetal={colors.RedMetal} metalStress={body.MaximumStress:0.000} " +
            $"metalFragments={body.Fragments}";
        return passed;
    }

    private static ColorMetrics AnalyzeColors(
        SimulationWorldSnapshot snapshot,
        string imagePath,
        uint highlightedBody)
    {
        if (!File.Exists(imagePath))
        {
            return new ColorMetrics(0, 0, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, 0);
        }

        ReadOnlySpan<GridCell> grid = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        ReadOnlySpan<LatticeParticle> particles = MemoryMarshal.Cast<byte, LatticeParticle>(snapshot.Particles);
        int water = 0;
        int sand = 0;
        int wrongWater = 0;
        int wrongSand = 0;
        int redWater = 0;
        int redSand = 0;
        int redMetal = 0;
        using Bitmap bitmap = new(imagePath);
        int width = Math.Min(bitmap.Width, snapshot.Width);
        int height = Math.Min(bitmap.Height, snapshot.Height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive == 0)
                {
                    continue;
                }

                Color color = bitmap.GetPixel(x, y);
                bool red = IsStressRed(color);
                MaterialId material = (MaterialId)cell.MaterialId;
                if (material == MaterialId.Water)
                {
                    water++;
                    wrongWater += color.B <= color.G || color.B <= color.R ? 1 : 0;
                    redWater += red ? 1 : 0;
                }
                else if (material == MaterialId.Sand)
                {
                    sand++;
                    wrongSand += color.R <= color.G || color.G <= color.B ? 1 : 0;
                    redSand += red ? 1 : 0;
                }
                else if (material == MaterialId.Metal && highlightedBody != 0)
                {
                    uint particleIndex = Math.Min(cell.LatticeParticleIndex, (uint)particles.Length - 1);
                    redMetal += particles[(int)particleIndex].BodyId == highlightedBody && red ? 1 : 0;
                }
            }
        }

        return new ColorMetrics(water, sand, wrongWater, wrongSand, redWater, redSand, redMetal);
    }

    private static int CountMaterial(
        SimulationWorldSnapshot snapshot,
        MaterialId material,
        Rectangle region)
    {
        ReadOnlySpan<GridCell> grid = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        Rectangle bounds = Rectangle.Intersect(region, new Rectangle(0, 0, snapshot.Width, snapshot.Height));
        int count = 0;
        for (int y = bounds.Top; y < bounds.Bottom; y++)
        {
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                count += cell.IsActive != 0 && (MaterialId)cell.MaterialId == material ? 1 : 0;
            }
        }

        return count;
    }

    private static int CountRedPixels(string imagePath, Rectangle region)
    {
        if (!File.Exists(imagePath))
        {
            return int.MaxValue;
        }

        int red = 0;
        using Bitmap bitmap = new(imagePath);
        Rectangle clipped = Rectangle.Intersect(region, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        for (int y = clipped.Top; y < clipped.Bottom; y++)
        {
            for (int x = clipped.Left; x < clipped.Right; x++)
            {
                red += IsStressRed(bitmap.GetPixel(x, y)) ? 1 : 0;
            }
        }

        return red;
    }

    private static bool IsStressRed(Color color)
    {
        return color.R > 150 && color.R > color.G * 1.25f && color.R > color.B * 1.15f;
    }

    private static BodyMetrics MeasureBody(
        SimulationWorldSnapshot snapshot,
        uint bodyId,
        float referenceY,
        float centerLeft,
        float centerRight)
    {
        ReadOnlySpan<LatticeParticle> particles = MemoryMarshal.Cast<byte, LatticeParticle>(snapshot.Particles);
        ReadOnlySpan<LatticeBond> bonds = MemoryMarshal.Cast<byte, LatticeBond>(snapshot.Bonds);
        int nodes = 0;
        int activeBonds = 0;
        int fragments = 0;
        int plastic = 0;
        float maximumSag = 0;
        float centerY = 0;
        int centerCount = 0;
        float maximumStress = 0;
        for (int index = 0; index < particles.Length; index++)
        {
            LatticeParticle particle = particles[index];
            if (particle.IsActive == 0 || particle.BodyId != bodyId)
            {
                continue;
            }

            LatticeBond bond = bonds[index];
            nodes++;
            activeBonds += BitOperations.PopCount(bond.ActiveNeighborMask);
            fragments += CountConnections(index, snapshot.Width, particles, bonds) == 0 ? 1 : 0;
            plastic += (particle.IsDynamic & 2) != 0 ? 1 : 0;
            float originalY = index / snapshot.Width + 0.5f;
            maximumSag = Math.Max(maximumSag, particle.PositionY - originalY);
            maximumStress = Math.Max(maximumStress, Math.Max(particle.Stress, bond.MaximumStrain));
            if (particle.PositionX >= centerLeft && particle.PositionX <= centerRight)
            {
                centerY += particle.PositionY;
                centerCount++;
            }
        }

        return new BodyMetrics(
            nodes,
            activeBonds,
            fragments,
            plastic,
            maximumSag,
            centerY / Math.Max(centerCount, 1) - referenceY,
            maximumStress);
    }

    private static float[] BuildBottomBoundary(
        SimulationWorldSnapshot snapshot,
        uint bodyId,
        int originalBottomTop)
    {
        ReadOnlySpan<LatticeParticle> particles = MemoryMarshal.Cast<byte, LatticeParticle>(snapshot.Particles);
        float[] boundary = new float[snapshot.Width];
        Array.Fill(boundary, -1);
        for (int index = 0; index < particles.Length; index++)
        {
            LatticeParticle particle = particles[index];
            if (particle.IsActive == 0 || particle.BodyId != bodyId ||
                index / snapshot.Width < originalBottomTop)
            {
                continue;
            }

            int x = Math.Clamp((int)particle.PositionX, 0, snapshot.Width - 1);
            boundary[x] = Math.Max(boundary[x], particle.PositionY);
        }

        for (int x = 1; x < boundary.Length - 1; x++)
        {
            if (boundary[x] < 0)
            {
                boundary[x] = Math.Max(boundary[x - 1], boundary[x + 1]);
            }
        }

        return boundary;
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

    private static float FitAngle(
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
        double slope = count < 3 || Math.Abs(denominator) < 0.001
            ? 0
            : (count * sumXY - sumX * sumY) / denominator;
        return MathF.Atan(MathF.Abs((float)slope)) * 180 / MathF.PI;
    }

    private static float SurfaceRoughness(IReadOnlyList<(int X, int Y)> surface)
    {
        if (surface.Count < 2)
        {
            return float.MaxValue;
        }

        float variation = 0;
        for (int index = 1; index < surface.Count; index++)
        {
            variation += Math.Abs(surface[index].Y - surface[index - 1].Y);
        }

        return variation / (surface.Count - 1);
    }

    private static string ImagePath(string directory, string fileName)
    {
        return Path.Combine(directory, fileName);
    }

    private static bool Fail(out string report)
    {
        report = "PHYXEL_ACCEPTANCE_UNKNOWN_SCENARIO";
        return false;
    }

    private readonly record struct BodyMetrics(
        int Nodes,
        int Bonds,
        int Fragments,
        int Plastic,
        float MaximumSag,
        float AverageCenterSag,
        float MaximumStress);
}
