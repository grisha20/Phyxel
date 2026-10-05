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
        bool isPaused,
        string transientStatus = "",
        string? filterBrush = null)
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

        string materialText = filterBrush is null ? $"Материал: {materialName}" : $"Кисть: {filterBrush}";
        spriteBatch.DrawString(font, materialText, new Vector2(x, textY), UiTheme.TextPrimary);
        x += (int)font.MeasureString(materialText).X + 18;

        string statusBlock = statusText;
        int statusWidth = (int)font.MeasureString(statusBlock).X + 34;
        int statusX = bounds.Right - statusWidth - 14;
        spriteBatch.Draw(pixel, new Rectangle(statusX - 12, bounds.Y + 7, 1, bounds.Height - 14), UiTheme.BorderColor);
        int dotSize = 8;
        spriteBatch.Draw(pixel, new Rectangle(statusX, bounds.Center.Y - dotSize / 2, dotSize, dotSize), statusColor);
        spriteBatch.DrawString(font, statusBlock, new Vector2(statusX + 14, textY), UiTheme.TextPrimary);

        if (!string.IsNullOrEmpty(transientStatus))
        {
            string message = FitStatus(font, transientStatus, Math.Max(0, statusX - x - 24));
            spriteBatch.DrawString(font, message, new Vector2(x, textY), UiTheme.TextPrimary);
            return;
        }
        x = DrawOptionalBlock(spriteBatch, font, pixel, bounds, x, statusX, tempProbeText, UiTheme.TextSecondary);
        x = DrawOptionalBlock(spriteBatch, font, pixel, bounds, x, statusX, $"Масштаб: {currentScale:0.00}x", UiTheme.TextSecondary);
        x = DrawOptionalBlock(spriteBatch, font, pixel, bounds, x, statusX, $"Частиц: {statistics.ActiveCells:N0}", UiTheme.TextSecondary);
        _ = DrawOptionalBlock(spriteBatch, font, pixel, bounds, x, statusX, $"{displayedFps:0} FPS", UiTheme.TextMuted);
    }

    internal static string FitStatus(SpriteFont font, string text, int maximumWidth)
    {
        // File paths may use characters outside the bundled font's alphabet.
        char[] safe = text.ToCharArray();
        for (int i = 0; i < safe.Length; i++)
            if (!font.Characters.Contains(safe[i])) safe[i] = '?';
        string value = new(safe);
        if (font.MeasureString(value).X <= maximumWidth) return value;
        const string suffix = "...";
        if (font.MeasureString(suffix).X > maximumWidth) return string.Empty;
        int low = 0, high = value.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (font.MeasureString(value[..mid] + suffix).X <= maximumWidth) low = mid;
            else high = mid - 1;
        }
        return value[..low] + suffix;
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

    internal static string FormatTemperatureProbe(MaterialRegistry registry, TemperatureProbeResult? probe)
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
        if (material.ThermalRegulator is not null)
        {
            float target = ((int)(value.Reserved & 0xffff) - 2732) / 10f;
            float power = (value.Reserved >> 16) / 10f;
            return string.Create(CultureInfo.GetCultureInfo("ru-RU"),
                $"{material.Name}: {tempStr} °C · цель {target:0.#} · P {power:0.#}");
        }
        if (material.Moisture is not null)
        {
            float fraction = BitConverter.UInt32BitsToSingle(value.Reserved);
            string liquidName=registry.TryGet((ushort)(value.RetainedLiquidMaterialIndex!=0?value.RetainedLiquidMaterialIndex:material.Properties.FuelLiquidMaterialIndex),out var retained)?retained.Name:"Жидкость";
            if (float.IsFinite(value.FuelFraction) && value.FuelFraction > 0 && value.FuelFraction <= 1)
                return string.Create(CultureInfo.GetCultureInfo("ru-RU"),
                    $"Под курсором: {material.Name} ({tempStr} °C · влага {fraction:P0} · {liquidName} {value.FuelFraction:P0})");
            if (float.IsFinite(fraction) && fraction > 0 && fraction <= 1)
                return string.Create(CultureInfo.GetCultureInfo("ru-RU"),
                    $"Под курсором: {material.Name} ({tempStr} °C · влага {fraction:P0})");
        }
        return $"Под курсором: {material.Name} ({tempStr} °C)";
    }
}
