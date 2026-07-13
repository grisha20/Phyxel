using System;
using System.Runtime.InteropServices;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

public static class SandWaterRegressionVerifier
{
    public static bool Validate(SimulationWorldSnapshot snapshot, out string report)
    {
        ReadOnlySpan<GridCell> grid = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        double sandY = 0;
        double waterY = 0;
        int sandCount = 0;
        int waterCount = 0;
        int bottomSand = 0;
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 101; x < 380; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive == 0)
                {
                    continue;
                }

                MaterialId material = (MaterialId)cell.MaterialId;
                if (material == MaterialId.Sand)
                {
                    sandY += y;
                    sandCount++;
                    bottomSand += y >= 225 ? 1 : 0;
                }
                else if (material == MaterialId.Water)
                {
                    waterY += y;
                    waterCount++;
                }
            }
        }

        double averageSandY = sandCount == 0 ? 0 : sandY / sandCount;
        double averageWaterY = waterCount == 0 ? snapshot.Height : waterY / waterCount;
        float bottomRatio = sandCount == 0 ? 0 : bottomSand / (float)sandCount;
        bool settled = sandCount > 500 && waterCount > 5000 && averageSandY > averageWaterY + 18 && bottomRatio > 0.78f;
        report = $"PHYXEL_SAND_WATER_METRICS sand={sandCount} water={waterCount} sandY={averageSandY:0.0} waterY={averageWaterY:0.0} bottomRatio={bottomRatio:0.000}";
        return settled;
    }
}
