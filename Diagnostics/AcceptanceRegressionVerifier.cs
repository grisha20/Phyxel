using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

public static class AcceptanceRegressionVerifier
{
    private readonly record struct ComponentMetrics(
        int Count,
        int Largest,
        int MinimumX,
        int MaximumX,
        int MinimumY,
        int MaximumY);

    public static bool Validate(
        AcceptanceScenarioMode mode,
        SimulationWorldSnapshot snapshot,
        SimulationStatistics statistics,
        double framesPerSecond,
        string artifactDirectory,
        out string report)
    {
        return mode switch
        {
            AcceptanceScenarioMode.Bowl => ValidateBowl(snapshot, artifactDirectory, out report),
            AcceptanceScenarioMode.SolidGravity => ValidateSolidGravity(snapshot, artifactDirectory, out report),
            AcceptanceScenarioMode.Sand => ValidateSand(snapshot, artifactDirectory, out report),
            AcceptanceScenarioMode.Hydro => ValidateHydro(
                snapshot,
                statistics,
                framesPerSecond,
                artifactDirectory,
                out report),
            AcceptanceScenarioMode.Slope => MaterialRegressionVerifier.ValidateSlope(
                snapshot,
                artifactDirectory,
                out report),
            AcceptanceScenarioMode.Gas => MaterialRegressionVerifier.ValidateGas(
                snapshot,
                artifactDirectory,
                out report),
            _ => Fail(out report)
        };
    }

    private static bool ValidateBowl(
        SimulationWorldSnapshot snapshot,
        string artifactDirectory,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int metal = 0;
        int water = 0;
        int sand = 0;
        int leakedWater = 0;
        int movingWater = 0;
        int movingSand = 0;
        double waterY = 0;
        double sandY = 0;
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive == 0)
                {
                    continue;
                }
                MaterialId material = (MaterialId)cell.MaterialId;
                if (material == MaterialId.Metal) metal++;
                if (material == MaterialId.Water)
                {
                    water++;
                    waterY += y;
                    leakedWater += x < 108 || x > 331 || y > 231 ? 1 : 0;
                    movingWater += Speed(cell) > 0.02f ? 1 : 0;
                }
                if (material == MaterialId.Sand)
                {
                    sand++;
                    sandY += y;
                    movingSand += Speed(cell) > 0.02f ? 1 : 0;
                }
            }
        }
        int wallGaps = 0;
        for (int y = 115; y <= 229; y++)
        {
            wallGaps += HasMaterial(grid, snapshot.Width, 108, 120, y, MaterialId.Metal) ? 0 : 1;
            wallGaps += HasMaterial(grid, snapshot.Width, 319, 331, y, MaterialId.Metal) ? 0 : 1;
        }
        for (int x = 110; x <= 329; x++)
        {
            wallGaps += HasMaterial(grid, snapshot.Width, x, x, 220, 230, MaterialId.Metal) ? 0 : 1;
        }
        string waterImage = Path.Combine(artifactDirectory, "A_water_2s.png");
        string finalImage = Path.Combine(artifactDirectory, "A_water_sand.png");
        WaterVisualMetrics waterVisual = AnalyzeWater(waterImage, 121, 318, 120, 218);
        ColorMetrics colors = AnalyzeColors(finalImage);
        bool passed = metal > 3500 && water > 1000 && sand > 5000 && leakedWater == 0 && wallGaps == 0 &&
            movingWater == 0 && movingSand == 0 && sandY / sand > waterY / water &&
            waterVisual.Columns > 190 && waterVisual.Gaps == 0 && waterVisual.SurfaceRange <= 2 &&
            File.Exists(waterImage) && colors.Red == 0 && colors.Blue > 500 && colors.Yellow > 500 && colors.Metal > 500;
        report = $"PHYXEL_A bowlMetal={metal} water={water} sand={sand} leaked={leakedWater} gaps={wallGaps} movingWater={movingWater} movingSand={movingSand} waterY={waterY / Math.Max(1, water):0.0} sandY={sandY / Math.Max(1, sand):0.0} imageColumns={waterVisual.Columns} imageGaps={waterVisual.Gaps} surfaceRange={waterVisual.SurfaceRange} red={colors.Red}";
        return passed;
    }

    private static bool ValidateSolidGravity(
        SimulationWorldSnapshot snapshot,
        string artifactDirectory,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        GridCell[] componentCells = grid.ToArray();
        ComponentMetrics metal = Components(componentCells, snapshot.Width, snapshot.Height, MaterialId.Metal);
        ComponentMetrics concrete = Components(componentCells, snapshot.Width, snapshot.Height, MaterialId.Concrete);
        string offImage = Path.Combine(artifactDirectory, "B_gravity_off.png");
        string fallingImage = Path.Combine(artifactDirectory, "B_falling.png");
        string landedImage = Path.Combine(artifactDirectory, "B_landed.png");
        string splitImage = Path.Combine(artifactDirectory, "B_split_concrete.png");
        int suspendedMetal = CountColor(offImage, 50, 15, 120, 85, IsMetal);
        int suspendedConcrete = CountColor(offImage, 205, 45, 425, 75, IsConcrete);
        ColorMetrics colors = AnalyzeColors(splitImage);
        int squareCells = CountMaterial(grid, snapshot.Width, 50, 120, 180, 245, MaterialId.Metal);
        int supportedFragment = CountMaterial(grid, snapshot.Width, 125, 165, 90, 115, MaterialId.Metal);
        int floorFragment = CountMaterial(grid, snapshot.Width, 170, 210, 225, 245, MaterialId.Metal);
        int supportedConcrete = CountMaterial(grid, snapshot.Width, 210, 310, 155, 185, MaterialId.Concrete);
        int floorConcrete = CountMaterial(grid, snapshot.Width, 320, 410, 225, 245, MaterialId.Concrete);
        int landedWholeConcrete = CountColor(landedImage, 205, 155, 425, 185, IsConcrete);
        int water = CountMaterial(grid, snapshot.Width, 0, snapshot.Width - 1, 0, snapshot.Height - 1, MaterialId.Water);
        int sand = CountMaterial(grid, snapshot.Width, 0, snapshot.Width - 1, 0, snapshot.Height - 1, MaterialId.Sand);
        bool metalWhole = metal.Count == 3 && metal.Largest > 1800 && squareCells > 1800 &&
            supportedFragment > 150 && floorFragment > 150;
        bool concreteSplit = concrete.Count == 2 && concrete.Largest > 700 &&
            supportedConcrete > 650 && floorConcrete > 500 && landedWholeConcrete > 1400 &&
            concrete.MinimumY <= 170 && concrete.MaximumY >= 240;
        bool passed = metalWhole && concreteSplit && water > 500 && sand > 300 &&
            suspendedMetal > 1500 && suspendedConcrete > 1000 &&
            File.Exists(fallingImage) && File.Exists(splitImage) && colors.Red == 0;
        report = $"PHYXEL_B metalComponents={metal.Count} largestMetal={metal.Largest} square={squareCells} splitLevels={supportedFragment}/{floorFragment} concreteComponents={concrete.Count} concreteCells={concrete.Largest} concreteLevels={supportedConcrete}/{floorConcrete} landedWhole={landedWholeConcrete} water={water} sand={sand} suspendedMetal={suspendedMetal} suspendedConcrete={suspendedConcrete} red={colors.Red}";
        return passed;
    }

    private static bool ValidateSand(
        SimulationWorldSnapshot snapshot,
        string artifactDirectory,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int[] surface = new int[snapshot.Width];
        Array.Fill(surface, -1);
        int sand = 0;
        int resting = 0;
        int moving = 0;
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive == 0 || cell.MaterialId != (uint)MaterialId.Sand)
                {
                    continue;
                }
                sand++;
                surface[x] = surface[x] < 0 ? y : surface[x];
                resting += cell.RestFrames >= 30 ? 1 : 0;
                moving += Speed(cell) > 0.02f ? 1 : 0;
            }
        }
        int left = Array.FindIndex(surface, value => value >= 0);
        int right = Array.FindLastIndex(surface, value => value >= 0);
        int peakX = left;
        for (int x = Math.Max(0, left); x <= right; x++)
        {
            if (surface[x] >= 0 && surface[x] < surface[peakX]) peakX = x;
        }
        float leftAngle = Angle(surface, left, peakX);
        float rightAngle = Angle(surface, right, peakX);
        float roughness = 0;
        int samples = 0;
        int gaps = 0;
        for (int x = Math.Max(0, left + 1); x <= right; x++)
        {
            if (surface[x] < 0 || surface[x - 1] < 0)
            {
                gaps++;
                continue;
            }
            roughness += Math.Abs(surface[x] - surface[x - 1]);
            samples++;
        }
        roughness /= Math.Max(1, samples);
        ColorMetrics colors = AnalyzeColors(Path.Combine(artifactDirectory, "C_pile_3s.png"));
        bool passed = sand is >= 900 and <= 1100 && leftAngle is >= 30 and <= 45 &&
            rightAngle is >= 30 and <= 45 && roughness <= 1.5f && gaps == 0 &&
            resting == sand && moving == 0 && colors.Red == 0 && colors.Yellow > 500;
        report = $"PHYXEL_C sand={sand} resting={resting} moving={moving} width={right - left + 1} leftAngle={leftAngle:0.0} rightAngle={rightAngle:0.0} roughness={roughness:0.00} gaps={gaps} red={colors.Red}";
        return passed;
    }

    private static bool ValidateHydro(
        SimulationWorldSnapshot snapshot,
        SimulationStatistics statistics,
        double framesPerSecond,
        string artifactDirectory,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int leftTop = SurfaceTop(grid, snapshot.Width, 25, 128, 100, 245);
        int rightTop = SurfaceTop(grid, snapshot.Width, 148, 250, 100, 245);
        int waterfallTop = SurfaceTop(grid, snapshot.Width, 300, 455, 100, 245);
        int water = 0;
        int resting = 0;
        int moving = 0;
        int leaks = 0;
        double leakedMass = 0;
        int minimumLeakY = snapshot.Height;
        int maximumLeakY = 0;
        int minimumLeakX = snapshot.Width;
        int maximumLeakX = 0;
        int wallGaps = 0;
        foreach (GridCell cell in grid)
        {
            if (cell.IsActive == 0 || cell.MaterialId != (uint)MaterialId.Water)
            {
                continue;
            }
            water++;
            resting += cell.RestFrames >= 60 ? 1 : 0;
            moving += Speed(cell) > 0.02f ? 1 : 0;
        }
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive != 0 && cell.MaterialId == (uint)MaterialId.Water &&
                    ((x < 10 || x > 470) || y > 249))
                {
                    leaks++;
                    leakedMass += cell.Mass;
                    minimumLeakY = Math.Min(minimumLeakY, y);
                    maximumLeakY = Math.Max(maximumLeakY, y);
                    minimumLeakX = Math.Min(minimumLeakX, x);
                    maximumLeakX = Math.Max(maximumLeakX, x);
                }
            }
        }
        for (int y = 95; y <= 254; y++)
        {
            wallGaps += HasMaterial(grid, snapshot.Width, 10, 20, y, MaterialId.Metal) ? 0 : 1;
            wallGaps += HasMaterial(grid, snapshot.Width, 255, 265, y, MaterialId.Metal) ? 0 : 1;
        }
        for (int x = 15; x <= 260; x++)
        {
            wallGaps += HasMaterial(grid, snapshot.Width, x, x, 245, 255, MaterialId.Metal) ? 0 : 1;
        }
        string equalImage = Path.Combine(artifactDirectory, "D_equal_2s.png");
        string waterfallImage = Path.Combine(artifactDirectory, "D_waterfall.png");
        int imageLeft = ImageSurfaceTop(equalImage, 25, 128, 100, 245);
        int imageRight = ImageSurfaceTop(equalImage, 148, 250, 100, 245);
        WaterVisualMetrics leftVisual = AnalyzeWater(equalImage, 25, 128, 100, 245);
        WaterVisualMetrics rightVisual = AnalyzeWater(equalImage, 148, 250, 100, 245);
        int fallingWater = CountColor(waterfallImage, 330, 80, 415, 220, IsBlue);
        ColorMetrics colors = AnalyzeColors(Path.Combine(artifactDirectory, "D_rest.png"));
        bool passed = water > 5000 && Math.Abs(leftTop - rightTop) <= 3 &&
            Math.Abs(imageLeft - imageRight) <= 3 && waterfallTop > 0 &&
            leftVisual.Gaps == 0 && rightVisual.Gaps == 0 &&
            resting >= water * 0.99 && moving == 0 && leaks == 0 &&
            framesPerSecond >= 55 && colors.Red == 0 && colors.Blue > 500 &&
            fallingWater > 100;
        report = $"PHYXEL_D water={water} leftTop={leftTop} rightTop={rightTop} image2s={imageLeft}/{imageRight} waterfallTop={waterfallTop} fallingWater={fallingWater} resting={resting} moving={moving} leaks={leaks} leakMass={leakedMass:0.000} leakBounds={minimumLeakX},{minimumLeakY}-{maximumLeakX},{maximumLeakY} wallGaps={wallGaps} fps={framesPerSecond:0.0} statsMoving={statistics.MovingCells} red={colors.Red}";
        return passed;
    }

    private static ComponentMetrics Components(
        GridCell[] grid,
        int width,
        int height,
        MaterialId material)
    {
        bool[] visited = new bool[grid.Length];
        int[] queue = new int[grid.Length];
        int components = 0;
        int largest = 0;
        int minX = width;
        int maxX = 0;
        int minY = height;
        int maxY = 0;
        for (int start = 0; start < grid.Length; start++)
        {
            if (visited[start] || grid[start].IsActive == 0 || grid[start].MaterialId != (uint)material)
            {
                continue;
            }
            components++;
            int head = 0;
            int tail = 0;
            queue[tail++] = start;
            visited[start] = true;
            int size = 0;
            while (head < tail)
            {
                int index = queue[head++];
                int x = index % width;
                int y = index / width;
                size++;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
                Enqueue(grid, visited, queue, ref tail, index - 1, x > 0, material);
                Enqueue(grid, visited, queue, ref tail, index + 1, x + 1 < width, material);
                Enqueue(grid, visited, queue, ref tail, index - width, y > 0, material);
                Enqueue(grid, visited, queue, ref tail, index + width, y + 1 < height, material);
            }
            largest = Math.Max(largest, size);
        }
        return new ComponentMetrics(components, largest, minX, maxX, minY, maxY);
    }

    private static void Enqueue(
        GridCell[] grid,
        bool[] visited,
        int[] queue,
        ref int tail,
        int index,
        bool valid,
        MaterialId material)
    {
        if (!valid || visited[index] || grid[index].IsActive == 0 || grid[index].MaterialId != (uint)material)
        {
            return;
        }
        visited[index] = true;
        queue[tail++] = index;
    }

    private static float Angle(int[] surface, int edge, int peak)
    {
        int horizontal = Math.Max(1, Math.Abs(peak - edge));
        int vertical = Math.Max(0, surface[edge] - surface[peak]);
        return MathF.Atan2(vertical, horizontal) * 180 / MathF.PI;
    }

    private static int SurfaceTop(
        ReadOnlySpan<GridCell> grid,
        int width,
        int left,
        int right,
        int top,
        int bottom)
    {
        List<int> tops = [];
        for (int x = left; x <= right; x++)
        {
            for (int y = top; y <= bottom; y++)
            {
                GridCell cell = grid[y * width + x];
                if (cell.IsActive != 0 && cell.MaterialId == (uint)MaterialId.Water)
                {
                    tops.Add(y);
                    break;
                }
            }
        }
        if (tops.Count == 0) return -1;
        tops.Sort();
        return tops[tops.Count / 2];
    }

    private static int ImageSurfaceTop(string path, int left, int right, int top, int bottom)
    {
        if (!File.Exists(path)) return -1000;
        using Bitmap bitmap = new(path);
        List<int> tops = [];
        for (int x = left; x <= right; x++)
        {
            for (int y = top; y <= bottom; y++)
            {
                if (IsBlue(bitmap.GetPixel(x, y)))
                {
                    tops.Add(y);
                    break;
                }
            }
        }
        if (tops.Count == 0) return -1000;
        tops.Sort();
        return tops[tops.Count / 2];
    }

    private static ColorMetrics AnalyzeColors(string path)
    {
        if (!File.Exists(path)) return default;
        using Bitmap bitmap = new(path);
        int red = 0;
        int blue = 0;
        int yellow = 0;
        int metal = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                Color color = bitmap.GetPixel(x, y);
                red += color.R > 120 && color.R > color.G * 1.35 && color.R > color.B * 1.35 ? 1 : 0;
                blue += IsBlue(color) ? 1 : 0;
                yellow += IsYellow(color) ? 1 : 0;
                metal += IsMetal(color) ? 1 : 0;
            }
        }
        return new ColorMetrics(red, blue, yellow, metal);
    }

    private static WaterVisualMetrics AnalyzeWater(
        string path,
        int left,
        int right,
        int top,
        int bottom)
    {
        if (!File.Exists(path)) return default;
        using Bitmap bitmap = new(path);
        int columns = 0;
        int gaps = 0;
        int minimumTop = bottom;
        int maximumTop = top;
        for (int x = left; x <= right; x++)
        {
            int first = -1;
            int last = -1;
            for (int y = top; y <= bottom; y++)
            {
                if (!IsBlue(bitmap.GetPixel(x, y))) continue;
                first = first < 0 ? y : first;
                last = y;
            }
            if (first < 0) continue;
            columns++;
            minimumTop = Math.Min(minimumTop, first);
            maximumTop = Math.Max(maximumTop, first);
            for (int y = first; y <= last; y++)
            {
                gaps += IsBlue(bitmap.GetPixel(x, y)) ? 0 : 1;
            }
        }
        return new WaterVisualMetrics(columns, gaps, maximumTop - minimumTop);
    }

    private static int CountColor(
        string path,
        int left,
        int top,
        int right,
        int bottom,
        Func<Color, bool> predicate)
    {
        if (!File.Exists(path)) return 0;
        using Bitmap bitmap = new(path);
        int count = 0;
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                count += predicate(bitmap.GetPixel(x, y)) ? 1 : 0;
            }
        }
        return count;
    }

    private static bool HasMaterial(
        ReadOnlySpan<GridCell> grid,
        int width,
        int left,
        int right,
        int y,
        MaterialId material)
    {
        for (int x = left; x <= right; x++)
        {
            GridCell cell = grid[y * width + x];
            if (cell.IsActive != 0 && cell.MaterialId == (uint)material) return true;
        }
        return false;
    }

    private static int CountMaterial(
        ReadOnlySpan<GridCell> grid,
        int width,
        int left,
        int right,
        int top,
        int bottom,
        MaterialId material)
    {
        int count = 0;
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                GridCell cell = grid[y * width + x];
                count += cell.IsActive != 0 && cell.MaterialId == (uint)material ? 1 : 0;
            }
        }
        return count;
    }

    private static bool HasMaterial(
        ReadOnlySpan<GridCell> grid,
        int width,
        int x,
        int ignored,
        int top,
        int bottom,
        MaterialId material)
    {
        for (int y = top; y <= bottom; y++)
        {
            GridCell cell = grid[y * width + x];
            if (cell.IsActive != 0 && cell.MaterialId == (uint)material) return true;
        }
        return false;
    }

    private static ReadOnlySpan<GridCell> Cells(SimulationWorldSnapshot snapshot)
    {
        return MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
    }

    private static float Speed(GridCell cell)
    {
        return Math.Abs(cell.VelocityX) + Math.Abs(cell.VelocityY);
    }

    private static bool IsBlue(Color color) => color.B > color.G + 35 && color.G > color.R + 35;
    private static bool IsYellow(Color color) => color.R > 160 && color.G > 130 && color.B < 130;
    private static bool IsMetal(Color color) => color.R is >= 125 and <= 160 && color.G is >= 140 and <= 175 && color.B is >= 145 and <= 185;
    private static bool IsConcrete(Color color) => color.R is >= 75 and <= 110 && color.G is >= 80 and <= 115 && color.B is >= 85 and <= 125;
    private static bool Fail(out string report)
    {
        report = "PHYXEL_ACCEPTANCE_MODE_MISSING";
        return false;
    }

    private readonly record struct ColorMetrics(int Red, int Blue, int Yellow, int Metal);
    private readonly record struct WaterVisualMetrics(int Columns, int Gaps, int SurfaceRange);
}
