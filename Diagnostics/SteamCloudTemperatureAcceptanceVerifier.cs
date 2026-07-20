using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class SteamCloudTemperatureAcceptanceVerifier
{
    public static bool Validate(
        SimulationWorldSnapshot snapshot,
        MaterialRegistry materials,
        IReadOnlyList<ThermalAcceptanceCheckpoint> checkpoints,
        out string report)
    {
        List<string> errors = [];
        Require(checkpoints.Count == 11,
            $"steam cloud checkpoints expected=11 actual={checkpoints.Count}", errors);
        uint steam = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Steam);
        uint water = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        List<CloudStage> stages = checkpoints
            .Select(checkpoint => Measure(checkpoint.Snapshot, steam, water))
            .ToList();
        CloudStage final = Measure(snapshot, steam, water);

        if (checkpoints.Count > 0)
        {
            SimulationWorldSnapshot? initial = SteamCloudTemperatureAcceptanceScenario.CreateInitialWorld(
                AcceptanceScenarioMode.SteamCloudTemperature,
                snapshot.Width,
                snapshot.Height,
                materials);
            Require(checkpoints[0].ThermalTicks == 0 && initial is not null &&
                checkpoints[0].Snapshot.Grid.AsSpan().SequenceEqual(initial.Grid),
                "Pause changed steam movement or temperature before the first thermal tick", errors);
        }

        if (stages.Count >= 11)
        {
            Require(stages[1].TotalMass > stages[0].TotalMass + 100 &&
                stages[2].TotalMass > stages[1].TotalMass + 100,
                $"three sequential steam batches were not injected={Join(stages.Take(3))}", errors);
            double releasedMass = stages[2].TotalMass;
            foreach ((CloudStage stage, int index) in stages.Skip(2)
                .Select((stage, index) => (stage, index + 2)))
            {
                Require(Math.Abs(stage.TotalMass - releasedMass) <= 0.05,
                    $"steam/water mass changed at stage {index}={stage.TotalMass:F4} " +
                    $"expected={releasedMass:F4}", errors);
            }
            Require(Math.Abs(final.TotalMass - releasedMass) <= 0.05,
                $"final steam/water mass changed={final.TotalMass:F4} expected={releasedMass:F4}", errors);
            Require(stages.Take(7).All(stage => stage.BoundaryWaterCells == 0),
                $"dilute invisible steam condensed against a remote wall={Join(stages.Take(7))}", errors);

            CloudStage joined = stages[3];
            Require(joined.SteamMass > 0 && joined.HorizontalSpan >= 120 &&
                joined.VerticalSpan >= 55 && joined.SignificantClusters <= 3,
                $"steam batches did not form one broad cloud={joined}", errors);
            Require(joined.AdjacentTemperaturePairs >= 20 &&
                joined.MeanAdjacentTemperatureDifference < 0.5,
                $"visible thermal bands remained between steam batches={joined}", errors);

            CloudStage developed = stages[4];
            Require(developed.HorizontalSpan > developed.VerticalSpan * 1.45 &&
                developed.CeilingFraction < 0.35,
                $"steam collapsed into a narrow ceiling pile={developed}", errors);
            Require(developed.DenseTemperatureSamples >= 5 &&
                developed.EdgeTemperatureSamples >= 20 &&
                developed.DenseTemperature > developed.EdgeTemperature + 0.05,
                $"steam center is not warmer than its exposed edge={developed}", errors);

            int firstWaterIndex = stages.FindIndex(stage => stage.WaterMass >= 0.5);
            Require(firstWaterIndex is >= 7 and <= 10,
                $"condensation was not delayed and gradual firstWaterStage={firstWaterIndex}", errors);
            if (firstWaterIndex >= 0)
            {
                CloudStage firstWater = stages[firstWaterIndex];
                Require(firstWater.WaterEdgeFraction >= 0.55,
                    $"first condensed water did not occur predominantly at cloud edges={firstWater}", errors);
                Require(firstWater.WaterComponents >= 2 &&
                    firstWater.WaterMass < releasedMass * 0.01,
                    $"steam condensed as a wall instead of separate droplets={firstWater}", errors);
            }
            List<CloudStage> condensingStages = stages
                .Where(stage => stage.WaterMass >= 0.5 && stage.SteamMass > 0)
                .ToList();
            Require(condensingStages.Count >= 2 &&
                condensingStages.Zip(condensingStages.Skip(1),
                    (left, right) => right.WaterMass + 0.001 >= left.WaterMass).All(value => value),
                $"condensation did not remain progressive across checkpoints={Join(stages.Skip(5))}", errors);
            Require(final.WaterMass >= 0.5 &&
                final.WaterMass < releasedMass * 0.10 && final.SteamMass > releasedMass * 0.90,
                $"final capture did not preserve gradual condensation={final}", errors);
            Require(final.WaterCells <= final.WaterMass * 64 + 64,
                $"condensed water remained a field of near-empty dark cells={final}", errors);
        }

        report = "PHYXEL_STEAM_CLOUD " +
            Join(stages.Select((stage, index) => $"stage{index}={stage}")) +
            $" | final={final}";
        if (errors.Count == 0)
        {
            return true;
        }
        report += Environment.NewLine + "PHYXEL_STEAM_CLOUD_FAILURE " +
            string.Join("; ", errors.Take(16));
        return false;
    }

    private static CloudStage Measure(
        SimulationWorldSnapshot snapshot,
        uint steam,
        uint water)
    {
        ReadOnlySpan<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        List<(int X, int Y, float Temperature)> steamCells = [];
        List<(int X, int Y)> waterCells = [];
        double steamMass = 0;
        double waterMass = 0;
        int minX = int.MaxValue;
        int maxX = int.MinValue;
        int minY = int.MaxValue;
        int maxY = int.MinValue;
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = cells[y * snapshot.Width + x];
                if (cell.IsActive == 0) continue;
                if (cell.MaterialIndex == steam)
                {
                    steamMass += cell.Mass;
                    steamCells.Add((x, y, cell.Temperature));
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
                else if (cell.MaterialIndex == water)
                {
                    waterMass += cell.Mass;
                    waterCells.Add((x, y));
                }
            }
        }

        HashSet<int> steamSet = steamCells
            .Select(cell => cell.Y * snapshot.Width + cell.X)
            .ToHashSet();
        double denseTemperature = 0;
        double edgeTemperature = 0;
        int denseSamples = 0;
        int edgeSamples = 0;
        double adjacentDifference = 0;
        float maximumAdjacentDifference = 0;
        int adjacentPairs = 0;
        int ceilingCells = 0;
        foreach ((int x, int y, float temperature) in steamCells)
        {
            int immediateNeighbors = 0;
            int localNeighbors = 0;
            for (int dy = -4; dy <= 4; dy++)
            {
                for (int dx = -4; dx <= 4; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    if (steamSet.Contains((y + dy) * snapshot.Width + x + dx))
                    {
                        localNeighbors++;
                        if (Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1)
                        {
                            immediateNeighbors++;
                        }
                    }
                }
            }
            if (localNeighbors >= 12)
            {
                denseTemperature += temperature;
                denseSamples++;
            }
            if (immediateNeighbors <= 2)
            {
                edgeTemperature += temperature;
                edgeSamples++;
            }
            if (y <= SteamCloudTemperatureAcceptanceScenario.Top + 12) ceilingCells++;

            foreach ((int dx, int dy) in new[] { (1, 0), (0, 1) })
            {
                int neighborX = x + dx;
                int neighborY = y + dy;
                if (neighborX >= snapshot.Width || neighborY >= snapshot.Height) continue;
                GridCell neighbor = cells[neighborY * snapshot.Width + neighborX];
                if (neighbor.IsActive == 0 || neighbor.MaterialIndex != steam) continue;
                float difference = Math.Abs(temperature - neighbor.Temperature);
                adjacentDifference += difference;
                maximumAdjacentDifference = Math.Max(maximumAdjacentDifference, difference);
                adjacentPairs++;
            }
        }

        (int clusters, _) = Components(steamSet, snapshot.Width, 10);
        HashSet<int> waterSet = waterCells.Select(cell => cell.Y * snapshot.Width + cell.X).ToHashSet();
        (int waterComponents, int largestWaterComponent) = Components(waterSet, snapshot.Width, 1);
        int edgeWater = 0;
        int boundaryWater = 0;
        if (steamCells.Count > 0)
        {
            double centerX = (minX + maxX) * 0.5;
            double centerY = (minY + maxY) * 0.5;
            double halfWidth = Math.Max(1, (maxX - minX + 1) * 0.5);
            double halfHeight = Math.Max(1, (maxY - minY + 1) * 0.5);
            foreach ((int x, int y) in waterCells)
            {
                double nx = (x - centerX) / halfWidth;
                double ny = (y - centerY) / halfHeight;
                if (nx * nx + ny * ny >= 0.45 || y > maxY) edgeWater++;
                if (x <= SteamCloudTemperatureAcceptanceScenario.Left + 4 ||
                    x >= SteamCloudTemperatureAcceptanceScenario.Right - 4 ||
                    y <= SteamCloudTemperatureAcceptanceScenario.Top + 4 ||
                    y >= SteamCloudTemperatureAcceptanceScenario.Bottom - 4)
                {
                    boundaryWater++;
                }
            }
        }

        return new CloudStage(
            steamMass,
            waterMass,
            steamCells.Count,
            waterCells.Count,
            steamCells.Count > 0 ? maxX - minX + 1 : 0,
            steamCells.Count > 0 ? maxY - minY + 1 : 0,
            clusters,
            steamCells.Count > 0 ? ceilingCells / (double)steamCells.Count : 0,
            denseSamples > 0 ? denseTemperature / denseSamples : 0,
            edgeSamples > 0 ? edgeTemperature / edgeSamples : 0,
            denseSamples,
            edgeSamples,
            adjacentPairs > 0 ? adjacentDifference / adjacentPairs : 0,
            maximumAdjacentDifference,
            adjacentPairs,
            waterComponents,
            largestWaterComponent,
            waterCells.Count > 0 ? edgeWater / (double)waterCells.Count : 0,
            boundaryWater);
    }

    private static (int Count, int Largest) Components(HashSet<int> cells, int width, int radius)
    {
        HashSet<int> remaining = [.. cells];
        int count = 0;
        int largest = 0;
        Queue<int> queue = new();
        while (remaining.Count > 0)
        {
            int seed = remaining.First();
            remaining.Remove(seed);
            queue.Enqueue(seed);
            int size = 0;
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                size++;
                int currentX = current % width;
                int currentY = current / width;
                for (int dy = -radius; dy <= radius; dy++)
                {
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        if ((dx == 0 && dy == 0) || Math.Max(Math.Abs(dx), Math.Abs(dy)) > radius)
                        {
                            continue;
                        }
                        int neighborX = currentX + dx;
                        int neighborY = currentY + dy;
                        if (neighborX < 0 || neighborX >= width || neighborY < 0) continue;
                        int neighbor = neighborY * width + neighborX;
                        if (remaining.Remove(neighbor)) queue.Enqueue(neighbor);
                    }
                }
            }
            count++;
            largest = Math.Max(largest, size);
        }
        return (count, largest);
    }

    private static string Join<T>(IEnumerable<T> values) => string.Join(" | ", values);

    private static void Require(bool condition, string message, List<string> errors)
    {
        if (!condition) errors.Add(message);
    }

    private readonly record struct CloudStage(
        double SteamMass,
        double WaterMass,
        int SteamCells,
        int WaterCells,
        int HorizontalSpan,
        int VerticalSpan,
        int SignificantClusters,
        double CeilingFraction,
        double DenseTemperature,
        double EdgeTemperature,
        int DenseTemperatureSamples,
        int EdgeTemperatureSamples,
        double MeanAdjacentTemperatureDifference,
        double MaximumAdjacentTemperatureDifference,
        int AdjacentTemperaturePairs,
        int WaterComponents,
        int LargestWaterComponent,
        double WaterEdgeFraction,
        int BoundaryWaterCells)
    {
        public double TotalMass => SteamMass + WaterMass;

        public override string ToString() =>
            $"mass={TotalMass:F0} steam={SteamMass:F0}/{SteamCells} water={WaterMass:F0}/{WaterCells} " +
            $"span={HorizontalSpan}x{VerticalSpan} clusters={SignificantClusters} ceiling={CeilingFraction:P0} " +
            $"tempDense/edge={DenseTemperature:F2}/{EdgeTemperature:F2} " +
            $"adjacent={MeanAdjacentTemperatureDifference:F2}/{MaximumAdjacentTemperatureDifference:F2} " +
            $"droplets={WaterComponents}/{LargestWaterComponent} edgeWater={WaterEdgeFraction:P0} " +
            $"boundaryWater={BoundaryWaterCells}";
    }
}
