using System;
using System.Collections.Generic;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

public static class CriticalRegressionScenario
{
    public const uint MetalBodyId = 11;
    public const uint ConcreteBodyId = 12;

    public static IReadOnlyList<BrushDrawCommand> CreateCommands(uint frameIndex, int width, int height)
    {
        List<BrushDrawCommand> commands = [];
        if (frameIndex == 0)
        {
            int metalY = Math.Max(24, height / 5);
            for (int x = 30; x <= 130; x++)
            {
                commands.Add(Create(x, metalY, MaterialId.Metal, 0.5f, (uint)x, MetalBodyId, 0));
            }

            int centerX = Math.Min(width - 70, width * 2 / 3);
            int supportTop = Math.Max(60, height / 3);
            int supportBottom = Math.Min(height - 35, supportTop + 90);
            int radius = 42;
            for (int y = supportTop; y <= supportBottom; y += 3)
            {
                commands.Add(Create(centerX - radius, y, MaterialId.Concrete, 2, (uint)(1000 + y), ConcreteBodyId, 0));
                commands.Add(Create(centerX + radius, y, MaterialId.Concrete, 2, (uint)(2000 + y), ConcreteBodyId, 0));
            }

            for (int x = -radius; x <= radius; x += 3)
            {
                int y = supportTop - (int)MathF.Sqrt(radius * radius - x * x);
                commands.Add(Create(centerX + x, y, MaterialId.Concrete, 2, (uint)(3000 + x + radius), ConcreteBodyId, 0));
            }
        }
        else if (frameIndex == 30)
        {
            int centerX = Math.Min(width - 70, width * 2 / 3);
            int supportTop = Math.Max(60, height / 3);
            commands.Add(Create(centerX - 42, supportTop + 62, MaterialId.Eraser, 8, 9000, 0, 1));
        }

        return commands;
    }

    private static BrushDrawCommand Create(
        int x,
        int y,
        MaterialId material,
        float radius,
        uint seed,
        uint bodyId,
        uint mode)
    {
        return new BrushDrawCommand
        {
            X = x,
            Y = y,
            MaterialId = (uint)material,
            Radius = radius,
            Density = 1,
            Mode = mode,
            Seed = seed,
            Reserved = bodyId
        };
    }
}
