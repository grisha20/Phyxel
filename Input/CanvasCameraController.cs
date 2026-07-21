using System;
using Microsoft.Xna.Framework;

namespace Phyxel.Input;

public sealed class CanvasCameraController
{
    private const float MinimumZoom = 1f;
    private const float MaximumZoom = 4f;
    private const float ZoomStep = 0.25f;

    private Vector2 center = new(0.5f, 0.5f);
    private Point previousPointer;
    private Rectangle previousCanvas;
    private bool dragging;

    public float Zoom { get; private set; } = MinimumZoom;

    public Rectangle Update(
        RawInputSnapshot input,
        Rectangle canvas,
        Rectangle fittedWorldBounds,
        bool active,
        bool pointerConsumedByUi)
    {
        if (canvas != previousCanvas)
        {
            dragging = false;
            previousCanvas = canvas;
        }

        bool pointerInside = canvas.Contains(input.MousePosition) && !pointerConsumedByUi;
        if (!active || !pointerInside)
        {
            dragging = false;
            return GetWorldBounds(fittedWorldBounds);
        }

        if (input.WheelDelta != 0)
        {
            float oldZoom = Zoom;
            Zoom = Math.Clamp(
                Zoom + Math.Sign(input.WheelDelta) * ZoomStep,
                MinimumZoom,
                MaximumZoom);
            if (Zoom != oldZoom)
            {
                KeepPointerAnchored(input.MousePosition, fittedWorldBounds, oldZoom);
            }
        }

        if (input.LeftDown)
        {
            if (dragging)
            {
                int width = Math.Max(1, (int)MathF.Round(fittedWorldBounds.Width * Zoom));
                int height = Math.Max(1, (int)MathF.Round(fittedWorldBounds.Height * Zoom));
                center.X -= (input.MousePosition.X - previousPointer.X) / (float)width;
                center.Y -= (input.MousePosition.Y - previousPointer.Y) / (float)height;
                ClampCenter();
            }
            previousPointer = input.MousePosition;
            dragging = true;
        }
        else
        {
            dragging = false;
        }

        return GetWorldBounds(fittedWorldBounds);
    }

    public Rectangle GetWorldBounds(Rectangle fittedWorldBounds)
    {
        int width = Math.Max(1, (int)MathF.Round(fittedWorldBounds.Width * Zoom));
        int height = Math.Max(1, (int)MathF.Round(fittedWorldBounds.Height * Zoom));
        int x = fittedWorldBounds.Center.X - (int)MathF.Round(center.X * width);
        int y = fittedWorldBounds.Center.Y - (int)MathF.Round(center.Y * height);
        return new Rectangle(x, y, width, height);
    }

    public void Reset()
    {
        Zoom = MinimumZoom;
        center = new Vector2(0.5f, 0.5f);
        dragging = false;
    }

    private void KeepPointerAnchored(Point pointer, Rectangle fittedWorldBounds, float oldZoom)
    {
        int oldWidth = Math.Max(1, (int)MathF.Round(fittedWorldBounds.Width * oldZoom));
        int oldHeight = Math.Max(1, (int)MathF.Round(fittedWorldBounds.Height * oldZoom));
        float oldLeft = fittedWorldBounds.Center.X - center.X * oldWidth;
        float oldTop = fittedWorldBounds.Center.Y - center.Y * oldHeight;
        float worldX = (pointer.X - oldLeft) / oldWidth;
        float worldY = (pointer.Y - oldTop) / oldHeight;

        int newWidth = Math.Max(1, (int)MathF.Round(fittedWorldBounds.Width * Zoom));
        int newHeight = Math.Max(1, (int)MathF.Round(fittedWorldBounds.Height * Zoom));
        center.X = (fittedWorldBounds.Center.X - pointer.X + worldX * newWidth) / newWidth;
        center.Y = (fittedWorldBounds.Center.Y - pointer.Y + worldY * newHeight) / newHeight;
        ClampCenter();
    }

    private void ClampCenter()
    {
        float halfVisible = 0.5f / Zoom;
        center.X = Math.Clamp(center.X, halfVisible, 1f - halfVisible);
        center.Y = Math.Clamp(center.Y, halfVisible, 1f - halfVisible);
    }
}
