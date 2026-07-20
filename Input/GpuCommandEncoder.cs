using System;
using System.Collections.Generic;
using Phyxel.Core;
using Phyxel.Materials;
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
            BrushDrawCommand command = source[index];
            if (command.Mode is not (
                BrushCommandMode.Material or
                BrushCommandMode.Erase or
                BrushCommandMode.SetTemperature))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(source),
                    command.Mode,
                    "Unsupported brush command mode.");
            }
            if (command.Shape is not (BrushCommandShape.Point or BrushCommandShape.Segment))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(source),
                    command.Shape,
                    "Unsupported brush command shape.");
            }
            if (command.Mode == BrushCommandMode.SetTemperature)
            {
                if (!float.IsFinite(command.TargetTemperature))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(source),
                        command.TargetTemperature,
                        "Target temperature must be finite.");
                }
                command.TargetTemperature = Math.Clamp(
                    command.TargetTemperature,
                    MaterialRegistry.MinimumInitialTemperature,
                    MaterialRegistry.MaximumInitialTemperature);
            }
            commands[index] = command;
        }

        return commands.AsSpan(0, count);
    }
}
