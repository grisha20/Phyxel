using System;
using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.UI;

public sealed class UiStatusBar
{
    public void Draw(
        SpriteBatch spriteBatch,
        SpriteFont font,
        UiPanelBackdropRenderer backdrop,
        Texture2D pixel,
        Rectangle bounds,
        MaterialRegistry registry,
        ushort selectedMaterialIndex,
        bool isTemperatureTool,
        TemperatureProbeResult? probe,
        SimulationStatistics statistics,
        double displayedFps,
        float currentScale,
        bool isPaused)
    {
        backdrop.Draw(spriteBatch, bounds, 0);

        string materialName = isTemperatureTool
            ? "Температура"
            : (registry.TryGet(selectedMaterialIndex, out MaterialDefinition def) ? def.Name : "Неизвестно");

        string tempProbeText = FormatTemperatureProbe(registry, probe);
        string statusText = isPaused ? "Пауза" : "Симуляция работает";
        Color statusColor = isPaused ? UiTheme.PrimaryAccent : UiTheme.StatusGreen;
        int textY = bounds.Y + (bounds.Height - font.LineSpacing) / 2;
        int x = bounds.X + 14;

        int swatchSize = Math.Clamp(bounds.Height - 14, 14, 24);
        if (!isTemperatureTool && registry.TryGet(selectedMaterialIndex, out MaterialDefinition selected))
        {
            Rectangle swatch = new(x, bounds.Center.Y - swatchSize / 2, swatchSize, swatchSize);
            spriteBatch.Draw(pixel, swatch, selected.Color);
            UiIconRenderer.DrawStrokedRectangle(spriteBatch, pixel, swatch, 1, UiTheme.BorderHighlight);
            x = swatch.Right + 8;
        }

        string materialText = $"Материал: {materialName}";
        spriteBatch.DrawString(font, materialText, new Vector2(x, textY), UiTheme.TextPrimary);
        x += (int)font.MeasureString(materialText).X + 18;

        string statusBlock = statusText;
        int statusWidth = (int)font.MeasureString(statusBlock).X + 34;
        int statusX = bounds.Right - statusWidth - 14;
        spriteBatch.Draw(pixel, new Rectangle(statusX - 12, bounds.Y + 7, 1, bounds.Height - 14), UiTheme.BorderColor);
        int dotSize = 8;
        spriteBatch.Draw(pixel, new Rectangle(statusX, bounds.Center.Y - dotSize / 2, dotSize, dotSize), statusColor);
        spriteBatch.DrawString(font, statusBlock, new Vector2(statusX + 14, textY), UiTheme.TextPrimary);

        x = DrawOptionalBlock(spriteBatch, font, pixel, bounds, x, statusX, tempProbeText, UiTheme.TextSecondary);
        x = DrawOptionalBlock(spriteBatch, font, pixel, bounds, x, statusX, $"Масштаб: {currentScale:0.00}x", UiTheme.TextSecondary);
        x = DrawOptionalBlock(spriteBatch, font, pixel, bounds, x, statusX, $"Частиц: {statistics.ActiveCells:N0}", UiTheme.TextSecondary);
        _ = DrawOptionalBlock(spriteBatch, font, pixel, bounds, x, statusX, $"{displayedFps:0} FPS", UiTheme.TextMuted);
    }

    private static int DrawOptionalBlock(
        SpriteBatch spriteBatch,
        SpriteFont font,
        Texture2D pixel,
        Rectangle bounds,
        int x,
        int rightLimit,
        string text,
        Color color)
    {
        int width = (int)font.MeasureString(text).X;
        if (x + width + 22 >= rightLimit)
        {
            return x;
        }

        spriteBatch.Draw(pixel, new Rectangle(x, bounds.Y + 7, 1, bounds.Height - 14), UiTheme.BorderColor);
        x += 14;
        spriteBatch.DrawString(font, text, new Vector2(x, bounds.Y + (bounds.Height - font.LineSpacing) / 2f), color);
        return x + width + 16;
    }

    private static string FormatTemperatureProbe(MaterialRegistry registry, TemperatureProbeResult? probe)
    {
        if (probe is null || probe.Value.IsActive == 0)
        {
            return "Температура под курсором: —";
        }

        TemperatureProbeResult value = probe.Value;
        if (value.MaterialIndex > ushort.MaxValue ||
            !registry.TryGet((ushort)value.MaterialIndex, out MaterialDefinition material) ||
            !float.IsFinite(value.Temperature))
        {
            return "Температура под курсором: —";
        }

        string tempStr = value.Temperature.ToString("0.0", CultureInfo.GetCultureInfo("ru-RU"));
        return $"Под курсором: {material.Name} ({tempStr} °C)";
    }
}
