using System.Collections.Generic;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

public static class PhysicsRegressionScenario
{
    public static IReadOnlyList<BrushDrawCommand> CreateCommands(uint frameIndex, int width, int height)
    {
        List<BrushDrawCommand> commands = [];
        if (frameIndex == 0)
        {
            int floorY = height - 5;
            for (int x = 5; x < width - 5; x += 7)
            {
                commands.Add(CreateCommand(x, floorY, MaterialId.Concrete, 5, (uint)x));
            }
        }
        else if (frameIndex == 1)
        {
            for (int y = 105; y < height - 17; y += 7)
            {
                for (int x = 38; x <= 101; x += 7)
                {
                    commands.Add(CreateCommand(x, y, MaterialId.Water, 5, (uint)(y * width + x)));
                }
            }
        }
        else if (frameIndex == 2)
        {
            for (int x = 55; x <= 90; x += 5)
            {
                commands.Add(CreateCommand(x, 82, MaterialId.Sand, 4, (uint)(10000 + x)));
            }

            for (int x = width / 2; x <= width / 2 + 75; x += 5)
            {
                commands.Add(CreateCommand(x, 45, MaterialId.Metal, 4, (uint)(20000 + x)));
            }
        }
        else if (frameIndex == 3)
        {
            for (int x = 58; x <= 86; x += 5)
            {
                commands.Add(CreateCommand(x, height - 42, MaterialId.Gas, 4, (uint)(30000 + x)));
            }
        }

        return commands;
    }

    private static BrushDrawCommand CreateCommand(
        int x,
        int y,
        MaterialId materialId,
        float radius,
        uint seed)
    {
        return new BrushDrawCommand
        {
            X = x,
            Y = y,
            MaterialId = (uint)materialId,
            Radius = radius,
            Density = 1,
            Mode = 0,
            Seed = seed
        };
    }
}
