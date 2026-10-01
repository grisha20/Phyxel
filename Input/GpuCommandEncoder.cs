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
                BrushCommandMode.SetTemperature or BrushCommandMode.ThermalDevice))
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
            if (command.Mode is BrushCommandMode.SetTemperature or BrushCommandMode.ThermalDevice)
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
            if (command.Mode == BrushCommandMode.ThermalDevice)
            {
                float power = BitConverter.UInt32BitsToSingle(command.Reserved);
                if (!float.IsFinite(power)) throw new ArgumentOutOfRangeException(nameof(source), "Device power must be finite.");
                command.Reserved = BitConverter.SingleToUInt32Bits(Math.Clamp(power, 0, ThermalRegulator.MaximumPower));
            }
            commands[index] = command;
        }

        return commands.AsSpan(0, count);
    }
}
