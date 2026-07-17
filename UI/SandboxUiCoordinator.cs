using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Input;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.UI;

public readonly record struct UiFrameActions(
    bool ClearRequested,
    bool SaveRequested,
    bool LoadRequested,
    bool ScaleChanged,
    bool GravityChanged,
    bool HydraulicsChanged);

public sealed class SandboxUiCoordinator
{
    private readonly MaterialRegistry materialRegistry;
    private readonly SpriteFont font;
    private readonly Texture2D pixel;
    private readonly Texture2D brushOutline;
    private readonly UiPanelBackdropRenderer panelRenderer;
    private readonly List<(ushort RuntimeIndex, UiIconButton Button)> materialButtons = [];
    private readonly UiIconButton pauseButton = new(UiLocalizationProvider.Pause);
    private readonly UiIconButton clearButton = new(UiLocalizationProvider.Clear);
    private readonly UiIconButton saveButton = new(UiLocalizationProvider.Save);
    private readonly UiIconButton loadButton = new(UiLocalizationProvider.Load);
    private readonly UiIconButton solidGravityButton = new(UiLocalizationProvider.SolidGravity);
    private readonly UiIconButton hydraulicsButton = new(UiLocalizationProvider.HydraulicPressure);
    private readonly UiValueSlider brushSlider;
    private readonly UiValueSlider densitySlider;
    private readonly UiValueSlider scaleSlider;
    private float clearConfirmationRemaining;
    private bool compactLayout;

    public SandboxUiCoordinator(
        MaterialRegistry materialRegistry,
        SpriteFont font,
        GpuResourceLifecycleManager resources)
    {
        this.materialRegistry = materialRegistry;
        this.font = font;
        pixel = resources.PixelTexture;
        brushOutline = resources.BrushOutlineTexture;
        panelRenderer = new UiPanelBackdropRenderer(resources.PixelTexture, resources.CircleTexture);
        foreach (MaterialDefinition material in materialRegistry.SelectableMaterials)
        {
            UiIconButton button = new(material.Name)
            {
                AccentColor = material.Color
            };
            materialButtons.Add((material.RuntimeIndex, button));
        }

        brushSlider = new UiValueSlider(UiLocalizationProvider.BrushSize, 1, 96, 1, 18, " px");
        densitySlider = new UiValueSlider(UiLocalizationProvider.SpawnDensity, 5, 100, 1, 82, "%");
        scaleSlider = new UiValueSlider(
            "Масштаб симуляции",
            [25, 35, 50, 75, 85, 100],
            SimulationSettings.DefaultScale * 100,
            "%",
            "0");
        SelectedMaterial = materialRegistry.GetRequiredRuntimeIndex(CoreMaterialIds.Sand);
    }

    public ushort SelectedMaterial { get; set; }
    public Rectangle CanvasBounds { get; private set; }
    public Rectangle SidePanelBounds { get; private set; }
    public Rectangle InfoPanelBounds { get; private set; }
    public bool PointerConsumed { get; private set; }

    public UiFrameActions Update(RawInputSnapshot input, Viewport viewport, SimulationSettings settings)
    {
        Layout(viewport);
        PointerConsumed = SidePanelBounds.Contains(input.MousePosition) || InfoPanelBounds.Contains(input.MousePosition);
        if (input.WheelDelta != 0 && CanvasBounds.Contains(input.MousePosition) && !PointerConsumed)
        {
            settings.BrushRadius = Math.Clamp(settings.BrushRadius + Math.Sign(input.WheelDelta) * 2, 1, 96);
        }

        clearConfirmationRemaining = Math.Max(0f, clearConfirmationRemaining - input.DeltaSeconds);
        for (int index = 0; index < materialButtons.Count; index++)
        {
            (ushort runtimeIndex, UiIconButton button) = materialButtons[index];
            button.Active = runtimeIndex == SelectedMaterial;
            if (button.Update(input))
            {
                SelectedMaterial = runtimeIndex;
            }
        }

        pauseButton.Active = settings.Paused;
        pauseButton.Label = compactLayout
            ? settings.Paused ? "Продолжить" : "Пауза"
            : settings.Paused ? UiLocalizationProvider.Continue : UiLocalizationProvider.Pause;
        if (pauseButton.Update(input))
        {
            settings.Paused = !settings.Paused;
        }

        solidGravityButton.Active = settings.SolidGravity;
        bool gravityChanged = false;
        if (solidGravityButton.Update(input))
        {
            settings.SolidGravity = !settings.SolidGravity;
            solidGravityButton.Active = settings.SolidGravity;
            gravityChanged = true;
        }

        hydraulicsButton.Active = settings.HydraulicPressure;
        bool hydraulicsChanged = false;
        if (hydraulicsButton.Update(input))
        {
            settings.HydraulicPressure = !settings.HydraulicPressure;
            hydraulicsButton.Active = settings.HydraulicPressure;
            hydraulicsChanged = true;
        }

        bool clearRequested = false;
        if (clearButton.Update(input))
        {
            if (clearConfirmationRemaining > 0f)
            {
                clearRequested = true;
                clearConfirmationRemaining = 0f;
            }
            else
            {
                clearConfirmationRemaining = 3f;
            }
        }

        clearButton.Label = clearConfirmationRemaining > 0f
            ? compactLayout ? "Подтвердить" : UiLocalizationProvider.ConfirmClear
            : compactLayout ? "Очистить" : UiLocalizationProvider.Clear;
        solidGravityButton.Label = UiLocalizationProvider.SolidGravity;
        hydraulicsButton.Label = compactLayout ? "Сосуды" : UiLocalizationProvider.HydraulicPressure;
        saveButton.Label = compactLayout ? "Сохранить" : UiLocalizationProvider.Save;
        loadButton.Label = compactLayout ? "Загрузить" : UiLocalizationProvider.Load;
        bool saveRequested = saveButton.Update(input) || input.SavePressed;
        bool loadRequested = loadButton.Update(input) || input.LoadPressed;
        if (brushSlider.Update(input))
        {
            settings.BrushRadius = (int)brushSlider.Value;
        }

        if (densitySlider.Update(input))
        {
            settings.SpawnDensity = densitySlider.Value / 100f;
        }

        bool scaleChanged = scaleSlider.Update(input);
        if (scaleChanged)
        {
            settings.ApplyScale(scaleSlider.Value / 100f);
        }

        brushSlider.Value = settings.BrushRadius;
        densitySlider.Value = settings.SpawnDensity * 100f;
        scaleSlider.Value = settings.Scale * 100f;
        return new UiFrameActions(
            clearRequested,
            saveRequested,
            loadRequested,
            scaleChanged,
            gravityChanged,
            hydraulicsChanged);
    }

    public void Draw(
        SpriteBatch spriteBatch,
        SimulationSettings settings,
        SimulationStatistics statistics,
        double framesPerSecond,
        string transientStatus,
        TemperatureProbeResult? temperatureProbe)
    {
        panelRenderer.Draw(spriteBatch, SidePanelBounds, 12);
        panelRenderer.Draw(spriteBatch, InfoPanelBounds, 8);
        spriteBatch.DrawString(
            font,
            UiLocalizationProvider.Materials,
            new Vector2(SidePanelBounds.X + 14, SidePanelBounds.Y + 12),
            new Color(170, 170, 170));
        foreach ((ushort _, UiIconButton button) in materialButtons)
        {
            button.Draw(spriteBatch, font, panelRenderer, pixel, true);
        }

        brushSlider.Draw(spriteBatch, font, panelRenderer, pixel);
        densitySlider.Draw(spriteBatch, font, panelRenderer, pixel);
        scaleSlider.Draw(spriteBatch, font, panelRenderer, pixel);
        pauseButton.Draw(spriteBatch, font, panelRenderer, pixel);
        solidGravityButton.Draw(spriteBatch, font, panelRenderer, pixel);
        hydraulicsButton.Draw(spriteBatch, font, panelRenderer, pixel);
        clearButton.Draw(spriteBatch, font, panelRenderer, pixel);
        saveButton.Draw(spriteBatch, font, panelRenderer, pixel);
        loadButton.Draw(spriteBatch, font, panelRenderer, pixel);
        string selectedName = materialRegistry[SelectedMaterial].Name;
        string gravityState = settings.SolidGravity ? "вкл" : "выкл";
        string hydraulicsState = settings.HydraulicPressure ? "вкл" : "выкл";
        string probeInfo = FormatTemperatureProbe(materialRegistry, temperatureProbe);
        string info = $"FPS {framesPerSecond,5:0}   Частицы {statistics.ActiveCells:N0}   Твердые {statistics.SolidCells:N0}   Выбрано: {selectedName}   {probeInfo}   Кисть: {settings.BrushRadius} px   Гравитация: {gravityState}   Сосуды: {hydraulicsState}";
        spriteBatch.DrawString(font, info, new Vector2(InfoPanelBounds.X + 12, InfoPanelBounds.Y + 9), Color.White);
        if (!string.IsNullOrWhiteSpace(transientStatus))
        {
            Vector2 statusSize = font.MeasureString(transientStatus);
            spriteBatch.DrawString(
                font,
                transientStatus,
                new Vector2(InfoPanelBounds.Right - statusSize.X - 12, InfoPanelBounds.Y + 9),
                new Color(128, 198, 238));
        }
    }

    public static string FormatTemperatureProbe(
        MaterialRegistry materialRegistry,
        TemperatureProbeResult? probe)
    {
        if (probe is null)
        {
            return "Температура: —";
        }
        TemperatureProbeResult value = probe.Value;
        if (value.IsActive == 0)
        {
            return $"Материал: {materialRegistry[CoreMaterialIds.Empty].Name}   Температура: —";
        }
        if (value.MaterialIndex > ushort.MaxValue ||
            !materialRegistry.TryGet((ushort)value.MaterialIndex, out MaterialDefinition material) ||
            !float.IsFinite(value.Temperature))
        {
            return "Материал: Неизвестно   Температура: —";
        }
        string temperature = value.Temperature.ToString(
            "0.0",
            CultureInfo.GetCultureInfo("ru-RU"));
        return $"Материал: {material.Name}   Температура: {temperature} °C";
    }

    public void DrawBrushIndicator(
        SpriteBatch spriteBatch,
        Point pointer,
        Rectangle worldBounds,
        SimulationSettings settings,
        bool eraseOverride)
    {
        if (!worldBounds.Contains(pointer))
        {
            return;
        }

        float pixelScale = worldBounds.Width / (float)Math.Max(1, settings.Width);
        int diameter = Math.Max(3, (int)MathF.Round((settings.BrushRadius * 2 + 1) * pixelScale));
        Rectangle bounds = new(pointer.X - diameter / 2, pointer.Y - diameter / 2, diameter, diameter);
        bool erasing = eraseOverride ||
            (MaterialSimulationKind)materialRegistry[SelectedMaterial].Properties.SimulationKind ==
            MaterialSimulationKind.Tool;
        Color color = erasing ? new Color(255, 96, 96, 190) : new Color(210, 235, 255, 175);
        spriteBatch.Draw(brushOutline, bounds, color);
    }

    private void Layout(Viewport viewport)
    {
        compactLayout = viewport.Height < 750;
        float scale = Math.Clamp(viewport.Height / 1080f, 0.72f, 1.35f);
        int margin = Math.Max(8, (int)(12 * scale));
        int sideWidth = Math.Max(206, (int)(220 * scale));
        int infoHeight = Math.Max(38, (int)(40 * scale));
        SidePanelBounds = new Rectangle(
            viewport.Width - sideWidth - margin,
            margin,
            sideWidth,
            viewport.Height - infoHeight - margin * 3);
        InfoPanelBounds = new Rectangle(
            margin,
            viewport.Height - infoHeight - margin,
            viewport.Width - margin * 2,
            infoHeight);
        int compactToolbarHeight = compactLayout ? 40 : 0;
        CanvasBounds = new Rectangle(
            0,
            compactToolbarHeight,
            SidePanelBounds.X - margin,
            InfoPanelBounds.Y - margin - compactToolbarHeight);
        int innerX = SidePanelBounds.X + 10;
        int innerWidth = SidePanelBounds.Width - 20;
        int cursorY = SidePanelBounds.Y + 42;
        int buttonHeight = compactLayout ? 30 : Math.Max(36, (int)(40 * scale));
        int buttonSpacing = compactLayout ? 3 : 5;
        foreach ((ushort _, UiIconButton button) in materialButtons)
        {
            button.Bounds = new Rectangle(innerX, cursorY, innerWidth, buttonHeight);
            cursorY += buttonHeight + buttonSpacing;
        }

        cursorY += compactLayout ? 24 : 28;
        brushSlider.Bounds = new Rectangle(innerX + 4, cursorY, innerWidth - 8, 22);
        cursorY += compactLayout ? 49 : 64;
        densitySlider.Bounds = new Rectangle(innerX + 4, cursorY, innerWidth - 8, 22);
        cursorY += compactLayout ? 49 : 64;
        scaleSlider.Bounds = new Rectangle(innerX + 4, cursorY, innerWidth - 8, 22);
        UiIconButton[] serviceButtons =
            [pauseButton, solidGravityButton, hydraulicsButton, clearButton, saveButton, loadButton];
        if (compactLayout)
        {
            int toolbarWidth = CanvasBounds.Width;
            int toolbarGap = 4;
            int serviceWidth = (toolbarWidth - toolbarGap * (serviceButtons.Length - 1)) / serviceButtons.Length;
            for (int index = 0; index < serviceButtons.Length; index++)
            {
                serviceButtons[index].Bounds = new Rectangle(
                    index * (serviceWidth + toolbarGap),
                    4,
                    serviceWidth,
                    32);
            }

            return;
        }

        cursorY += 45;
        foreach (UiIconButton button in serviceButtons)
        {
            button.Bounds = new Rectangle(innerX, cursorY, innerWidth, buttonHeight);
            cursorY += buttonHeight + 5;
        }
    }
}
