using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Phyxel.UI;

public static class UiIconRenderer
{
    public static void DrawIcon(
        SpriteBatch spriteBatch,
        Texture2D texture,
        Rectangle bounds,
        Color tint)
    {
        float scale = Math.Min(bounds.Width / (float)Math.Max(1, texture.Width), bounds.Height / (float)Math.Max(1, texture.Height));
        int width = Math.Max(1, (int)MathF.Round(texture.Width * scale));
        int height = Math.Max(1, (int)MathF.Round(texture.Height * scale));
        Rectangle destination = new(
            bounds.Center.X - width / 2,
            bounds.Center.Y - height / 2,
            width,
            height);
        spriteBatch.Draw(texture, destination, tint);
    }

    public static void DrawActionIcon(
        SpriteBatch spriteBatch,
        Texture2D pixel,
        string iconKey,
        Rectangle bounds,
        Color color)
    {
        int cx = bounds.Center.X;
        int cy = bounds.Center.Y;
        int inset = Math.Max(2, bounds.Width / 5);
        switch (iconKey)
        {
            case "save":
                DrawStrokedRectangle(spriteBatch, pixel, bounds, 2, color);
                spriteBatch.Draw(pixel, new Rectangle(bounds.X + inset, bounds.Y + 2, bounds.Width - inset * 2, bounds.Height / 3), color);
                spriteBatch.Draw(pixel, new Rectangle(bounds.X + inset, cy + 2, bounds.Width - inset * 2, bounds.Bottom - cy - 4), color * 0.7f);
                break;
            case "load":
                DrawStrokedRectangle(spriteBatch, pixel, new Rectangle(bounds.X, cy, bounds.Width, bounds.Height / 2), 2, color);
                DrawThickLine(spriteBatch, pixel, new Vector2(cx, bounds.Y), new Vector2(cx, cy + 2), 2, color);
                DrawThickLine(spriteBatch, pixel, new Vector2(cx, bounds.Y), new Vector2(cx - 4, bounds.Y + 5), 2, color);
                DrawThickLine(spriteBatch, pixel, new Vector2(cx, bounds.Y), new Vector2(cx + 4, bounds.Y + 5), 2, color);
                break;
            case "pause":
                spriteBatch.Draw(pixel, new Rectangle(cx - 5, bounds.Y + 2, 3, bounds.Height - 4), color);
                spriteBatch.Draw(pixel, new Rectangle(cx + 2, bounds.Y + 2, 3, bounds.Height - 4), color);
                break;
            case "play":
                for (int row = 0; row < bounds.Height - 4; row++)
                {
                    int half = Math.Min(row, bounds.Height - 5 - row) / 2;
                    spriteBatch.Draw(pixel, new Rectangle(bounds.X + inset, bounds.Y + 2 + row, Math.Max(1, half + 2), 1), color);
                }
                break;
            case "settings":
                DrawStrokedRectangle(spriteBatch, pixel, new Rectangle(cx - 5, cy - 5, 10, 10), 2, color);
                spriteBatch.Draw(pixel, new Rectangle(cx - 2, cy - 2, 4, 4), color);
                break;
            case "reset":
                DrawThickLine(spriteBatch, pixel, new Vector2(bounds.X + 3, cy), new Vector2(cx, bounds.Y + 3), 2, color);
                DrawThickLine(spriteBatch, pixel, new Vector2(cx, bounds.Y + 3), new Vector2(bounds.Right - 3, cy), 2, color);
                DrawThickLine(spriteBatch, pixel, new Vector2(bounds.Right - 3, cy), new Vector2(cx, bounds.Bottom - 3), 2, color);
                break;
            case "clear":
                DrawStrokedRectangle(spriteBatch, pixel, new Rectangle(bounds.X + 4, bounds.Y + 6, bounds.Width - 8, bounds.Height - 8), 2, color);
                spriteBatch.Draw(pixel, new Rectangle(bounds.X + 2, bounds.Y + 3, bounds.Width - 4, 2), color);
                break;
            default:
                spriteBatch.Draw(pixel, new Rectangle(cx - 2, cy - 2, 4, 4), color);
                break;
        }
    }

    public static void DrawCategoryIcon(
        SpriteBatch spriteBatch,
        Texture2D pixel,
        MaterialCategoryType type,
        Rectangle bounds,
        Color color)
    {
        int cx = bounds.Center.X;
        int cy = bounds.Center.Y;
        switch (type)
        {
            case MaterialCategoryType.Powders:
                spriteBatch.Draw(pixel, new Rectangle(bounds.X + 2, cy + 2, 4, 4), color);
                spriteBatch.Draw(pixel, new Rectangle(cx - 1, bounds.Y + 2, 4, 4), color);
                spriteBatch.Draw(pixel, new Rectangle(bounds.Right - 6, cy + 1, 4, 4), color);
                break;
            case MaterialCategoryType.Liquids:
                DrawThickLine(spriteBatch, pixel, new Vector2(bounds.X + 1, cy + 2), new Vector2(cx - 2, cy - 1), 2, color);
                DrawThickLine(spriteBatch, pixel, new Vector2(cx - 2, cy - 1), new Vector2(bounds.Right - 2, cy + 2), 2, color);
                break;
            case MaterialCategoryType.Gases:
                DrawStrokedRectangle(spriteBatch, pixel, new Rectangle(bounds.X + 1, bounds.Y + 2, 6, 6), 1, color);
                DrawStrokedRectangle(spriteBatch, pixel, new Rectangle(cx, cy - 1, 7, 7), 1, color);
                break;
            case MaterialCategoryType.Solids:
                DrawStrokedRectangle(spriteBatch, pixel, new Rectangle(bounds.X + 2, bounds.Y + 2, bounds.Width - 4, bounds.Height - 4), 2, color);
                break;
            case MaterialCategoryType.Combustion:
                DrawThickLine(spriteBatch, pixel, new Vector2(cx, bounds.Y + 1), new Vector2(bounds.X + 3, bounds.Bottom - 3), 2, color);
                DrawThickLine(spriteBatch, pixel, new Vector2(bounds.X + 3, bounds.Bottom - 3), new Vector2(bounds.Right - 3, bounds.Bottom - 3), 2, color);
                DrawThickLine(spriteBatch, pixel, new Vector2(bounds.Right - 3, bounds.Bottom - 3), new Vector2(cx, bounds.Y + 1), 2, color);
                break;
            default:
                DrawThickLine(spriteBatch, pixel, new Vector2(bounds.X + 2, bounds.Bottom - 2), new Vector2(bounds.Right - 2, bounds.Y + 2), 2, color);
                break;
        }
    }

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
