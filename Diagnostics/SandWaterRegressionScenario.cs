using System.Collections.Generic;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

public static class SandWaterRegressionScenario
{
    public static IReadOnlyList<BrushDrawCommand> CreateCommands(uint frameIndex)
    {
        return frameIndex switch
        {
            0 => CreateBasin(),
            1 => CreateFill(MaterialId.Water, 112, 180, 368, 252, 6, 10),
            2 => CreateFill(MaterialId.Sand, 220, 125, 260, 175, 4, 6),
            _ => []
        };
    }

    private static IReadOnlyList<BrushDrawCommand> CreateBasin()
    {
        List<BrushDrawCommand> commands = [];
        AddLine(commands, 100, 80, 100, 258);
        AddLine(commands, 380, 80, 380, 258);
        AddLine(commands, 100, 258, 380, 258);
        return commands;
    }

    private static IReadOnlyList<BrushDrawCommand> CreateFill(
        MaterialId material,
        int left,
        int top,
        int right,
        int bottom,
        int radius,
        int spacing)
    {
        List<BrushDrawCommand> commands = [];
        for (int y = top; y <= bottom; y += spacing)
        {
            for (int x = left; x <= right; x += spacing)
            {
                commands.Add(Create(x, y, radius, material, 0));
            }
        }

        return commands;
    }

    private static void AddLine(List<BrushDrawCommand> commands, int startX, int startY, int endX, int endY)
    {
        int length = System.Math.Max(System.Math.Abs(endX - startX), System.Math.Abs(endY - startY));
        int samples = System.Math.Max(1, length / 3);
        for (int sample = 0; sample <= samples; sample++)
        {
            float amount = sample / (float)samples;
            commands.Add(Create(
                (int)System.MathF.Round(startX + (endX - startX) * amount),
                (int)System.MathF.Round(startY + (endY - startY) * amount),
                2,
                MaterialId.Concrete,
                401));
        }
    }

    private static BrushDrawCommand Create(int x, int y, float radius, MaterialId material, uint bodyId)
    {
        return new BrushDrawCommand
        {
            X = x,
            Y = y,
            MaterialId = (uint)material,
            Radius = radius,
            Density = 1,
            Seed = (uint)(y * 512 + x),
            Reserved = bodyId
        };
    }
}
