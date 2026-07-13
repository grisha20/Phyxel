using System;
using System.Collections.Generic;
using Phyxel.Core;
using Phyxel.Physics;

namespace Phyxel.Input;

public sealed class GpuCommandEncoder
{
    private readonly BrushDrawCommand[] commands = new BrushDrawCommand[SimulationSettings.MaximumBrushCommands];

    public ReadOnlySpan<BrushDrawCommand> Encode(IReadOnlyList<BrushDrawCommand> source)
    {
        int count = Math.Min(source.Count, commands.Length);
        for (int index = 0; index < count; index++)
        {
            commands[index] = source[index];
        }

        return commands.AsSpan(0, count);
    }
}
