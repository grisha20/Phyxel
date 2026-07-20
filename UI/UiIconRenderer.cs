using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Phyxel.UI;

public static class UiIconRenderer
{
    public static void DrawPhyxelLogo(
        SpriteBatch spriteBatch,
        Texture2D pixel,
        Rectangle bounds,
        Color color)
    {
        int size = Math.Min(bounds.Width, bounds.Height);
        int centerX = bounds.X + bounds.Width / 2;
        int centerY = bounds.Y + bounds.Height / 2;
        int radius = size / 3;

        // Diamond / Hexagon geometric Phyxel mark
        DrawThickLine(spriteBatch, pixel, new Vector2(centerX, centerY - radius), new Vector2(centerX + radius, centerY), 2, color);
        DrawThickLine(spriteBatch, pixel, new Vector2(centerX + radius, centerY), new Vector2(centerX, centerY + radius), 2, color);
        DrawThickLine(spriteBatch, pixel, new Vector2(centerX, centerY + radius), new Vector2(centerX - radius, centerY), 2, color);
        DrawThickLine(spriteBatch, pixel, new Vector2(centerX - radius, centerY), new Vector2(centerX, centerY - radius), 2, color);
        
        // Inner pixel dot
        spriteBatch.Draw(pixel, new Rectangle(centerX - 2, centerY - 2, 4, 4), color);
    }

    public static void DrawToolIcon(
        SpriteBatch spriteBatch,
        Texture2D pixel,
        string toolId,
        Rectangle bounds,
        Color color)
    {
        int cx = bounds.X + bounds.Width / 2;
        int cy = bounds.Y + bounds.Height / 2;
        int s = Math.Min(bounds.Width, bounds.Height) / 2;

        switch (toolId.ToLowerInvariant())
        {
            case "brush": // Кисть
                spriteBatch.Draw(pixel, new Rectangle(cx - 3, cy - 3, 6, 6), color);
                DrawThickLine(spriteBatch, pixel, new Vector2(cx - 5, cy + 5), new Vector2(cx + 4, cy - 4), 2, color);
                break;

            case "eraser": // Ластик
                Rectangle eraserBox = new(cx - s / 2, cy - s / 3, s, s * 2 / 3);
                DrawStrokedRectangle(spriteBatch, pixel, eraserBox, 2, color);
                DrawThickLine(spriteBatch, pixel, new Vector2(cx, eraserBox.Y), new Vector2(cx - s / 3, eraserBox.Bottom), 1, color);
                break;

            case "temperature": // Температура
                spriteBatch.Draw(pixel, new Rectangle(cx - 2, cy - s / 2, 4, s), color);
                spriteBatch.Draw(pixel, new Rectangle(cx - 4, cy + s / 4, 8, 8), color);
                break;

            case "pan": // Панорама / Камера
                DrawThickLine(spriteBatch, pixel, new Vector2(cx - s, cy), new Vector2(cx + s, cy), 2, color);
                DrawThickLine(spriteBatch, pixel, new Vector2(cx, cy - s), new Vector2(cx, cy + s), 2, color);
                break;

            case "line": // Линия
                DrawThickLine(spriteBatch, pixel, new Vector2(cx - s + 2, cy + s - 2), new Vector2(cx + s - 2, cy - s + 2), 2, color);
                break;

            case "rectangle": // Прямоугольник
                DrawStrokedRectangle(spriteBatch, pixel, new Rectangle(cx - s + 2, cy - s + 2, (s - 2) * 2, (s - 2) * 2), 2, color);
                break;

            case "circle": // Круг
                DrawStrokedRectangle(spriteBatch, pixel, new Rectangle(cx - s + 2, cy - s + 2, (s - 2) * 2, (s - 2) * 2), 2, color);
                break;

            case "fill": // Заливка
                DrawStrokedRectangle(spriteBatch, pixel, new Rectangle(cx - s / 2, cy - s / 2, s, s), 2, color);
                spriteBatch.Draw(pixel, new Rectangle(cx + s / 4, cy + s / 4, 4, 4), color);
                break;

            case "eyedropper": // Пипетка
                DrawThickLine(spriteBatch, pixel, new Vector2(cx - s / 2, cy + s / 2), new Vector2(cx + s / 2, cy - s / 2), 2, color);
                spriteBatch.Draw(pixel, new Rectangle(cx - s / 2 - 1, cy + s / 2 - 1, 3, 3), color);
                break;

            default:
                spriteBatch.Draw(pixel, new Rectangle(cx - 3, cy - 3, 6, 6), color);
                break;
        }
    }

    public static void DrawStrokedRectangle(
        SpriteBatch spriteBatch,
        Texture2D pixel,
        Rectangle bounds,
        int thickness,
        Color color)
    {
        spriteBatch.Draw(pixel, new Rectangle(bounds.X, bounds.Y, bounds.Width, thickness), color);
        spriteBatch.Draw(pixel, new Rectangle(bounds.X, bounds.Bottom - thickness, bounds.Width, thickness), color);
        spriteBatch.Draw(pixel, new Rectangle(bounds.X, bounds.Y, thickness, bounds.Height), color);
        spriteBatch.Draw(pixel, new Rectangle(bounds.Right - thickness, bounds.Y, thickness, bounds.Height), color);
    }

    public static void DrawThickLine(
        SpriteBatch spriteBatch,
        Texture2D pixel,
        Vector2 start,
        Vector2 end,
        int thickness,
        Color color)
    {
        Vector2 edge = end - start;
        float angle = (float)Math.Atan2(edge.Y, edge.X);
        spriteBatch.Draw(
            pixel,
            new Rectangle((int)start.X, (int)start.Y, (int)edge.Length(), thickness),
            null,
            color,
            angle,
            Vector2.Zero,
            SpriteEffects.None,
            0);
    }
}
