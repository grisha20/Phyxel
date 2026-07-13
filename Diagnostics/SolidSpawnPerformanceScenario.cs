using System.Collections.Generic;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

public static class SolidSpawnPerformanceScenario
{
    public static IReadOnlyList<BrushDrawCommand> CreateCommands(uint frameIndex, int width, int height)
    {
        if (frameIndex >= 120)
        {
            return [];
        }

        int stroke = (int)(frameIndex / 12);
        int segment = (int)(frameIndex % 12);
        int startX = 80 + segment * 12;
        int y = 90 + stroke * System.Math.Max(35, (height - 180) / 10);
        List<BrushDrawCommand> commands = [];
        for (int offset = 0; offset < 12; offset++)
        {
            commands.Add(new BrushDrawCommand
            {
                X = System.Math.Min(width - 5, startX + offset),
                Y = System.Math.Min(height - 5, y),
                MaterialId = (uint)MaterialId.Metal,
                Radius = 2.5f,
                Density = 1,
                Seed = frameIndex * 32 + (uint)offset,
                Reserved = 1000
            });
        }

        return commands;
    }
}
