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

    public IReadOnlyList<BrushDrawCommand> CreateCommands(
        RawInputSnapshot input,
        Rectangle canvasBounds,
        SimulationSettings settings,
        MaterialId selectedMaterial,
        bool pointerConsumedByUi)
    {
        frameCommands.Clear();
        if (input.WheelDelta != 0 && !pointerConsumedByUi)
        {
            settings.BrushRadius = Math.Clamp(settings.BrushRadius + Math.Sign(input.WheelDelta) * 2, 1, 96);
        }

        bool drawing = input.LeftDown || input.RightDown;
        if (!drawing || pointerConsumedByUi || !canvasBounds.Contains(input.MousePosition))
        {
            if (!drawing)
            {
                strokeActive = false;
            }

            return frameCommands;
        }

        Point gridPosition = MapToGrid(input.MousePosition, canvasBounds, settings);
        if (!strokeActive)
        {
            previousGridPosition = gridPosition;
            strokeOrigin = gridPosition;
            strokeActive = true;
        }

        if (input.ShiftDown)
        {
            gridPosition = SnapOrthogonally(strokeOrigin, gridPosition);
        }

        MaterialId material = input.RightDown ? MaterialId.Eraser : selectedMaterial;
        AppendInterpolatedCommands(previousGridPosition, gridPosition, material, settings);
        previousGridPosition = gridPosition;
        return frameCommands;
    }

    private void AppendInterpolatedCommands(Point start, Point end, MaterialId material, SimulationSettings settings)
    {
        int deltaX = end.X - start.X;
        int deltaY = end.Y - start.Y;
        float distance = MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);
        float spacing = MathF.Max(1f, settings.BrushRadius * 0.42f);
        int samples = Math.Max(1, (int)MathF.Ceiling(distance / spacing));
        for (int sample = 0; sample <= samples && frameCommands.Count < SimulationSettings.MaximumBrushCommands; sample++)
        {
            float interpolation = sample / (float)samples;
            frameCommands.Add(new BrushDrawCommand
            {
                X = (int)MathF.Round(start.X + deltaX * interpolation),
                Y = (int)MathF.Round(start.Y + deltaY * interpolation),
                MaterialId = (uint)material,
                Radius = settings.BrushRadius,
                Density = settings.SpawnDensity,
                Mode = material == MaterialId.Eraser ? 1u : 0u,
                Seed = ++commandSeed
            });
        }
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
