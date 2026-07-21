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
    private Point strokeOrigin;
    private bool strokeActive;
    private uint commandSeed;
    private uint activeBodyId;
    private uint nextBodyId = 1;
    private Rectangle previousCanvasBounds;

    public IReadOnlyList<BrushDrawCommand> CreateCommands(
        RawInputSnapshot input,
        Rectangle canvasBounds,
        SimulationSettings settings,
        ushort selectedMaterial,
        bool selectedMaterialIsTool,
        bool temperatureToolActive,
        float targetTemperature,
        bool pointerConsumedByUi)
    {
        frameCommands.Clear();
        if (canvasBounds != previousCanvasBounds)
        {
            strokeActive = false;
            previousCanvasBounds = canvasBounds;
        }

        bool drawing = input.LeftDown || input.RightDown;
        if (!drawing || pointerConsumedByUi || !canvasBounds.Contains(input.MousePosition))
        {
            strokeActive = false;
            return frameCommands;
        }

        Point gridPosition = MapToGrid(input.MousePosition, canvasBounds, settings);
        if (!strokeActive)
        {
            previousGridPosition = gridPosition;
            strokeOrigin = gridPosition;
            activeBodyId = nextBodyId;
            nextBodyId = nextBodyId == uint.MaxValue ? 1 : nextBodyId + 1;
            strokeActive = true;
        }

        if (input.ShiftDown)
        {
            gridPosition = SnapOrthogonally(strokeOrigin, gridPosition);
        }

        bool erasing = input.RightDown || !temperatureToolActive && selectedMaterialIsTool;
        BrushCommandMode mode = erasing
            ? BrushCommandMode.Erase
            : temperatureToolActive
                ? BrushCommandMode.SetTemperature
                : BrushCommandMode.Material;
        AppendStrokeCommand(
            previousGridPosition,
            gridPosition,
            selectedMaterial,
            mode,
            targetTemperature,
            settings);
        previousGridPosition = gridPosition;
        return frameCommands;
    }

    private void AppendStrokeCommand(
        Point start,
        Point end,
        ushort material,
        BrushCommandMode mode,
        float targetTemperature,
        SimulationSettings settings)
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
            Reserved = activeBodyId,
            TargetTemperature = mode == BrushCommandMode.SetTemperature
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

    private static Point SnapOrthogonally(Point origin, Point current)
    {
        int horizontalDistance = Math.Abs(current.X - origin.X);
        int verticalDistance = Math.Abs(current.Y - origin.Y);
        return horizontalDistance >= verticalDistance
            ? new Point(current.X, origin.Y)
            : new Point(origin.X, current.Y);
    }
}
