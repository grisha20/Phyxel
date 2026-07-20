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

        string leftInfo = $"Материал: {materialName}   |   {tempProbeText}";
        string rightInfo = $"Активно: {statistics.ActiveCells:N0}   |   Масштаб: {currentScale:0.00}x   |   Статус: {statusText}";

        int textY = bounds.Y + (bounds.Height - font.LineSpacing) / 2;
        spriteBatch.DrawString(font, leftInfo, new Vector2(bounds.X + 12, textY), UiTheme.TextSecondary);

        Vector2 rightSize = font.MeasureString(rightInfo);
        Vector2 statusPos = new(bounds.Right - rightSize.X - 12, textY);
        spriteBatch.DrawString(font, rightInfo, statusPos, statusColor);
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
