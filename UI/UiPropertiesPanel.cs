using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Phyxel.Core;
using Phyxel.Input;
using Phyxel.Materials;

namespace Phyxel.UI;

public sealed class UiPropertiesPanel
{
    private readonly UiValueSlider brushSlider = new("Размер кисти", 1, 96, 1, 18, " px");
    private readonly UiValueSlider densitySlider = new("Плотность спавна", 5, 100, 1, 82, "%");
    private readonly UiValueSlider temperatureSlider = new("Целевая температура", -200, 2000, 10, 500, " °C", "0");
    private readonly UiValueSlider deviceTemperatureSlider = new("Температура", -200, 2000, 10, 240, " °C", "0");
    private readonly UiValueSlider devicePowerSlider = new("Мощность", 0, 3600, 10, 600, " ед./с", "0.#");
    private readonly System.Collections.Generic.Dictionary<string, (float Temperature, float Power)> devicePreferences = new();
    private string? selectedDeviceId;
    private int deviceHintY;
    private readonly UiIconButton filterSelector = new("Только пар");
    private int filterHintY;
    internal Rectangle FilterSelectorBounds => filterSelector.Bounds;
    internal string FilterSelectorLabel => filterSelector.Label;
    internal Rectangle DeviceTemperatureBounds => deviceTemperatureSlider.Bounds;
    internal Rectangle DevicePowerBounds => devicePowerSlider.Bounds;
    public float DeviceTargetTemperature => deviceTemperatureSlider.Value;
    public float DeviceMaximumPower => devicePowerSlider.Value;
    private readonly UiValueSlider scaleSlider = new(
        "Масштаб симуляции",
        [25, 35, 50, 75, 85, 100],
        SimulationSettings.DefaultScale * 100,
        "%",
        "0");
    private readonly UiToggleSwitch solidGravityToggle = new("Гравитация построек");
    private readonly UiSimulationModeSelector modeSelector = new();
    private int modeHintY;
    private readonly UiToggleSwitch hydraulicsToggle = new("Гидравлика сосудов");
    private readonly UiToggleSwitch withoutEffectsToggle = new("Без эффектов");

    /// <summary>
    /// Показать поле воздуха вместо сцены. Без этого настройка огня велась
    /// вслепую: скорость потока не видна, и каждая оценка её величины
    /// промахивалась на порядок.
    /// </summary>
    private readonly UiToggleSwitch airFieldToggle = new("Показать воздух");

    /// <summary>
    /// Сплошные стены по краям. Выключено — всё улетающее за левый, правый и
    /// верхний край исчезает, как в The Powder Toy. Пол остаётся всегда.
    /// </summary>
    private readonly UiToggleSwitch boundariesToggle = new("Стены по краям");
    private readonly UiIconButton restartPhysicsButton = new("Перезапустить физику") { IconKey = "reset" };
    private readonly UiIconButton resetViewButton = new("Сбросить вид") { IconKey = "reset" };
    private readonly UiIconButton clearButton = new("Очистить всё") { IconKey = "clear", Danger = true };

    private Rectangle toolCardBounds;
    private Rectangle materialCardBounds;
    private Rectangle cameraInfoBounds;
    private int toolParametersHeaderY;
    private int simulationHeaderY;
    private float clearConfirmTimer;

    public bool GravityToggled { get; private set; }
    public bool HydraulicsToggled { get; private set; }
    public bool ModeChanged { get; private set; }
    internal Rectangle SandboxModeBounds => modeSelector.SandboxBounds;
    internal Rectangle SimulationModeBounds => modeSelector.SimulationBounds;
    public bool ResetRequested { get; private set; }
    public bool ResetViewRequested { get; private set; }
    public bool ClearRequested { get; private set; }
    public bool ScaleChanged { get; private set; }
    internal Rectangle ScaleSliderBounds => scaleSlider.Bounds;
    internal Rectangle ResetButtonBounds => restartPhysicsButton.Bounds;
    internal Rectangle ResetViewButtonBounds => resetViewButton.Bounds;
    internal Rectangle ClearButtonBounds => clearButton.Bounds;
    internal Rectangle BrushSliderBounds => brushSlider.Bounds;
    internal Rectangle DensitySliderBounds => densitySlider.Bounds;
    internal Rectangle TemperatureSliderBounds => temperatureSlider.Bounds;
    internal Rectangle GravityToggleBounds => solidGravityToggle.Bounds;
    internal Rectangle HydraulicsToggleBounds => hydraulicsToggle.Bounds;
    internal Rectangle AirFieldToggleBounds => airFieldToggle.Bounds;
    internal Rectangle BoundariesToggleBounds => boundariesToggle.Bounds;
    internal Rectangle WithoutEffectsToggleBounds => withoutEffectsToggle.Bounds;

    internal static bool ShowsDensity(PhyxelToolId tool) => tool == PhyxelToolId.Brush;
    internal static bool ShowsTemperature(PhyxelToolId tool) => tool == PhyxelToolId.Temperature;
    internal static bool ShowsBrushControls(PhyxelToolId tool) =>
        tool is PhyxelToolId.Brush or PhyxelToolId.Eraser or PhyxelToolId.Temperature or PhyxelToolId.Filter;

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
            temperatureSlider.IsDragging || scaleSlider.IsDragging ||
            deviceTemperatureSlider.IsDragging || devicePowerSlider.IsDragging;

        GravityToggled = false;
        HydraulicsToggled = false;
        ModeChanged = false;
        ResetRequested = false;
        ResetViewRequested = false;
        ClearRequested = false;
        ScaleChanged = false;
        clearConfirmTimer = Math.Max(0f, clearConfirmTimer - input.DeltaSeconds);
        clearButton.Label = clearConfirmTimer > 0f ? "Подтвердить очистку" : "Очистить всё";

        int padding = 14;
        int innerX = bounds.X + padding;
        int innerWidth = bounds.Width - padding * 2;
        bool showDevice = activeTool == PhyxelToolId.Brush && selectedMaterial.ThermalRegulator is not null;
        bool compact = bounds.Height < Math.Max(showDevice ? 1100 : 800,
            font.LineSpacing * (showDevice ? 25 : 23) + 100);
        bool compactDevice = compact && showDevice;
        int cardHeight = compact ? Math.Clamp(font.LineSpacing + 3, 32, 48) : Math.Clamp(font.LineSpacing + 24, 42, 58);
        toolCardBounds = new Rectangle(innerX, bounds.Y + font.LineSpacing + (compact ? 18 : 24), innerWidth, cardHeight);
        if (compact) toolCardBounds = Rectangle.Empty;
        int cursorY = compact ? bounds.Y + font.LineSpacing + 14 : toolCardBounds.Bottom + 8;

        if (activeTool == PhyxelToolId.Pan)
        {
            materialCardBounds = Rectangle.Empty;
        }
        else
        {
            materialCardBounds = new Rectangle(innerX, cursorY, innerWidth, cardHeight);
            cursorY = materialCardBounds.Bottom + (compact ? 8 : 12);
        }
        if (compactDevice || (compact && activeTool==PhyxelToolId.Filter))
        {
            // Device controls have an extra slider; show the selected device
            // in the section title instead of spending another card row.
            materialCardBounds = Rectangle.Empty;
            cursorY = bounds.Y + font.LineSpacing + 14;
        }

        toolParametersHeaderY = cursorY;
        cursorY += Math.Max(16, (int)MathF.Round(font.LineSpacing * (compact ? 0.68f : 0.82f))) + (compact ? 4 : 8);
        int sliderHeight = font.LineSpacing + (compact ? 22 : 30);
        int sliderGap = compactDevice ? 2 : compact ? 6 : 10;

        if (ShowsBrushControls(activeTool))
        {
            brushSlider.Bounds = new Rectangle(innerX, cursorY, innerWidth, sliderHeight);
            if (brushSlider.Update(input)) settings.BrushRadius = (int)brushSlider.Value;
            cursorY += sliderHeight + sliderGap;
        }
        else
        {
            brushSlider.CancelDrag();
        }

        filterSelector.Bounds=Rectangle.Empty;filterHintY=0;
        if(activeTool==PhyxelToolId.Filter){
            filterSelector.Bounds=new Rectangle(innerX,cursorY,innerWidth,Math.Clamp(font.LineSpacing+16,36,52));
            if(filterSelector.Update(input))settings.FilterSelection=(FilterSelection)(((int)settings.FilterSelection+1)%7);
            filterSelector.Label=FilterRules.Label(settings.FilterSelection,selectedMaterial.Name);
            if(settings.FilterSelection==FilterSelection.SelectedMaterial &&
               (MaterialSimulationKind)selectedMaterial.Properties.SimulationKind is not
                   (MaterialSimulationKind.Gas or MaterialSimulationKind.Liquid or MaterialSimulationKind.Granular))
                filterSelector.Label="Закрытый фильтр";
            filterSelector.Label = UiCategoryPalette.TruncateToWidth(font, filterSelector.Label, Math.Max(1, innerWidth - 24));
            cursorY=filterSelector.Bounds.Bottom+6;filterHintY=cursorY;cursorY+=Math.Max(16,(int)(font.LineSpacing*.65f))+8;
        }
        deviceHintY = 0;
        if (showDevice)
        {
            densitySlider.CancelDrag();
            temperatureSlider.CancelDrag();
            if (selectedDeviceId != selectedMaterial.Id)
            {
                if (selectedDeviceId is not null)
                    devicePreferences[selectedDeviceId] = (deviceTemperatureSlider.Value, devicePowerSlider.Value);
                selectedDeviceId = selectedMaterial.Id;
                var defaults = selectedMaterial.ThermalRegulator!;
                var preference = devicePreferences.TryGetValue(selectedDeviceId, out var saved)
                    ? saved : (defaults.TargetTemperature, defaults.MaximumPower);
                deviceTemperatureSlider.Value = preference.Item1;
                devicePowerSlider.Value = preference.Item2;
            }
            deviceTemperatureSlider.Bounds = new Rectangle(innerX, cursorY, innerWidth, sliderHeight);
            deviceTemperatureSlider.Update(input);
            cursorY += sliderHeight + sliderGap;
            devicePowerSlider.Bounds = new Rectangle(innerX, cursorY, innerWidth, sliderHeight);
            devicePowerSlider.Update(input);
            cursorY += sliderHeight + sliderGap;
            deviceHintY = compactDevice ? 0 : cursorY;
            if (!compactDevice) cursorY += Math.Max(14, (int)(font.LineSpacing * .65f)) + 4;
        }
        else
        {
            deviceTemperatureSlider.CancelDrag();
            devicePowerSlider.CancelDrag();
        }

        if (!showDevice && activeTool == PhyxelToolId.Temperature)
        {
            densitySlider.CancelDrag();
            temperatureSlider.Bounds = new Rectangle(innerX, cursorY, innerWidth, sliderHeight);
            temperatureSlider.Update(input);
            cursorY += sliderHeight + sliderGap;
        }
        else if (!showDevice && ShowsDensity(activeTool))
        {
            temperatureSlider.CancelDrag();
            densitySlider.Bounds = new Rectangle(innerX, cursorY, innerWidth, sliderHeight);
            if (densitySlider.Update(input)) settings.SpawnDensity = densitySlider.Value / 100f;
            cursorY += sliderHeight + sliderGap;
        }
        else if (!showDevice)
        {
            densitySlider.CancelDrag();
            temperatureSlider.CancelDrag();
        }

        if (activeTool == PhyxelToolId.Pan)
        {
            int cameraRowHeight = Math.Clamp(font.LineSpacing + 18, 40, 52);
            cameraInfoBounds = new Rectangle(innerX, cursorY, innerWidth, cameraRowHeight);
            cursorY += cameraRowHeight + 7;
            resetViewButton.Bounds = new Rectangle(innerX, cursorY, innerWidth, cameraRowHeight);
            if (resetViewButton.Update(input)) ResetViewRequested = true;
            cursorY += cameraRowHeight + 14;
        }
        else
        {
            cameraInfoBounds = Rectangle.Empty;
            resetViewButton.Bounds = Rectangle.Empty;
        }

        simulationHeaderY = cursorY;
        cursorY += Math.Max(16, (int)MathF.Round(font.LineSpacing * (compact ? 0.68f : 0.82f))) + (compact ? 4 : 8);
        scaleSlider.Bounds = new Rectangle(innerX, cursorY, innerWidth, sliderHeight);
        if (scaleSlider.Update(input))
        {
            settings.ApplyScale(scaleSlider.Value / 100f);
            ScaleChanged = true;
        }
        cursorY += sliderHeight + sliderGap;

        int toggleHeight = compact
            ? Math.Clamp(font.LineSpacing, 24, 44)
            : Math.Clamp(font.LineSpacing + (compact ? 12 : 18), 36, 52);
        modeSelector.Bounds = new Rectangle(innerX, cursorY, innerWidth, Math.Clamp(font.LineSpacing + 6, 28, 44));
        ModeChanged = modeSelector.Update(input, settings);
        modeHintY = modeSelector.Bounds.Bottom + 3;
        cursorY = modeHintY + Math.Max(12, (int)(font.LineSpacing * .6f)) + (compactDevice ? 2 : 5);
        solidGravityToggle.Bounds = new Rectangle(innerX, cursorY, innerWidth, toggleHeight);
        if (solidGravityToggle.Update(input))
        {
            settings.SolidGravity = !settings.SolidGravity;
            GravityToggled = true;
        }
        cursorY += toggleHeight + (compact ? 2 : 4);
        hydraulicsToggle.Bounds = new Rectangle(innerX, cursorY, innerWidth, toggleHeight);
        if (hydraulicsToggle.Update(input))
        {
            settings.HydraulicPressure = !settings.HydraulicPressure;
            HydraulicsToggled = true;
        }
        cursorY += toggleHeight + (compact ? 2 : 4);
        withoutEffectsToggle.Bounds = new Rectangle(innerX, cursorY, innerWidth, toggleHeight);
        if (withoutEffectsToggle.Update(input))
        {
            settings.RenderWithoutEffects = !settings.RenderWithoutEffects;
        }
        cursorY += toggleHeight + (compact ? 2 : 4);
        boundariesToggle.Bounds = new Rectangle(innerX, cursorY, innerWidth, toggleHeight);
        if (boundariesToggle.Update(input))
        {
            settings.OpenBoundaries = !settings.OpenBoundaries;
        }
        cursorY += toggleHeight + (compact ? 2 : 4);
        airFieldToggle.Bounds = new Rectangle(innerX, cursorY, innerWidth, toggleHeight);
        if (airFieldToggle.Update(input))
        {
            settings.ShowAirField = !settings.ShowAirField;
        }

        brushSlider.Value = settings.BrushRadius;
        densitySlider.Value = settings.SpawnDensity * 100f;
        scaleSlider.Value = settings.Scale * 100f;

        LayoutActions(bounds, font, innerX, innerWidth, padding);
        if (restartPhysicsButton.Update(input)) ResetRequested = true;
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
        MaterialDefinition selectedMaterial,
        MaterialCardPreviewCache previewCache,
        UiIconTextureCache iconCache,
        float cameraZoom)
    {
        backdrop.Draw(spriteBatch, bounds, 10);
        DrawSectionLabel(spriteBatch, font, "СВОЙСТВА", bounds.X + 14, bounds.Y + 10, 0.78f);

        if (toolCardBounds != Rectangle.Empty) DrawToolCard(spriteBatch, font, backdrop, pixel, iconCache, activeTool);
        if (materialCardBounds != Rectangle.Empty)
        {
            DrawMaterialCard(spriteBatch, font, backdrop, pixel, selectedMaterial, previewCache);
        }

        DrawSectionLabel(spriteBatch, font, activeTool == PhyxelToolId.Pan ? "КАМЕРА" : materialCardBounds == Rectangle.Empty && selectedMaterial.ThermalRegulator is not null && activeTool == PhyxelToolId.Brush ? selectedMaterial.Name.ToUpperInvariant() : "ПАРАМЕТРЫ ИНСТРУМЕНТА",
            bounds.X + 14, toolParametersHeaderY, 0.68f);
        if (ShowsBrushControls(activeTool)) brushSlider.Draw(spriteBatch, font, backdrop, pixel);
        if(activeTool==PhyxelToolId.Filter){
            filterSelector.Draw(spriteBatch,font,backdrop,pixel,iconCache);
            spriteBatch.DrawString(font,"ЛКМ — нанести · ПКМ — снять",new Vector2(bounds.X+14,filterHintY),UiTheme.TextMuted,0,Vector2.Zero,.65f,SpriteEffects.None,0);
        }
        if (activeTool == PhyxelToolId.Brush && selectedMaterial.ThermalRegulator is not null)
        {
            deviceTemperatureSlider.Draw(spriteBatch, font, backdrop, pixel);
            devicePowerSlider.Draw(spriteBatch, font, backdrop, pixel);
            if (deviceHintY > 0) spriteBatch.DrawString(font, "Нанесите кистью · 0 — выключено", new Vector2(bounds.X + 14, deviceHintY),
                UiTheme.TextMuted, 0, Vector2.Zero, .6f, SpriteEffects.None, 0);
        }
        else if (activeTool == PhyxelToolId.Temperature) temperatureSlider.Draw(spriteBatch, font, backdrop, pixel);
        else if (ShowsDensity(activeTool)) densitySlider.Draw(spriteBatch, font, backdrop, pixel);

        if (activeTool == PhyxelToolId.Pan)
        {
            backdrop.DrawRoundedRectangle(spriteBatch, cameraInfoBounds, UiTheme.CardBackground, 6);
            UiIconRenderer.DrawStrokedRectangle(spriteBatch, pixel, cameraInfoBounds, 1, UiTheme.BorderColor);
            spriteBatch.DrawString(font, "Масштаб вида", new Vector2(cameraInfoBounds.X + 10,
                cameraInfoBounds.Center.Y - font.LineSpacing / 2f), UiTheme.TextSecondary);
            string zoomText = $"{cameraZoom:0.00}x";
            Vector2 zoomSize = font.MeasureString(zoomText);
            spriteBatch.DrawString(font, zoomText, new Vector2(cameraInfoBounds.Right - zoomSize.X - 10,
                cameraInfoBounds.Center.Y - font.LineSpacing / 2f), UiTheme.TextPrimary);
            resetViewButton.Draw(spriteBatch, font, backdrop, pixel, iconCache);
        }

        DrawSectionLabel(spriteBatch, font, "СИМУЛЯЦИЯ", bounds.X + 14, simulationHeaderY, 0.68f);
        scaleSlider.Draw(spriteBatch, font, backdrop, pixel);
        modeSelector.Draw(spriteBatch, font, backdrop, pixel, settings.Mode);
        spriteBatch.DrawString(font, settings.Mode == SimulationMode.Simulation
            ? "Кислород расходуется" : "Горение без обязательного поддува",
            new Vector2(bounds.X + 14, modeHintY), UiTheme.TextMuted,
            0, Vector2.Zero, .55f, SpriteEffects.None, 0);
        solidGravityToggle.Draw(spriteBatch, font, backdrop, pixel, settings.SolidGravity);
        hydraulicsToggle.Draw(spriteBatch, font, backdrop, pixel, settings.HydraulicPressure);
        withoutEffectsToggle.Draw(spriteBatch, font, backdrop, pixel, settings.RenderWithoutEffects);
        // Тумблер показывает «стены есть», поэтому значение инвертировано
        // относительно OpenBoundaries.
        boundariesToggle.Draw(spriteBatch, font, backdrop, pixel, !settings.OpenBoundaries);
        airFieldToggle.Draw(spriteBatch, font, backdrop, pixel, settings.ShowAirField);

        spriteBatch.Draw(pixel, new Rectangle(bounds.X + 14, restartPhysicsButton.Bounds.Y - 9, bounds.Width - 28, 1), UiTheme.BorderColor);
        restartPhysicsButton.Draw(spriteBatch, font, backdrop, pixel, iconCache);
        clearButton.Draw(spriteBatch, font, backdrop, pixel, iconCache);
    }

    private void DrawToolCard(
        SpriteBatch spriteBatch,
        SpriteFont font,
        UiPanelBackdropRenderer backdrop,
        Texture2D pixel,
        UiIconTextureCache iconCache,
        PhyxelToolId activeTool)
    {
        backdrop.DrawRoundedRectangle(spriteBatch, toolCardBounds, UiTheme.CardBackground, 7);
        UiIconRenderer.DrawStrokedRectangle(spriteBatch, pixel, toolCardBounds, 1, UiTheme.BorderColor);
        string toolName = activeTool switch
        {
            PhyxelToolId.Brush => "Кисть",
            PhyxelToolId.Eraser => "Ластик",
            PhyxelToolId.Temperature => "Температура",
            PhyxelToolId.Filter => "Фильтр",
            PhyxelToolId.Pan => "Камера / панорама",
            _ => "Инструмент"
        };
        string toolKey = activeTool switch
        {
            PhyxelToolId.Brush => "brush",
            PhyxelToolId.Eraser => "eraser",
            PhyxelToolId.Temperature => "temperature",
            PhyxelToolId.Filter => "settings",
            _ => "pan"
        };
        int iconSize = Math.Clamp(toolCardBounds.Height - 18, 24, 32);
        Rectangle icon = new(toolCardBounds.X + 11, toolCardBounds.Center.Y - iconSize / 2, iconSize, iconSize);
        if (iconCache.TryGet(toolKey, out Texture2D iconTexture))
        {
            UiIconRenderer.DrawIcon(spriteBatch, iconTexture, icon, UiTheme.PrimaryAccent);
        }
        else
        {
            UiIconRenderer.DrawToolIcon(spriteBatch, pixel, toolKey, icon, UiTheme.PrimaryAccent);
        }
        spriteBatch.DrawString(font, toolName,
            new Vector2(icon.Right + 10, toolCardBounds.Center.Y - font.LineSpacing / 2f), UiTheme.TextPrimary);
    }

    private void DrawMaterialCard(
        SpriteBatch spriteBatch,
        SpriteFont font,
        UiPanelBackdropRenderer backdrop,
        Texture2D pixel,
        MaterialDefinition material,
        MaterialCardPreviewCache previewCache)
    {
        backdrop.DrawRoundedRectangle(spriteBatch, materialCardBounds, UiTheme.CardBackground, 7);
        UiIconRenderer.DrawStrokedRectangle(spriteBatch, pixel, materialCardBounds, 1, UiTheme.BorderColor);
        int previewSize = materialCardBounds.Height - 12;
        Rectangle previewBounds = new(materialCardBounds.X + 6, materialCardBounds.Y + 6, previewSize, previewSize);
        spriteBatch.Draw(pixel, previewBounds, material.Color);
        if (previewCache.TryGetPreview(material.Id, out Texture2D preview))
        {
            spriteBatch.Draw(preview, previewBounds, UiCategoryPalette.CalculateAspectFillSource(preview, previewBounds), material.ThermalRegulator is null ? Color.White : material.Color);
        }
        else
        {
            spriteBatch.Draw(previewCache.FallbackTexture, previewBounds, Color.White);
        }
        UiIconRenderer.DrawStrokedRectangle(spriteBatch, pixel, previewBounds, 1, UiTheme.BorderHighlight);
        string name = TruncateToWidth(font, material.Name, materialCardBounds.Right - previewBounds.Right - 20);
        spriteBatch.DrawString(font, name,
            new Vector2(previewBounds.Right + 10, materialCardBounds.Center.Y - font.LineSpacing / 2f), UiTheme.TextPrimary);
    }

    private void LayoutActions(Rectangle bounds, SpriteFont font, int innerX, int innerWidth, int padding)
    {
        int buttonHeight = Math.Clamp(font.LineSpacing + 16, 40, 50);
        int gap = 7;
        int restartWidth = (int)MathF.Ceiling(font.MeasureString(restartPhysicsButton.Label).X) + 46;
        int clearWidth = (int)MathF.Ceiling(font.MeasureString(clearButton.Label).X) + 46;
        if (restartWidth + clearWidth + gap <= innerWidth)
        {
            restartPhysicsButton.Bounds = new Rectangle(innerX, bounds.Bottom - buttonHeight - padding, restartWidth, buttonHeight);
            clearButton.Bounds = new Rectangle(restartPhysicsButton.Bounds.Right + gap, restartPhysicsButton.Bounds.Y,
                innerWidth - restartWidth - gap, buttonHeight);
        }
        else
        {
            int firstY = bounds.Bottom - padding - buttonHeight * 2 - gap;
            restartPhysicsButton.Bounds = new Rectangle(innerX, firstY, innerWidth, buttonHeight);
            clearButton.Bounds = new Rectangle(innerX, firstY + buttonHeight + gap, innerWidth, buttonHeight);
        }
    }

    private static void DrawSectionLabel(SpriteBatch spriteBatch, SpriteFont font, string text, int x, int y, float scale)
    {
        spriteBatch.DrawString(font, text, new Vector2(x, y), UiTheme.TextMuted, 0, Vector2.Zero, scale, SpriteEffects.None, 0);
    }

    private static string TruncateToWidth(SpriteFont font, string text, int maximumWidth)
    {
        if (font.MeasureString(text).X <= maximumWidth) return text;
        const string ellipsis = "…";
        int length = text.Length;
        while (length > 0 && font.MeasureString(text[..length] + ellipsis).X > maximumWidth) length--;
        return length == 0 ? ellipsis : text[..length] + ellipsis;
    }
}
