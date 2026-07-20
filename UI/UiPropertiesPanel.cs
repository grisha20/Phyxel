using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Phyxel.Core;
using Phyxel.Input;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.UI;

public sealed class UiPropertiesPanel
{
    private readonly UiValueSlider brushSlider;
    private readonly UiValueSlider densitySlider;
    private readonly UiValueSlider temperatureSlider;
    private readonly UiValueSlider scaleSlider;

    private readonly UiIconButton solidGravityButton = new("Гравитация");
    private readonly UiIconButton hydraulicsButton = new("Сосуды");
    private readonly UiIconButton resetButton = new("Сброс симуляции");
    private readonly UiIconButton clearButton = new("Очистить всё");

    private float clearConfirmTimer;

    public bool GravityToggled { get; private set; }
    public bool HydraulicsToggled { get; private set; }
    public bool ResetRequested { get; private set; }
    public bool ClearRequested { get; private set; }
    public bool ScaleChanged { get; private set; }

    public UiPropertiesPanel()
    {
        brushSlider = new UiValueSlider("Размер", 1, 96, 1, 18, " px");
        densitySlider = new UiValueSlider("Плотность", 5, 100, 1, 82, "%");
        temperatureSlider = new UiValueSlider("Температура", -200, 2000, 10, 500, " °C", "0");
        scaleSlider = new UiValueSlider(
            "Масштаб",
            [25, 35, 50, 75, 85, 100],
            SimulationSettings.DefaultScale * 100,
            "%",
            "0");
    }

    public void Update(
        RawInputSnapshot input,
        Rectangle bounds,
        SimulationSettings settings,
        PhyxelToolId activeTool,
        MaterialDefinition selectedMaterial,
        out bool pointerConsumed)
    {
        pointerConsumed = bounds.Contains(input.MousePosition) ||
            brushSlider.IsDragging || densitySlider.IsDragging ||
            temperatureSlider.IsDragging || scaleSlider.IsDragging;

        GravityToggled = false;
        HydraulicsToggled = false;
        ResetRequested = false;
        ClearRequested = false;
        clearConfirmTimer = Math.Max(0f, clearConfirmTimer - input.DeltaSeconds);

        int padding = 12;
        int innerX = bounds.X + padding;
        int innerWidth = bounds.Width - padding * 2;
        // Info box occupies Y+34..Y+86; slider labels draw 24px above Bounds.Y
        int cursorY = bounds.Y + 120;

        // Sliders Layout
        brushSlider.Bounds = new Rectangle(innerX, cursorY, innerWidth, 22);
        cursorY += 52;

        if (activeTool == PhyxelToolId.Temperature)
        {
            temperatureSlider.Bounds = new Rectangle(innerX, cursorY, innerWidth, 22);
            temperatureSlider.Update(input);
        }
        else
        {
            densitySlider.Bounds = new Rectangle(innerX, cursorY, innerWidth, 22);
            if (densitySlider.Update(input))
            {
                settings.SpawnDensity = densitySlider.Value / 100f;
            }
        }
        cursorY += 52;

        scaleSlider.Bounds = new Rectangle(innerX, cursorY, innerWidth, 22);
        if (scaleSlider.Update(input))
        {
            ScaleChanged = true;
            settings.ApplyScale(scaleSlider.Value / 100f);
        }
        cursorY += 52;

        if (brushSlider.Update(input))
        {
            settings.BrushRadius = (int)brushSlider.Value;
        }

        brushSlider.Value = settings.BrushRadius;
        densitySlider.Value = settings.SpawnDensity * 100f;
        scaleSlider.Value = settings.Scale * 100f;

        // Toggle Buttons
        int buttonHeight = 32;
        solidGravityButton.Bounds = new Rectangle(innerX, cursorY, innerWidth, buttonHeight);
        solidGravityButton.Active = settings.SolidGravity;
        solidGravityButton.Label = settings.SolidGravity ? "Гравитация: Вкл" : "Гравитация: Выкл";
        if (solidGravityButton.Update(input))
        {
            settings.SolidGravity = !settings.SolidGravity;
            GravityToggled = true;
        }
        cursorY += buttonHeight + 6;

        hydraulicsButton.Bounds = new Rectangle(innerX, cursorY, innerWidth, buttonHeight);
        hydraulicsButton.Active = settings.HydraulicPressure;
        hydraulicsButton.Label = settings.HydraulicPressure ? "Гидравлика: Вкл" : "Гидравлика: Выкл";
        if (hydraulicsButton.Update(input))
        {
            settings.HydraulicPressure = !settings.HydraulicPressure;
            HydraulicsToggled = true;
        }
        cursorY += buttonHeight + 12;

        // Bottom Action Buttons
        int bottomY = bounds.Bottom - buttonHeight * 2 - padding - 6;
        resetButton.Bounds = new Rectangle(innerX, bottomY, innerWidth, buttonHeight);
        if (resetButton.Update(input))
        {
            ResetRequested = true;
        }

        clearButton.Bounds = new Rectangle(innerX, bottomY + buttonHeight + 6, innerWidth, buttonHeight);
        clearButton.Label = clearConfirmTimer > 0f ? "Подтвердить" : "Очистить всё";
        if (clearButton.Update(input))
        {
            if (clearConfirmTimer > 0f)
            {
                ClearRequested = true;
                clearConfirmTimer = 0f;
            }
            else
            {
                clearConfirmTimer = 3f;
            }
        }
    }

    public float TargetTemperature => temperatureSlider.Value;

    public void Draw(
        SpriteBatch spriteBatch,
        SpriteFont font,
        UiPanelBackdropRenderer backdrop,
        Texture2D pixel,
        Rectangle bounds,
        PhyxelToolId activeTool,
        MaterialDefinition selectedMaterial)
    {
        backdrop.Draw(spriteBatch, bounds, 8);

        // Header
        spriteBatch.DrawString(
            font,
            "СВОЙСТВА",
            new Vector2(bounds.X + 12, bounds.Y + 8),
            UiTheme.TextMuted);

        // Current Tool & Material Header Box (height 52px, Y+34)
        Rectangle infoBox = new(bounds.X + 12, bounds.Y + 34, bounds.Width - 24, 52);
        backdrop.Draw(spriteBatch, infoBox, 4);
        UiIconRenderer.DrawStrokedRectangle(spriteBatch, pixel, infoBox, 1, UiTheme.BorderColor);

        string toolName = activeTool switch
        {
            PhyxelToolId.Brush => "Кисть",
            PhyxelToolId.Eraser => "Ластик",
            PhyxelToolId.Temperature => "Температура",
            PhyxelToolId.Pan => "Панорама",
            _ => "Инструмент"
        };

        spriteBatch.DrawString(
            font,
            $"Инструмент: {toolName}",
            new Vector2(infoBox.X + 8, infoBox.Y + 5),
            UiTheme.TextPrimary);

        // Material color preview swatch
        Rectangle swatch = new(infoBox.X + 8, infoBox.Y + 29, 14, 14);
        spriteBatch.Draw(pixel, swatch, selectedMaterial.Color);
        UiIconRenderer.DrawStrokedRectangle(spriteBatch, pixel, swatch, 1, UiTheme.BorderColor);

        spriteBatch.DrawString(
            font,
            $"Материал: {selectedMaterial.Name}",
            new Vector2(swatch.Right + 6, infoBox.Y + 27),
            UiTheme.TextSecondary);

        // Draw Sliders
        brushSlider.Draw(spriteBatch, font, backdrop, pixel);
        if (activeTool == PhyxelToolId.Temperature)
        {
            temperatureSlider.Draw(spriteBatch, font, backdrop, pixel);
        }
        else
        {
            densitySlider.Draw(spriteBatch, font, backdrop, pixel);
        }
        scaleSlider.Draw(spriteBatch, font, backdrop, pixel);

        // Draw Toggles & Actions
        solidGravityButton.Draw(spriteBatch, font, backdrop, pixel);
        hydraulicsButton.Draw(spriteBatch, font, backdrop, pixel);
        resetButton.Draw(spriteBatch, font, backdrop, pixel);
        clearButton.Draw(spriteBatch, font, backdrop, pixel);
    }
}
