using System;
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

public sealed class SandboxUiCoordinator : IDisposable
{
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
            System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
        return $"Материал: {material.Name}   Температура: {temperature} °C";
    }
    private readonly MaterialRegistry materialRegistry;
    private readonly SpriteFont font;
    private readonly Texture2D pixel;
    private readonly Texture2D brushOutline;
    private readonly UiPanelBackdropRenderer panelRenderer;
    private readonly MaterialCardPreviewCache materialCardPreviews;

    private readonly UiTopBar topBar;
    private readonly UiLeftToolbar leftToolbar = new();
    private readonly UiPropertiesPanel propertiesPanel = new();
    private readonly UiCategoryPalette categoryPalette;
    private readonly UiStatusBar statusBar = new();

    private ushort selectedMaterial;
    private UiLayoutBounds currentLayout;

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
        materialCardPreviews = new MaterialCardPreviewCache(
            resources.PixelTexture.GraphicsDevice,
            System.IO.Path.Combine(AppContext.BaseDirectory, "Content", "UI", "MaterialCards"));
        topBar = new UiTopBar(font);
        categoryPalette = new UiCategoryPalette(materialRegistry, font, materialCardPreviews);

        SelectedMaterial = materialRegistry.GetRequiredRuntimeIndex(CoreMaterialIds.Sand);
    }

    public ushort SelectedMaterial
    {
        get => selectedMaterial;
        set
        {
            selectedMaterial = value;
            if (leftToolbar.ActiveTool == PhyxelToolId.Temperature)
            {
                leftToolbar.ActiveTool = PhyxelToolId.Brush;
            }
        }
    }

    public bool TemperatureToolActive => leftToolbar.ActiveTool == PhyxelToolId.Temperature;
    public float TargetTemperature => propertiesPanel.TargetTemperature;

    public Rectangle CanvasBounds => currentLayout.SimulationCanvas;
    public Rectangle SidePanelBounds => currentLayout.RightPanel;
    public Rectangle InfoPanelBounds => currentLayout.StatusBar;
    public bool PointerConsumed { get; private set; }

    public UiFrameActions Update(RawInputSnapshot input, Viewport viewport, SimulationSettings settings)
    {
        currentLayout = UiLayoutCalculator.Calculate(viewport);

        // 1. Top bar update
        bool paused = settings.Paused;
        topBar.Update(input, currentLayout.TopBar, ref paused);
        settings.Paused = paused;

        // 2. Left toolbar update
        leftToolbar.Update(input, currentLayout.LeftToolbar, out bool leftConsumed);

        // 3. Properties panel update
        MaterialDefinition currentMatDef = materialRegistry.TryGet(SelectedMaterial, out MaterialDefinition mat)
            ? mat
            : materialRegistry[CoreMaterialIds.Sand];

        propertiesPanel.Update(
            input,
            currentLayout.RightPanel,
            settings,
            leftToolbar.ActiveTool,
            currentMatDef,
            out bool rightConsumed);

        // Synchronize Temperature tool click
        if (leftToolbar.ActiveTool == PhyxelToolId.Eraser)
        {
            SelectedMaterial = materialRegistry.GetRequiredRuntimeIndex(CoreMaterialIds.Eraser);
        }

        // 4. Category palette update
        ushort? newlySelected = categoryPalette.Update(
            input,
            currentLayout.BottomPalette,
            SelectedMaterial,
            TemperatureToolActive,
            out bool bottomConsumed);

        if (newlySelected.HasValue)
        {
            SelectedMaterial = newlySelected.Value;
            leftToolbar.ActiveTool = PhyxelToolId.Brush;
        }

        bool topConsumed = currentLayout.TopBar.Contains(input.MousePosition);
        bool statusConsumed = currentLayout.StatusBar.Contains(input.MousePosition);

        PointerConsumed = topConsumed || leftConsumed || rightConsumed || bottomConsumed || statusConsumed;

        // Mouse Wheel brush size inside Canvas
        if (input.WheelDelta != 0 && CanvasBounds.Contains(input.MousePosition) && !PointerConsumed)
        {
            settings.BrushRadius = Math.Clamp(settings.BrushRadius + Math.Sign(input.WheelDelta) * 2, 1, 96);
        }

        bool clearRequested = propertiesPanel.ClearRequested;
        bool saveRequested = topBar.SaveRequested || input.SavePressed;
        bool loadRequested = topBar.LoadRequested || input.LoadPressed;

        return new UiFrameActions(
            clearRequested,
            saveRequested,
            loadRequested,
            propertiesPanel.ScaleChanged,
            propertiesPanel.GravityToggled,
            propertiesPanel.HydraulicsToggled);
    }

    public void Draw(
        SpriteBatch spriteBatch,
        SimulationSettings settings,
        SimulationStatistics statistics,
        double framesPerSecond,
        string transientStatus,
        TemperatureProbeResult? temperatureProbe)
    {
        topBar.RecordFrameTime((float)(1.0 / Math.Max(1.0, framesPerSecond)));

        // 1. Top Bar
        topBar.Draw(
            spriteBatch,
            font,
            panelRenderer,
            pixel,
            currentLayout.TopBar,
            framesPerSecond,
            settings.Paused);

        // 2. Left Toolbar
        leftToolbar.Draw(
            spriteBatch,
            font,
            panelRenderer,
            pixel,
            currentLayout.LeftToolbar);

        // 3. Properties Panel
        MaterialDefinition currentMatDef = materialRegistry.TryGet(SelectedMaterial, out MaterialDefinition mat)
            ? mat
            : materialRegistry[CoreMaterialIds.Sand];

        propertiesPanel.Draw(
            spriteBatch,
            font,
            panelRenderer,
            pixel,
            currentLayout.RightPanel,
            leftToolbar.ActiveTool,
            currentMatDef);

        // 4. Bottom Category Palette
        categoryPalette.Draw(
            spriteBatch,
            font,
            panelRenderer,
            pixel,
            currentLayout.BottomPalette,
            SelectedMaterial,
            TemperatureToolActive);

        // 5. Bottom Status Bar
        statusBar.Draw(
            spriteBatch,
            font,
            panelRenderer,
            pixel,
            currentLayout.StatusBar,
            materialRegistry,
            SelectedMaterial,
            TemperatureToolActive,
            temperatureProbe,
            statistics,
            framesPerSecond,
            settings.Scale,
            settings.Paused);
    }

    public void DrawBrushIndicator(
        SpriteBatch spriteBatch,
        Point pointer,
        Rectangle worldBounds,
        SimulationSettings settings,
        bool eraseOverride)
    {
        if (!worldBounds.Contains(pointer) || PointerConsumed)
        {
            return;
        }

        float pixelScale = worldBounds.Width / (float)Math.Max(1, settings.Width);
        int diameter = Math.Max(3, (int)MathF.Round((settings.BrushRadius * 2 + 1) * pixelScale));
        Rectangle bounds = new(pointer.X - diameter / 2, pointer.Y - diameter / 2, diameter, diameter);

        bool erasing = eraseOverride ||
            (!TemperatureToolActive &&
            (MaterialSimulationKind)materialRegistry[SelectedMaterial].Properties.SimulationKind ==
                MaterialSimulationKind.Tool);

        Color color = erasing
            ? new Color(255, 96, 96, 190)
            : TemperatureToolActive
                ? new Color(255, 154, 86, 190)
                : new Color(210, 235, 255, 175);

        spriteBatch.Draw(brushOutline, bounds, color);
    }

    public void Dispose()
    {
        materialCardPreviews.Dispose();
    }
}
