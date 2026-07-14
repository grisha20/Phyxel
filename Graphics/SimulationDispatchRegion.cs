using System;
using Phyxel.Physics;

namespace Phyxel.Graphics;

internal sealed class SimulationDispatchRegion
{
    private bool active;
    private int minimumX;
    private int minimumY;
    private int maximumX;
    private int maximumY;

    public void Reset()
    {
        active = false;
    }

    public void SetFull(int width, int height)
    {
        minimumX = 0;
        minimumY = 0;
        maximumX = width;
        maximumY = height;
        active = true;
    }

    public void Include(BrushDrawCommand command, int padding)
    {
        int radius = (int)MathF.Ceiling(command.Radius) + padding;
        int left = command.X - radius;
        int top = command.Y - radius;
        int right = command.X + radius + 1;
        int bottom = command.Y + radius + 1;
        if (!active)
        {
            minimumX = left;
            minimumY = top;
            maximumX = right;
            maximumY = bottom;
            active = true;
            return;
        }

        minimumX = Math.Min(minimumX, left);
        minimumY = Math.Min(minimumY, top);
        maximumX = Math.Max(maximumX, right);
        maximumY = Math.Max(maximumY, bottom);
    }

    public void Include(SimulationDispatchRegion other)
    {
        if (!other.active)
        {
            return;
        }
        if (!active)
        {
            minimumX = other.minimumX;
            minimumY = other.minimumY;
            maximumX = other.maximumX;
            maximumY = other.maximumY;
            active = true;
            return;
        }

        minimumX = Math.Min(minimumX, other.minimumX);
        minimumY = Math.Min(minimumY, other.minimumY);
        maximumX = Math.Max(maximumX, other.maximumX);
        maximumY = Math.Max(maximumY, other.maximumY);
    }

    public void Grow(int width, int height, int amount)
    {
        Grow(width, height, amount, amount);
    }

    public void Grow(int width, int height, int horizontalAmount, int verticalAmount)
    {
        Grow(width, height, horizontalAmount, verticalAmount, verticalAmount);
    }

    public void Grow(int width, int height, int horizontalAmount, int upwardAmount, int downwardAmount)
    {
        if (!active)
        {
            SetFull(width, height);
            return;
        }

        minimumX = Math.Max(0, minimumX - horizontalAmount);
        minimumY = Math.Max(0, minimumY - upwardAmount);
        maximumX = Math.Min(width, maximumX + horizontalAmount);
        maximumY = Math.Min(height, maximumY + downwardAmount);
    }

    public void Get(int width, int height, out int x, out int y, out int regionWidth, out int regionHeight)
    {
        if (!active)
        {
            x = 0;
            y = 0;
            regionWidth = width;
            regionHeight = height;
            return;
        }

        x = Math.Clamp(minimumX, 0, width - 1);
        y = Math.Clamp(minimumY, 0, height - 1);
        int right = Math.Clamp(maximumX, x + 1, width);
        int bottom = Math.Clamp(maximumY, y + 1, height);
        regionWidth = right - x;
        regionHeight = bottom - y;
    }
}
