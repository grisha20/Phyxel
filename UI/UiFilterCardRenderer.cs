using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Phyxel.Core;

namespace Phyxel.UI;

// Native pixel drawings scale with the existing material-card palette.
// Each brush has its own pictogram, without introducing raster dependencies.
internal static class UiFilterCardRenderer
{
    internal static string Title(FilterSelection brush) => brush switch
    {
        FilterSelection.SelectedMaterial => "Выбранный вид",
        FilterSelection.Wall => "Стенка",
        _ => FilterRules.Label(brush, "")
    };

    internal static string CardTitle(FilterSelection brush) => brush switch
    {
        FilterSelection.Steam => "Только\nпар",
        FilterSelection.Water => "Только\nвода",
        FilterSelection.Oil => "Только\nмасло",
        FilterSelection.Gases => "Все\nгазы",
        FilterSelection.Liquids => "Все\nжидкости",
        FilterSelection.Powders => "Все\nпорошки",
        FilterSelection.SelectedMaterial => "Выбранный\nвид",
        FilterSelection.AirOnly => "Только\nвоздух",
        FilterSelection.NoAir => "Без\nвоздуха",
        _ => "Стенка"
    };

    internal static string Description(FilterSelection brush) => brush switch
    {
        FilterSelection.Wall => "Блокирует частицы и фоновый воздух",
        FilterSelection.AirOnly => "Пропускает фоновый воздух; задерживает все частицы",
        FilterSelection.NoAir => "Пропускает все частицы; блокирует фоновый воздух",
        FilterSelection.Gases => "Пропускает газы, пламя и фоновый воздух",
        FilterSelection.SelectedMaterial => "Пропускает выбранное вещество из палитры",
        _ => "Пропускает указанное вещество или класс; блокирует фоновый воздух"
    };

    internal static void Draw(SpriteBatch batch, Texture2D pixel, Rectangle bounds, FilterSelection brush)
    {
        Color tint = brush switch
        {
            FilterSelection.Water => new(65, 150, 255),
            FilterSelection.Oil => new(225, 164, 65),
            FilterSelection.Gases => new(95, 210, 155),
            FilterSelection.Liquids => new(90, 175, 245),
            FilterSelection.Powders => new(230, 195, 90),
            FilterSelection.SelectedMaterial => new(195, 130, 235),
            FilterSelection.Wall => new(175, 185, 195),
            FilterSelection.AirOnly => new(170, 220, 245),
            FilterSelection.NoAir => new(235, 110, 155),
            _ => new(205, 235, 255)
        };
        batch.Draw(pixel, bounds, new Color(15, 24, 33));
        // Draw in a common 100x60 coordinate system; all primitives are clipped
        // by their geometry to the picture area, including compact palettes.
        float sx = bounds.Width / 100f, sy = bounds.Height / 60f;
        void Rect(float x, float y, float w, float h, Color color) => batch.Draw(pixel,
            new Rectangle(bounds.X + (int)(x * sx), bounds.Y + (int)(y * sy),
                Math.Max(1, (int)(w * sx)), Math.Max(1, (int)(h * sy))), color);
        void Dot(float x, float y, float radius, Color color)
        {
            for (int row = -(int)radius; row <= radius; row++)
            {
                float half = MathF.Sqrt(Math.Max(0, radius * radius - row * row));
                Rect(x - half, y + row, half * 2 + 1, 1, color);
            }
        }
        void Drop(float x, float y, Color color)
        {
            for (int row = 0; row < 17; row++)
            {
                float half = Math.Min(row * .6f, MathF.Sqrt(Math.Max(0, 49 - (row - 10) * (row - 10))));
                if (row < 7) half = row * .6f;
                Rect(x - half, y + row, half * 2 + 1, 1, color);
            }
            Rect(x - 2, y + 9, 2, 4, Color.White * .65f);
        }
        void Wind(float y, bool blocked)
        {
            Rect(16, y, blocked ? 27 : 62, 2, tint);
            float arrowX = blocked ? 38 : 72;
            for (int i = 0; i < 6; i++) Rect(arrowX + i, y - 5 + i, 2, 12 - 2 * i, tint);
        }
        Color grid = tint * .3f;
        for (int x = 6; x < 100; x += 10) Rect(x, 4, 1, 52, grid);
        for (int y = 6; y < 60; y += 10) Rect(4, y, 92, 1, grid);
        switch (brush)
        {
            case FilterSelection.Steam:
                Dot(42, 34, 9, tint); Dot(53, 31, 12, tint); Dot(66, 35, 8, tint);
                Rect(33, 34, 41, 10, tint);
                for (int i = 0; i < 3; i++) { Rect(39 + i * 12, 14 - i % 2 * 4, 3, 10, tint); }
                break;
            case FilterSelection.Water:
                Drop(50, 19, tint); Rect(29, 44, 42, 2, tint); Rect(36, 49, 28, 2, tint);
                break;
            case FilterSelection.Oil:
                Drop(50, 16, tint); Rect(32, 40, 36, 10, tint); Rect(37, 43, 26, 2, new Color(70, 43, 20));
                break;
            case FilterSelection.Gases:
                Dot(32, 25, 5, tint); Dot(55, 16, 6, tint); Dot(70, 36, 7, tint); Dot(43, 43, 4, tint);
                break;
            case FilterSelection.Liquids:
                for (int y = 20; y <= 44; y += 8)
                    for (int x = 20; x < 80; x += 2) Rect(x, y + (int)(MathF.Sin(x * .22f) * 3), 2, 2, tint);
                break;
            case FilterSelection.Powders:
                for (int row = 0; row < 4; row++)
                    for (int col = 0; col <= row; col++) Dot(50 - row * 7 + col * 14, 17 + row * 9, 3, tint);
                break;
            case FilterSelection.SelectedMaterial:
                Rect(35, 15, 30, 30, tint); Rect(40, 20, 20, 20, new Color(35, 22, 48));
                Rect(49, 10, 2, 40, Color.White); Rect(30, 29, 40, 2, Color.White);
                break;
            case FilterSelection.Wall:
                for (int row = 0; row < 4; row++)
                    for (int col = 0; col < 3; col++) Rect(17 + col * 22 + row % 2 * 3, 12 + row * 10, 20, 8, tint);
                break;
            case FilterSelection.AirOnly:
                Wind(19, false); Wind(31, false); Wind(43, false);
                break;
            case FilterSelection.NoAir:
                Wind(30, true); Rect(52, 14, 4, 32, tint);
                Dot(69, 22, 4, new Color(240, 210, 95)); Dot(80, 40, 4, new Color(240, 210, 95));
                break;
        }
    }
}
