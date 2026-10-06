using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Phyxel.Core;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Input;

public sealed class CanvasBrushController
{
    private readonly List<BrushDrawCommand> frameCommands = new(SimulationSettings.MaximumBrushCommands);
    private Point previousGridPosition;
    private bool strokeActive;
    private uint commandSeed;
    private uint activeBodyId;
    private uint nextBodyId = 1;
    private Rectangle previousCanvasBounds;
    private BrushDrawCommand? pendingLine;
    private bool lineUsesRightButton;
    private bool suppressUntilRelease;

    public BrushDrawCommand? LinePreview => pendingLine;
    public bool CommandsStartStroke { get; private set; }

    public void CancelStroke()
    {
        suppressUntilRelease |= strokeActive;
        strokeActive = false;
        pendingLine = null;
    }

    public IReadOnlyList<BrushDrawCommand> CreateCommands(
        RawInputSnapshot input,
        Rectangle canvasBounds,
        SimulationSettings settings,
        ushort selectedMaterial,
        bool selectedMaterialIsTool,
        bool temperatureToolActive,
        float targetTemperature,
        bool pointerConsumedByUi,
        bool thermalDevice = false,
        float deviceTargetTemperature = 20,
        float deviceMaximumPower = 0,
        bool filterTool = false,uint filterRule = 0)
    {
        frameCommands.Clear();
        CommandsStartStroke = false;
        if (canvasBounds != previousCanvasBounds)
        {
            CancelStroke();
            previousCanvasBounds = canvasBounds;
        }

        bool drawing = input.LeftDown || input.RightDown;
        if (suppressUntilRelease)
        {
            if (!drawing) suppressUntilRelease = false;
            return frameCommands;
        }
        if (pendingLine is { } line)
        {
            if (pointerConsumedByUi || !canvasBounds.Contains(input.MousePosition))
            {
                CancelStroke();
                suppressUntilRelease &= drawing;
                return frameCommands;
            }
            Point end = MapToGrid(input.MousePosition, canvasBounds, settings);
            line.EndX = end.X;
            line.EndY = end.Y;
            pendingLine = line;
            bool released = lineUsesRightButton ? input.RightReleased : input.LeftReleased;
            bool held = lineUsesRightButton ? input.RightDown : input.LeftDown;
            if (released)
            {
                CommandsStartStroke = true;
                frameCommands.Add(line);
                pendingLine = null;
                strokeActive = false;
                suppressUntilRelease = drawing;
            }
            else if (!held)
            {
                CancelStroke();
                suppressUntilRelease &= drawing;
            }
            return frameCommands;
        }
        if (!drawing || pointerConsumedByUi || !canvasBounds.Contains(input.MousePosition))
        {
            strokeActive = false;
            return frameCommands;
        }

        Point gridPosition = MapToGrid(input.MousePosition, canvasBounds, settings);
        bool startingStroke = !strokeActive;
        CommandsStartStroke = startingStroke && !input.ShiftDown;
        if (startingStroke && input.ShiftDown && !input.LeftPressed && !input.RightPressed)
            return frameCommands;
        if (!strokeActive)
        {
            previousGridPosition = gridPosition;
            activeBodyId = nextBodyId;
            nextBodyId = nextBodyId == uint.MaxValue ? 1 : nextBodyId + 1;
            strokeActive = true;
        }

        bool erasing = input.RightDown || !temperatureToolActive && selectedMaterialIsTool;
        BrushCommandMode mode = input.RightDown ? BrushCommandMode.Erase : filterTool ? BrushCommandMode.Filter : erasing
            ? BrushCommandMode.Erase
            : temperatureToolActive
                ? BrushCommandMode.SetTemperature
                : thermalDevice ? BrushCommandMode.ThermalDevice : BrushCommandMode.Material;
        AppendStrokeCommand(
            previousGridPosition,
            gridPosition,
            selectedMaterial,
            mode,
            thermalDevice && !temperatureToolActive ? deviceTargetTemperature : targetTemperature,
            settings, deviceMaximumPower);
        if(mode == BrushCommandMode.Filter){var command=frameCommands[^1];command.Reserved=filterRule;frameCommands[^1]=command;}
        if (startingStroke && input.ShiftDown)
        {
            pendingLine = frameCommands[0];
            lineUsesRightButton = input.RightDown;
            frameCommands.Clear();
        }
        previousGridPosition = gridPosition;
        return frameCommands;
    }

    private void AppendStrokeCommand(
        Point start,
        Point end,
        ushort material,
        BrushCommandMode mode,
        float targetTemperature,
        SimulationSettings settings,
        float deviceMaximumPower)
    {
        frameCommands.Add(new BrushDrawCommand
        {
            X = start.X,
            Y = start.Y,
            EndX = end.X,
            EndY = end.Y,
            Shape = BrushCommandShape.Segment,
            MaterialIndex = material,
            Radius = settings.BrushRadius,
            Density = settings.SpawnDensity,
            Mode = mode,
            Seed = ++commandSeed,
            Reserved = mode == BrushCommandMode.ThermalDevice
                ? BitConverter.SingleToUInt32Bits(Math.Clamp(float.IsFinite(deviceMaximumPower) ? deviceMaximumPower : 0,
                    0, ThermalRegulator.MaximumPower)) : activeBodyId,
            TargetTemperature = mode is BrushCommandMode.SetTemperature or BrushCommandMode.ThermalDevice
                ? Math.Clamp(
                    float.IsFinite(targetTemperature) ? targetTemperature : 20f,
                    MaterialRegistry.MinimumInitialTemperature,
                    MaterialRegistry.MaximumInitialTemperature)
                : 0
        });
    }

    private static Point MapToGrid(Point pointer, Rectangle canvas, SimulationSettings settings)
    {
        float horizontal = (pointer.X - canvas.X) / (float)Math.Max(1, canvas.Width);
        float vertical = (pointer.Y - canvas.Y) / (float)Math.Max(1, canvas.Height);
        return new Point(
            Math.Clamp((int)(horizontal * settings.Width), 0, settings.Width - 1),
            Math.Clamp((int)(vertical * settings.Height), 0, settings.Height - 1));
    }
}
