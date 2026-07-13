using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

public static class HydrodynamicsRegressionVerifier
{
    public static bool Validate(SimulationWorldSnapshot snapshot, out string report)
    {
        ReadOnlySpan<GridCell> grid = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        float leftMass = 0;
        float rightMass = 0;
        int leftTop = snapshot.Height;
        int rightTop = snapshot.Height;
        List<int> waterfallSurface = [];
        List<int> leftSurface = [];
        List<int> rightSurface = [];
        int highWaterfallCells = 0;
        float maximumLeftPressure = 0;
        float maximumRightPressure = 0;
        int elevatedRightCells = 0;
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive == 0 || (MaterialId)cell.MaterialId != MaterialId.Water)
                {
                    continue;
                }

                if (x is > 8 and < 112)
                {
                    leftMass += cell.Mass;
                    leftTop = Math.Min(leftTop, y);
                    maximumLeftPressure = Math.Max(maximumLeftPressure, cell.Pressure);
                }
                else if (x is > 118 and < 222)
                {
                    rightMass += cell.Mass;
                    rightTop = Math.Min(rightTop, y);
                    maximumRightPressure = Math.Max(maximumRightPressure, cell.Pressure);
                    elevatedRightCells += y < 237 ? 1 : 0;
                }
                else if (x is > 253 and < 470 && y < 178)
                {
                    highWaterfallCells++;
                }
            }
        }

        for (int x = 255; x < 469; x++)
        {
            int top = snapshot.Height;
            for (int y = 178; y < 258; y++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive != 0 && (MaterialId)cell.MaterialId == MaterialId.Water)
                {
                    top = y;
                    break;
                }
            }

            if (top < snapshot.Height)
            {
                waterfallSurface.Add(top);
            }
        }

        CollectSurface(grid, snapshot.Width, snapshot.Height, 9, 112, leftSurface);
        CollectSurface(grid, snapshot.Width, snapshot.Height, 119, 222, rightSurface);

        float vesselDifference = Math.Abs(leftMass - rightMass) / Math.Max(leftMass + rightMass, 1);
        double waterfallDeviation = CalculateDeviation(waterfallSurface);
        double leftAverageTop = CalculateAverage(leftSurface);
        double rightAverageTop = CalculateAverage(rightSurface);
        bool communicatingVessels = leftMass > 2000 && rightMass > 2000 && vesselDifference < 0.08f &&
            Math.Abs(leftAverageTop - rightAverageTop) <= 4 &&
            CalculateDeviation(leftSurface) < 5 && CalculateDeviation(rightSurface) < 5;
        bool waterfallSettled = waterfallSurface.Count > 150 && waterfallDeviation < 4 && highWaterfallCells < 30;
        report = $"PHYXEL_HYDRO_METRICS leftMass={leftMass:0.0} rightMass={rightMass:0.0} difference={vesselDifference:0.000} leftTop={leftAverageTop:0.0} rightTop={rightAverageTop:0.0} leftPressure={maximumLeftPressure:0.0} rightPressure={maximumRightPressure:0.0} elevatedRight={elevatedRightCells} waterfallColumns={waterfallSurface.Count} surfaceDeviation={waterfallDeviation:0.00} airborne={highWaterfallCells}";
        return communicatingVessels && waterfallSettled;
    }

    private static void CollectSurface(
        ReadOnlySpan<GridCell> grid,
        int width,
        int height,
        int left,
        int right,
        List<int> values)
    {
        for (int x = left; x < right; x++)
        {
            for (int y = 0; y < height; y++)
            {
                GridCell cell = grid[y * width + x];
                if (cell.IsActive != 0 && (MaterialId)cell.MaterialId == MaterialId.Water)
                {
                    values.Add(y);
                    break;
                }
            }
        }
    }

    private static double CalculateAverage(IReadOnlyList<int> values)
    {
        if (values.Count == 0)
        {
            return double.MaxValue;
        }

        double total = 0;
        foreach (int value in values)
        {
            total += value;
        }

        return total / values.Count;
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
