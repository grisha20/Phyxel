using System.Collections.Generic;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

public static class SolidSpawnRegressionScenario
{
    public static IReadOnlyList<BrushDrawCommand> CreateCommands(uint frameIndex)
    {
        return frameIndex switch
        {
            0 => CreateLine(12, 22, 10, 201),
            1 => CreateLine(12, 38, 50, 202),
            2 => CreateLine(12, 54, 100, 203),
            3 => CreateLine(12, 70, 200, 204),
            4 => [CreateCommand(300, 72, 25, 205, 4000)],
            5 => [CreateCommand(380, 30, 2.5f, 206, 5000), CreateCommand(400, 62, 10, 207, 5001)],
            >= 6 and < 16 => CreateLine(240, 100 + ((int)frameIndex - 6) * 12, 20, 210 + frameIndex - 6),
            _ => []
        };
    }

    private static IReadOnlyList<BrushDrawCommand> CreateLine(int x, int y, int length, uint bodyId)
    {
        List<BrushDrawCommand> commands = new(length);
        for (int offset = 0; offset < length; offset++)
        {
            commands.Add(CreateCommand(x + offset, y, 0.5f, bodyId, bodyId * 1000 + (uint)offset));
        }

        return commands;
    }

    private static BrushDrawCommand CreateCommand(int x, int y, float radius, uint bodyId, uint seed)
    {
        return new BrushDrawCommand
        {
            X = x,
            Y = y,
            MaterialId = (uint)MaterialId.Metal,
            Radius = radius,
            Density = 1,
            Seed = seed,
            Reserved = bodyId
        };
    }
}
