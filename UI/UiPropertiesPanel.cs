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

    private readonly UiToggleSwitch solidGravityToggle = new("Гравитация твёрдых тел");
    private readonly UiToggleSwitch hydraulicsToggle = new("Гидравлика сосудов");
    private readonly UiIconButton resetButton = new("Сброс");
    private readonly UiIconButton clearButton = new("Очистить");

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
        resetButton.IconKey = "reset";
        clearButton.IconKey = "clear";
        clearButton.Danger = true;
    }

    public void Update(
        RawInputSnapshot input,
        Rectangle bounds,
        SpriteFont font,
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
        ScaleChanged = false;
        clearConfirmTimer = Math.Max(0f, clearConfirmTimer - input.DeltaSeconds);

        int padding = 14;
        int innerX = bounds.X + padding;
        int innerWidth = bounds.Width - padding * 2;
        int infoHeight = font.LineSpacing * 2 + 26;
        int cursorY = bounds.Y + font.LineSpacing + infoHeight + 34;
        int sliderHeight = font.LineSpacing + 30;
        int sliderGap = 12;

        brushSlider.Bounds = new Rectangle(innerX, cursorY, innerWidth, sliderHeight);
        cursorY += sliderHeight + sliderGap;

        if (activeTool == PhyxelToolId.Temperature)
        {
            densitySlider.CancelDrag();
            temperatureSlider.Bounds = new Rectangle(innerX, cursorY, innerWidth, sliderHeight);
            temperatureSlider.Update(input);
            cursorY += sliderHeight + sliderGap;
        }
        else if (activeTool == PhyxelToolId.Brush)
        {
            temperatureSlider.CancelDrag();
            densitySlider.Bounds = new Rectangle(innerX, cursorY, innerWidth, sliderHeight);
            if (densitySlider.Update(input))
            {
                settings.SpawnDensity = densitySlider.Value / 100f;
            }
            cursorY += sliderHeight + sliderGap;
        }
        else
        {
            densitySlider.CancelDrag();
            temperatureSlider.CancelDrag();
        }

        scaleSlider.Bounds = new Rectangle(innerX, cursorY, innerWidth, sliderHeight);
        if (scaleSlider.Update(input))
        {
            ScaleChanged = true;
            settings.ApplyScale(scaleSlider.Value / 100f);
        }
        cursorY += sliderHeight + 16;

        if (brushSlider.Update(input))
        {
            settings.BrushRadius = (int)brushSlider.Value;
        }

        brushSlider.Value = settings.BrushRadius;
        densitySlider.Value = settings.SpawnDensity * 100f;
        scaleSlider.Value = settings.Scale * 100f;

        // Toggle Buttons
        int toggleHeight = Math.Clamp(font.LineSpacing + 18, 40, 52);
        solidGravityToggle.Bounds = new Rectangle(innerX, cursorY, innerWidth, toggleHeight);
        if (solidGravityToggle.Update(input))
        {
            settings.SolidGravity = !settings.SolidGravity;
            GravityToggled = true;
        }
        cursorY += toggleHeight + 4;

        hydraulicsToggle.Bounds = new Rectangle(innerX, cursorY, innerWidth, toggleHeight);
        if (hydraulicsToggle.Update(input))
        {
            settings.HydraulicPressure = !settings.HydraulicPressure;
            HydraulicsToggled = true;
        }

        // Bottom Action Buttons
        int buttonHeight = Math.Clamp(font.LineSpacing + 16, 38, 50);
        int bottomY = bounds.Bottom - buttonHeight - padding;
        int actionGap = 7;
        int actionWidth = (innerWidth - actionGap) / 2;
        resetButton.Bounds = new Rectangle(innerX, bottomY, actionWidth, buttonHeight);
        if (resetButton.Update(input))
        {
            ResetRequested = true;
        }

        clearButton.Bounds = new Rectangle(resetButton.Bounds.Right + actionGap, bottomY, innerWidth - actionWidth - actionGap, buttonHeight);
        clearButton.Label = clearConfirmTimer > 0f ? "Подтвердить" : "Очистить";
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
        SimulationSettings settings,
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

        int padding = 14;
        int infoHeight = font.LineSpacing * 2 + 26;
        Rectangle infoBox = new(bounds.X + padding, bounds.Y + font.LineSpacing + 20, bounds.Width - padding * 2, infoHeight);
        backdrop.DrawRoundedRectangle(spriteBatch, infoBox, UiTheme.CardBackground, 6);
        UiIconRenderer.DrawStrokedRectangle(spriteBatch, pixel, infoBox, 1, UiTheme.BorderColor);

        string toolName = activeTool switch
        {
            PhyxelToolId.Brush => "Кисть",
            PhyxelToolId.Eraser => "Ластик",
            PhyxelToolId.Temperature => "Температура",
            PhyxelToolId.Pan => "Панорама",
            _ => "Инструмент"
        };

        Rectangle toolIcon = new(infoBox.X + 10, infoBox.Y + 9, font.LineSpacing, font.LineSpacing);
        string toolKey = activeTool switch
        {
            PhyxelToolId.Brush => "brush",
            PhyxelToolId.Eraser => "eraser",
            PhyxelToolId.Temperature => "temperature",
            _ => "pan"
        };
        UiIconRenderer.DrawToolIcon(spriteBatch, pixel, toolKey, toolIcon, UiTheme.PrimaryAccent);
        spriteBatch.DrawString(font, toolName, new Vector2(toolIcon.Right + 9, infoBox.Y + 7), UiTheme.TextPrimary);

        // Material color preview swatch
        int swatchSize = Math.Clamp(font.LineSpacing - 3, 14, 22);
        Rectangle swatch = new(infoBox.X + 10, infoBox.Bottom - swatchSize - 9, swatchSize, swatchSize);
        spriteBatch.Draw(pixel, swatch, selectedMaterial.Color);
        UiIconRenderer.DrawStrokedRectangle(spriteBatch, pixel, swatch, 1, UiTheme.BorderColor);

        spriteBatch.DrawString(
            font,
            TruncateToWidth(font, $"Материал: {selectedMaterial.Name}", infoBox.Right - swatch.Right - 18),
            new Vector2(swatch.Right + 8, swatch.Center.Y - font.LineSpacing / 2f),
            UiTheme.TextSecondary);

        // Draw Sliders
        brushSlider.Draw(spriteBatch, font, backdrop, pixel);
        if (activeTool == PhyxelToolId.Temperature)
        {
            temperatureSlider.Draw(spriteBatch, font, backdrop, pixel);
        }
        else if (activeTool == PhyxelToolId.Brush)
        {
            densitySlider.Draw(spriteBatch, font, backdrop, pixel);
        }
        scaleSlider.Draw(spriteBatch, font, backdrop, pixel);

        // Draw Toggles & Actions
        spriteBatch.Draw(pixel, new Rectangle(bounds.X + padding, solidGravityToggle.Bounds.Y - 9, bounds.Width - padding * 2, 1), UiTheme.BorderColor);
        solidGravityToggle.Draw(spriteBatch, font, backdrop, pixel, settings.SolidGravity);
        hydraulicsToggle.Draw(spriteBatch, font, backdrop, pixel, settings.HydraulicPressure);
        resetButton.Draw(spriteBatch, font, backdrop, pixel);
        clearButton.Draw(spriteBatch, font, backdrop, pixel);
    }

    private static string TruncateToWidth(SpriteFont font, string text, int maximumWidth)
    {
        if (font.MeasureString(text).X <= maximumWidth)
        {
            return text;
        }
        const string ellipsis = "…";
        int length = text.Length;
        while (length > 0 && font.MeasureString(text[..length] + ellipsis).X > maximumWidth)
        {
            length--;
        }
        return length == 0 ? ellipsis : text[..length] + ellipsis;
    }
}
