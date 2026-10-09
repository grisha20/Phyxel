using System;
using Microsoft.Xna.Framework;

namespace Phyxel.Input;

public sealed class CanvasCameraController
{
    private const float MinimumZoom = 1f;
    private const float MaximumZoom = 16f;
    private const float ZoomFactor = 1.25f;
    private const float ZoomResponse = 22f;

    private Vector2 center = new(0.5f, 0.5f);
    private Point previousPointer;
    private Rectangle previousCanvas;
    private Rectangle previousFittedBounds;
    private bool dragging;
    private float targetZoom = 1f;
    private Vector2 zoomAnchorWorld;
    private Point zoomAnchorScreen;

    public float Zoom { get; private set; } = 1f;
    public bool IsZooming => Zoom != targetZoom;

    public Rectangle Update(
        RawInputSnapshot input,
        Rectangle canvas,
        Rectangle fittedWorldBounds,
        bool panToolActive,
        bool pointerConsumedByUi)
    {
        if (canvas != previousCanvas || fittedWorldBounds != previousFittedBounds)
        {
            dragging = false;
            previousCanvas = canvas;
            previousFittedBounds = fittedWorldBounds;
            targetZoom = Zoom;
        }
        ClampToWorld(canvas, fittedWorldBounds);

        bool pointerInside = canvas.Contains(input.MousePosition) && !pointerConsumedByUi;
        if (pointerInside && input.WheelDelta != 0 && !input.ShiftDown)
        {
            Rectangle oldBounds = GetWorldBounds(fittedWorldBounds);
            targetZoom = Math.Clamp(
                targetZoom * MathF.Pow(ZoomFactor, input.WheelDelta / 120f),
                MinimumZoom,
                MaximumZoom);
            zoomAnchorScreen = input.MousePosition;
            zoomAnchorWorld = new Vector2(
                (input.MousePosition.X - oldBounds.X) / (float)oldBounds.Width,
                (input.MousePosition.Y - oldBounds.Y) / (float)oldBounds.Height);
        }

        if (IsZooming)
        {
            // Logarithmic interpolation gives the same feel at every zoom and FPS.
            float difference = MathF.Log(targetZoom / Zoom);
            float blend = 1f - MathF.Exp(-ZoomResponse * Math.Clamp(input.DeltaSeconds, 0f, .05f));
            Zoom = Math.Abs(difference) < .0001f ? targetZoom :
                Math.Clamp(Zoom * MathF.Exp(difference * blend), MinimumZoom, MaximumZoom);
            KeepPointerAnchored(fittedWorldBounds);
        }

        if (pointerInside && (input.MiddleDown || panToolActive && input.LeftDown))
        {
            if (dragging)
            {
                int width = Math.Max(1, (int)MathF.Round(fittedWorldBounds.Width * Zoom));
                int height = Math.Max(1, (int)MathF.Round(fittedWorldBounds.Height * Zoom));
                Point offset = input.MousePosition - previousPointer;
                center.X -= offset.X / (float)width;
                center.Y -= offset.Y / (float)height;
                // Continue an in-flight zoom from the location reached by dragging.
                zoomAnchorScreen += offset;
            }
            previousPointer = input.MousePosition;
            dragging = true;
        }
        else
        {
            dragging = false;
        }

        ClampToWorld(canvas, fittedWorldBounds);
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
        Zoom = targetZoom = 1f;
        center = new Vector2(0.5f, 0.5f);
        dragging = false;
    }

    private void KeepPointerAnchored(Rectangle fittedWorldBounds)
    {
        int newWidth = Math.Max(1, (int)MathF.Round(fittedWorldBounds.Width * Zoom));
        int newHeight = Math.Max(1, (int)MathF.Round(fittedWorldBounds.Height * Zoom));
        center.X = (fittedWorldBounds.Center.X - zoomAnchorScreen.X + zoomAnchorWorld.X * newWidth) / newWidth;
        center.Y = (fittedWorldBounds.Center.Y - zoomAnchorScreen.Y + zoomAnchorWorld.Y * newHeight) / newHeight;
    }

    private void ClampToWorld(Rectangle canvas, Rectangle fittedWorldBounds)
    {
        Rectangle view = GetWorldBounds(fittedWorldBounds);
        // The viewport must stay inside the world after both zoom and pan.
        // Use pixel bounds, including the fitted world's aspect/rounding, so a
        // single exposed row or column cannot accumulate through repeated zooms.
        int x = view.Width >= canvas.Width
            ? Math.Clamp(view.X, canvas.Right - view.Width, canvas.Left)
            : canvas.Center.X - view.Width / 2;
        int y = view.Height >= canvas.Height
            ? Math.Clamp(view.Y, canvas.Bottom - view.Height, canvas.Top)
            : canvas.Center.Y - view.Height / 2;
        if (x == view.X && y == view.Y) return;

        center.X = (fittedWorldBounds.Center.X - x) / (float)view.Width;
        center.Y = (fittedWorldBounds.Center.Y - y) / (float)view.Height;
        if (IsZooming)
        {
            // Once an edge stops the camera, continue smoothly from that edge
            // rather than pulling back toward the now-unreachable old anchor.
            zoomAnchorWorld = new Vector2(
                (zoomAnchorScreen.X - x) / (float)view.Width,
                (zoomAnchorScreen.Y - y) / (float)view.Height);
        }
    }
}
