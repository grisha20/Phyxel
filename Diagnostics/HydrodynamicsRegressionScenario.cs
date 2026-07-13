using System.Collections.Generic;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

public static class HydrodynamicsRegressionScenario
{
    public static IReadOnlyList<BrushDrawCommand> CreateCommands(uint frameIndex)
    {
        return frameIndex switch
        {
            0 => CreateVesselShell(),
            1 => CreateVesselDivider(),
            2 => CreateWaterfallBasin(),
            3 => CreateVesselWater(),
            4 => CreateWaterfall(),
            _ => []
        };
    }

    private static IReadOnlyList<BrushDrawCommand> CreateVesselShell()
    {
        List<BrushDrawCommand> commands = [];
        AddLine(commands, 6, 12, 6, 258, 3, MaterialId.Concrete, 301);
        AddLine(commands, 224, 12, 224, 258, 3, MaterialId.Concrete, 301);
        AddLine(commands, 6, 258, 224, 258, 3, MaterialId.Concrete, 301);
        return commands;
    }

    private static IReadOnlyList<BrushDrawCommand> CreateVesselDivider()
    {
        List<BrushDrawCommand> commands = [];
        for (int y = 12; y <= 235; y++)
        {
            commands.Add(Create(115, y, 0.5f, MaterialId.Concrete, 302));
        }

        return commands;
    }

    private static IReadOnlyList<BrushDrawCommand> CreateWaterfallBasin()
    {
        List<BrushDrawCommand> commands = [];
        AddLine(commands, 250, 178, 250, 258, 3, MaterialId.Concrete, 303);
        AddLine(commands, 472, 178, 472, 258, 3, MaterialId.Concrete, 303);
        AddLine(commands, 250, 258, 472, 258, 3, MaterialId.Concrete, 303);
        return commands;
    }

    private static IReadOnlyList<BrushDrawCommand> CreateVesselWater()
    {
        List<BrushDrawCommand> commands = [];
        for (int y = 108; y <= 252; y += 8)
        {
            for (int x = 13; x <= 107; x += 8)
            {
                commands.Add(Create(x, y, 5, MaterialId.Water, 0));
            }
        }

        return commands;
    }

    private static IReadOnlyList<BrushDrawCommand> CreateWaterfall()
    {
        List<BrushDrawCommand> commands = [];
        for (int x = 325; x <= 397; x += 5)
        {
            commands.Add(Create(x, 56, 5, MaterialId.Water, 0));
        }

        return commands;
    }

    private static void AddLine(
        List<BrushDrawCommand> commands,
        int startX,
        int startY,
        int endX,
        int endY,
        int spacing,
        MaterialId material,
        uint bodyId)
    {
        int length = System.Math.Max(System.Math.Abs(endX - startX), System.Math.Abs(endY - startY));
        int samples = System.Math.Max(1, length / spacing);
        for (int sample = 0; sample <= samples; sample++)
        {
            float amount = sample / (float)samples;
            commands.Add(Create(
                (int)System.MathF.Round(startX + (endX - startX) * amount),
                (int)System.MathF.Round(startY + (endY - startY) * amount),
                2,
                material,
                bodyId));
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
