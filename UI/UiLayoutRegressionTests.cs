using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Phyxel.Core;
using Phyxel.Input;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.UI;

public static class UiLayoutRegressionTests
{
    private static readonly (int Width, int Height)[] Resolutions =
    [
        (1280, 720),
        (1600, 900),
        (1920, 1080),
        (2560, 1440)
    ];

    private static readonly float[] DpiScales = [1f, 1.25f, 1.5f];

    public static void RunAllTests(
        MaterialRegistry registry,
        UiFontSet fonts,
        SandboxUiCoordinator coordinator)
    {
        Console.WriteLine("=== Running Phyxel UI Layout & Input Regression Tests ===");

        TestLayoutResolutionAndDpiMatrix();
        TestTopBarButtons(fonts);
        TestPanelControlBounds(registry, fonts);
        TestPropertiesActions(registry, fonts);
        TestToolAndMaterialPersistence(registry, fonts, coordinator);
        TestBrushToolModesAndInputBreaks(registry);
        TestCameraPanZoomAndInputIsolation();
        TestButtonVisualStates();
        TestMaterialCards(fonts.Regular);
        TestMaterialCategorization(registry);
        TestExternalCategoryValidation();
        TestMaterialPreviewMapping();
        TestPauseContinueLogic();

        Console.WriteLine("=== All UI Layout & Input Regression Tests Passed Successfully ===");
    }

    private static void TestLayoutResolutionAndDpiMatrix()
    {
        foreach ((int width, int height) in Resolutions)
        {
            foreach (float dpi in DpiScales)
            {
                Viewport viewport = new(0, 0, width, height);
                UiLayoutBounds layout = UiLayoutCalculator.Calculate(viewport, dpi);
                Rectangle window = new(0, 0, width, height);
                Rectangle[] regions =
                [
                    layout.TopBar,
                    layout.LeftToolbar,
                    layout.RightPanel,
                    layout.BottomPalette,
                    layout.StatusBar,
                    layout.SimulationCanvas
                ];

                foreach (Rectangle region in regions)
                {
                    Require(IsInside(region, window),
                        $"Region {region} escaped {width}x{height} at {dpi:0.##} DPI scale.");
                    Require(region.Width > 0 && region.Height > 0,
                        $"Region {region} has invalid size at {width}x{height}.");
                }

                for (int first = 0; first < regions.Length; first++)
                {
                    for (int second = first + 1; second < regions.Length; second++)
                    {
                        Require(!regions[first].Intersects(regions[second]),
                            $"Regions {regions[first]} and {regions[second]} overlap at {width}x{height}, DPI {dpi:0.##}.");
                    }
                }

                Require(layout.SimulationCanvas.Width >= 420 && layout.SimulationCanvas.Height >= 240,
                    $"Canvas too small at {width}x{height}, DPI {dpi:0.##}: {layout.SimulationCanvas}.");
                Require(layout.Scale is >= 0.82f and <= 1.5f,
                    $"Layout scale out of range: {layout.Scale}.");
            }
        }

        Console.WriteLine($"[PASS] Layout matrix: {Resolutions.Length} resolutions x {DpiScales.Length} DPI scales.");
    }

    private static void TestTopBarButtons(UiFontSet fonts)
    {
        foreach ((int width, int height) in Resolutions)
        {
            foreach (float dpi in DpiScales)
            {
                UiLayoutBounds layout = UiLayoutCalculator.Calculate(new Viewport(0, 0, width, height), dpi);
                SpriteFont font = fonts.Select(dpi, layout.Scale);
                UiTopBar topBar = new(font);
                bool paused = false;
                topBar.Update(Input(new Point(-1, -1)), layout.TopBar, ref paused);

                Rectangle previous = Rectangle.Empty;
                foreach (UiIconButton button in topBar.Buttons)
                {
                    Require(IsInside(button.Bounds, layout.TopBar),
                        $"Top-bar button '{button.Label}' escaped at {width}x{height}, DPI {dpi}.");
                    Require(previous == Rectangle.Empty || !previous.Intersects(button.Bounds),
                        $"Top-bar buttons overlap at {width}x{height}, DPI {dpi}.");
                    Require(font.MeasureString(button.Label).X + 44 <= button.Bounds.Width,
                        $"Top-bar button '{button.Label}' does not fit its text and icon.");
                    previous = button.Bounds;
                }
                Require(previous.Right < layout.TopBar.Right - 210,
                    $"Top-bar buttons collide with performance area at {width}x{height}, DPI {dpi}.");

                topBar.Update(
                    Input(topBar.SettingsButtonBounds.Center, leftDown: true, leftPressed: true),
                    layout.TopBar,
                    ref paused);
                Require(!topBar.SettingsEnabled && !topBar.SettingsRequested,
                    "Disabled Settings button reported an action.");

                topBar.Update(Input(topBar.SaveButtonBounds.Center, leftDown: true, leftPressed: true), layout.TopBar, ref paused);
                Require(topBar.SaveRequested, "Save button did not report an action.");
                topBar.Update(Input(topBar.LoadButtonBounds.Center, leftDown: true, leftPressed: true), layout.TopBar, ref paused);
                Require(topBar.LoadRequested, "Load button did not report an action.");
                topBar.Update(Input(topBar.PauseButtonBounds.Center, leftDown: true, leftPressed: true), layout.TopBar, ref paused);
                Require(paused, "Pause button did not toggle pause state.");
            }
        }

        Console.WriteLine("[PASS] Top-bar bounds, text fit, and honest Settings behavior.");
    }

    private static void TestPanelControlBounds(MaterialRegistry registry, UiFontSet fonts)
    {
        MaterialDefinition sand = registry[CoreMaterialIds.Sand];
        foreach ((int width, int height) in Resolutions)
        {
            foreach (float dpi in DpiScales)
            {
                UiLayoutBounds layout = UiLayoutCalculator.Calculate(new Viewport(0, 0, width, height), dpi);
                SpriteFont font = fonts.Select(dpi, layout.Scale);
                UiPropertiesPanel panel = new();
                SimulationSettings settings = new();
                panel.Update(Input(new Point(-1, -1)), layout.RightPanel, font, settings,
                    PhyxelToolId.Brush, sand, out _);
                Rectangle[] controls =
                [
                    panel.BrushSliderBounds,
                    panel.DensitySliderBounds,
                    panel.ScaleSliderBounds,
                    panel.GravityToggleBounds,
                    panel.HydraulicsToggleBounds,
                    panel.ResetButtonBounds,
                    panel.ClearButtonBounds
                ];
                foreach (Rectangle control in controls)
                {
                    Require(IsInside(control, layout.RightPanel),
                        $"Property control {control} escaped at {width}x{height}, DPI {dpi}.");
                }
                Require(!panel.GravityToggleBounds.Intersects(panel.HydraulicsToggleBounds),
                    "Property switches overlap.");
                Require(!panel.ResetButtonBounds.Intersects(panel.ClearButtonBounds),
                    "Property action buttons overlap.");
                Require(!panel.ResetButtonBounds.Intersects(panel.HydraulicsToggleBounds) &&
                        !panel.ClearButtonBounds.Intersects(panel.HydraulicsToggleBounds),
                    "Property action buttons overlap the simulation switches.");

                UiLeftToolbar toolbar = new();
                foreach (ToolDefinition tool in UiLeftToolbar.Tools)
                {
                    Rectangle toolBounds = toolbar.GetToolBounds(layout.LeftToolbar, font, tool.Id);
                    Require(IsInside(toolBounds, layout.LeftToolbar),
                        $"Tool '{tool.DisplayName}' bounds {toolBounds} escaped panel {layout.LeftToolbar} " +
                        $"at {width}x{height}, DPI {dpi}.");
                }
            }
        }
        Console.WriteLine("[PASS] Tool and property controls stay inside panels for every resolution/DPI.");
    }

    private static void TestPropertiesActions(MaterialRegistry registry, UiFontSet fonts)
    {
        UiLayoutBounds layout = UiLayoutCalculator.Calculate(new Viewport(0, 0, 1920, 1080), 1f);
        SpriteFont font = fonts.Select(1f, layout.Scale);
        UiPropertiesPanel panel = new();
        SimulationSettings settings = new();
        MaterialDefinition sand = registry[CoreMaterialIds.Sand];

        panel.Update(Input(new Point(-1, -1)), layout.RightPanel, font, settings, PhyxelToolId.Brush, sand, out _);
        Require(IsInside(panel.BrushSliderBounds, layout.RightPanel), "Brush slider escaped properties panel.");
        Require(IsInside(panel.DensitySliderBounds, layout.RightPanel), "Density slider escaped properties panel.");
        Require(IsInside(panel.ScaleSliderBounds, layout.RightPanel), "Scale slider escaped properties panel.");
        Require(IsInside(panel.ResetButtonBounds, layout.RightPanel) && IsInside(panel.ClearButtonBounds, layout.RightPanel),
            "Reset/Clear escaped properties panel.");
        Require(!panel.ResetButtonBounds.Intersects(panel.ClearButtonBounds), "Reset and Clear overlap.");

        panel.Update(
            Input(panel.ResetButtonBounds.Center, leftDown: true, leftPressed: true),
            layout.RightPanel,
            font,
            settings,
            PhyxelToolId.Brush,
            sand,
            out _);
        Require(panel.ResetRequested, "Reset click was not emitted.");
        panel.Update(Input(new Point(-1, -1)), layout.RightPanel, font, settings, PhyxelToolId.Brush, sand, out _);
        Require(!panel.ResetRequested, "Reset action persisted beyond one frame.");

        Point scaleTarget = new(panel.ScaleSliderBounds.Right - 1, panel.ScaleSliderBounds.Bottom - 10);
        panel.Update(
            Input(scaleTarget, leftDown: true, leftPressed: true),
            layout.RightPanel,
            font,
            settings,
            PhyxelToolId.Brush,
            sand,
            out _);
        Require(panel.ScaleChanged && settings.Scale == 1f, "Scale change was not emitted for a real value change.");
        panel.Update(
            Input(scaleTarget, leftReleased: true),
            layout.RightPanel,
            font,
            settings,
            PhyxelToolId.Brush,
            sand,
            out _);
        Require(!panel.ScaleChanged, "ScaleChanged did not reset on the next frame.");

        panel.Update(
            Input(panel.GravityToggleBounds.Center, leftDown: true, leftPressed: true),
            layout.RightPanel, font, settings, PhyxelToolId.Brush, sand, out _);
        Require(settings.SolidGravity && panel.GravityToggled, "Gravity switch did not change the setting.");
        panel.Update(
            Input(panel.HydraulicsToggleBounds.Center, leftDown: true, leftPressed: true),
            layout.RightPanel, font, settings, PhyxelToolId.Brush, sand, out _);
        Require(settings.HydraulicPressure && panel.HydraulicsToggled,
            "Hydraulics switch did not change the setting.");

        panel.Update(
            Input(panel.ClearButtonBounds.Center, leftDown: true, leftPressed: true),
            layout.RightPanel, font, settings, PhyxelToolId.Brush, sand, out _);
        Require(!panel.ClearRequested, "Clear skipped confirmation.");
        panel.Update(
            Input(panel.ClearButtonBounds.Center, leftDown: true, leftPressed: true),
            layout.RightPanel, font, settings, PhyxelToolId.Brush, sand, out _);
        Require(panel.ClearRequested, "Confirmed Clear did not emit an action.");

        Require(UiPropertiesPanel.ShowsDensity(PhyxelToolId.Brush), "Brush must show density.");
        Require(!UiPropertiesPanel.ShowsDensity(PhyxelToolId.Eraser), "Eraser must not show density.");
        Require(UiPropertiesPanel.ShowsTemperature(PhyxelToolId.Temperature), "Temperature tool must show target temperature.");
        Require(!UiPropertiesPanel.ShowsTemperature(PhyxelToolId.Brush), "Brush must not show target temperature.");
        Require(!UiPropertiesPanel.ShowsBrushControls(PhyxelToolId.Pan),
            "Camera must not show brush-specific controls.");

        panel.Update(Input(new Point(-1, -1)), layout.RightPanel, font, settings,
            PhyxelToolId.Pan, sand, out _);
        Require(IsInside(panel.ResetViewButtonBounds, layout.RightPanel),
            "Camera reset-view button escaped properties panel.");
        panel.Update(
            Input(panel.ResetViewButtonBounds.Center, leftDown: true, leftPressed: true),
            layout.RightPanel,
            font,
            settings,
            PhyxelToolId.Pan,
            sand,
            out _);
        Require(panel.ResetViewRequested && !panel.ResetRequested,
            "Camera reset-view action was not distinct from physics restart.");
        panel.Update(Input(new Point(-1, -1)), layout.RightPanel, font, settings,
            PhyxelToolId.Pan, sand, out _);
        Require(!panel.ResetViewRequested, "Camera reset-view action persisted beyond one frame.");

        Console.WriteLine("[PASS] Properties visibility, physics restart, camera reset, compact actions, and one-frame ScaleChanged.");
    }

    private static void TestToolAndMaterialPersistence(
        MaterialRegistry registry,
        UiFontSet fonts,
        SandboxUiCoordinator coordinator)
    {
        UiLeftToolbar toolbar = new();
        UiLayoutBounds small = UiLayoutCalculator.Calculate(new Viewport(0, 0, 1280, 720), 1f);
        Rectangle eraser = toolbar.GetToolBounds(small.LeftToolbar, fonts.Regular, PhyxelToolId.Eraser);
        toolbar.Update(Input(eraser.Center, leftDown: true, leftPressed: true), small.LeftToolbar, fonts.Regular, out _);
        Require(toolbar.ActiveTool == PhyxelToolId.Eraser, "Eraser did not become active.");

        UiLayoutBounds large = UiLayoutCalculator.Calculate(new Viewport(0, 0, 2560, 1440), 1.5f);
        toolbar.Update(Input(new Point(-1, -1)), large.LeftToolbar, fonts.Large, out _);
        Require(toolbar.ActiveTool == PhyxelToolId.Eraser, "Tool selection was lost after resize.");
        Rectangle camera = toolbar.GetToolBounds(large.LeftToolbar, fonts.Large, PhyxelToolId.Pan);
        toolbar.Update(Input(camera.Center, leftDown: true, leftPressed: true), large.LeftToolbar, fonts.Large, out _);
        Require(toolbar.ActiveTool == PhyxelToolId.Pan, "Camera tool did not become active.");
        toolbar.Update(Input(new Point(-1, -1)), small.LeftToolbar, fonts.Regular, out _);
        Require(toolbar.ActiveTool == PhyxelToolId.Pan, "Camera selection was lost after resize.");

        ushort previousMaterial = coordinator.SelectedMaterial;
        PhyxelToolId previousTool = coordinator.ActiveTool;
        ushort water = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        coordinator.SelectedMaterial = water;
        coordinator.ActiveTool = PhyxelToolId.Temperature;
        SimulationSettings settings = new();
        coordinator.Update(Input(new Point(-1, -1)), new Viewport(0, 0, 1280, 720), 1f, settings);
        coordinator.Update(Input(new Point(-1, -1)), new Viewport(0, 0, 2560, 1440), 1.5f, settings);
        Require(coordinator.SelectedMaterial == water && coordinator.ActiveTool == PhyxelToolId.Temperature,
            "Selected material or tool was lost after coordinator resize.");
        coordinator.SelectedMaterial = previousMaterial;
        coordinator.ActiveTool = previousTool;

        Console.WriteLine("[PASS] Tool/material persistence and enabled Camera behavior.");
    }

    private static void TestBrushToolModesAndInputBreaks(MaterialRegistry registry)
    {
        SimulationSettings settings = new();
        Rectangle canvas = new(100, 50, 960, 540);
        Point center = canvas.Center;
        ushort sand = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Sand);
        ushort eraser = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Eraser);

        CanvasBrushController brush = new();
        IReadOnlyList<BrushDrawCommand> material = brush.CreateCommands(
            Input(center, leftDown: true, leftPressed: true), canvas, settings, sand, false, false, 20, false);
        Require(material.Count == 1 && material[0].Mode == BrushCommandMode.Material,
            "Brush did not emit a material command.");

        CanvasBrushController erase = new();
        IReadOnlyList<BrushDrawCommand> erased = erase.CreateCommands(
            Input(center, leftDown: true, leftPressed: true), canvas, settings, eraser, true, false, 20, false);
        Require(erased.Count == 1 && erased[0].Mode == BrushCommandMode.Erase,
            "Eraser did not emit an erase command.");

        CanvasBrushController temperature = new();
        IReadOnlyList<BrushDrawCommand> heated = temperature.CreateCommands(
            Input(center, leftDown: true, leftPressed: true), canvas, settings, sand, false, true, 750, false);
        Require(heated.Count == 1 && heated[0].Mode == BrushCommandMode.SetTemperature &&
                Math.Abs(heated[0].TargetTemperature - 750) < 0.01f,
            "Temperature tool did not emit the requested temperature.");

        CanvasBrushController uiBlocked = new();
        Require(uiBlocked.CreateCommands(
            Input(center, leftDown: true, leftPressed: true), canvas, settings, sand, false, false, 20, true).Count == 0,
            "UI-consumed click produced a world command.");

        CanvasBrushController reentry = new();
        reentry.CreateCommands(Input(new Point(canvas.X + 10, canvas.Y + 10), leftDown: true),
            canvas, settings, sand, false, false, 20, false);
        Require(reentry.CreateCommands(Input(new Point(canvas.Right + 20, canvas.Bottom + 20), leftDown: true),
            canvas, settings, sand, false, false, 20, false).Count == 0,
            "Pointer outside canvas produced a command.");
        IReadOnlyList<BrushDrawCommand> reentered = reentry.CreateCommands(
            Input(new Point(canvas.Right - 10, canvas.Bottom - 10), leftDown: true),
            canvas, settings, sand, false, false, 20, false);
        Require(reentered.Count == 1 && reentered[0].X == reentered[0].EndX && reentered[0].Y == reentered[0].EndY,
            "Canvas re-entry created a long connecting stroke.");

        CanvasBrushController resized = new();
        Rectangle resizedCanvas = new(20, 30, 640, 360);
        Point quarter = new(resizedCanvas.X + resizedCanvas.Width / 4, resizedCanvas.Y + resizedCanvas.Height / 4);
        IReadOnlyList<BrushDrawCommand> mapped = resized.CreateCommands(
            Input(quarter, leftDown: true, leftPressed: true), resizedCanvas, settings, sand, false, false, 20, false);
        Require(mapped.Count == 1 && Math.Abs(mapped[0].X - settings.Width / 4) <= 1 &&
                Math.Abs(mapped[0].Y - settings.Height / 4) <= 1,
            "Mouse-to-world mapping broke after canvas resize.");

        Console.WriteLine("[PASS] Brush, Eraser, Temperature, UI blocking, re-entry, and resize mapping.");
    }

    private static void TestCameraPanZoomAndInputIsolation()
    {
        CanvasCameraController camera = new();
        Rectangle canvas = new(100, 80, 800, 450);
        Rectangle fittedWorld = canvas;
        Point center = canvas.Center;

        Rectangle zoomed = camera.Update(
            Input(center, wheelDelta: 120), canvas, fittedWorld, true, false);
        Require(Math.Abs(camera.Zoom - 1.25f) < 0.001f && zoomed.Width == 1000,
            "Camera wheel zoom did not update the visible world transform.");

        camera.Update(Input(center, leftDown: true, leftPressed: true), canvas, fittedWorld, true, false);
        Rectangle panned = camera.Update(
            Input(new Point(center.X + 100, center.Y), leftDown: true),
            canvas,
            fittedWorld,
            true,
            false);
        Require(panned.X > zoomed.X, "Camera drag did not pan the world.");

        camera.Update(
            Input(new Point(canvas.Right + 50, canvas.Bottom + 50), leftDown: true),
            canvas,
            fittedWorld,
            true,
            false);
        Rectangle reentered = camera.Update(
            Input(new Point(canvas.X + 20, canvas.Y + 20), leftDown: true),
            canvas,
            fittedWorld,
            true,
            false);
        Rectangle next = camera.Update(
            Input(new Point(canvas.X + 21, canvas.Y + 20), leftDown: true),
            canvas,
            fittedWorld,
            true,
            false);
        Require(Math.Abs(next.X - reentered.X) <= 2,
            "Camera re-entry created a long accidental pan.");

        Rectangle resizedCanvas = new(20, 30, 1280, 720);
        Rectangle resizedView = camera.Update(
            Input(new Point(-1, -1)), resizedCanvas, resizedCanvas, true, false);
        Require(resizedView.Width == 1600 && resizedView.Height == 900,
            "Camera transform did not preserve zoom after resize.");

        camera.Reset();
        Require(camera.Zoom == 1f && camera.GetWorldBounds(resizedCanvas) == resizedCanvas,
            "Camera reset did not restore the fitted view.");
        CanvasBrushController brush = new();
        Require(brush.CreateCommands(
                Input(resizedCanvas.Center, leftDown: true, leftPressed: true),
                resizedCanvas,
                new SimulationSettings(),
                0,
                false,
                false,
                20,
                true).Count == 0,
            "Camera-owned drag leaked into brush commands.");
        Console.WriteLine("[PASS] Camera pan, zoom, re-entry safety, resize, reset, and brush isolation.");
    }

    private static void TestButtonVisualStates()
    {
        UiIconButton button = new("Test") { Bounds = new Rectangle(10, 10, 100, 40) };
        button.Update(Input(new Point(20, 20)));
        Require(button.VisualState == UiButtonVisualState.Hover, "Button hover state failed.");
        button.Update(Input(new Point(20, 20), leftDown: true));
        Require(button.VisualState == UiButtonVisualState.Pressed, "Button pressed state failed.");
        button.Active = true;
        button.Update(Input(new Point(-1, -1)));
        Require(button.VisualState == UiButtonVisualState.Active, "Button active state failed.");
        button.Enabled = false;
        Require(!button.Update(Input(new Point(20, 20), leftDown: true, leftPressed: true)) &&
                button.VisualState == UiButtonVisualState.Disabled,
            "Button disabled state failed.");
        Console.WriteLine("[PASS] Button hover, pressed, active, and disabled states.");
    }

    private static void TestMaterialCards(SpriteFont font)
    {
        const string longName = "Очень длинное название внешнего материала";
        string truncated = UiCategoryPalette.TruncateToWidth(font, longName, 120);
        Require(font.MeasureString(truncated).X <= 120 && truncated.EndsWith('…'),
            "Long material title escaped its card width.");

        Rectangle destination = new(0, 0, 132, 80);
        Rectangle source = UiCategoryPalette.CalculateAspectFillSource(450, 335, destination);
        Require(source.X >= 0 && source.Y >= 0 && source.Right <= 450 && source.Bottom <= 335,
            "Aspect-fill crop escaped preview texture.");
        Require(Math.Abs(source.Width / (float)source.Height - destination.Width / (float)destination.Height) < 0.02f,
            "Aspect-fill crop distorted preview proportions.");

        (string wrapped, float titleScale) = UiCategoryPalette.FitCardTitle(font, "Древесный уголь", 120);
        Require(wrapped.Contains('\n') && !wrapped.Contains('…') && titleScale is >= 0.72f and <= 1f,
            "Two-word material title did not use a readable two-line layout.");
        foreach (string line in wrapped.Split('\n'))
        {
            Require(font.MeasureString(line).X * titleScale <= 120.5f,
                "Wrapped material title escaped its card width.");
        }

        UiLayoutBounds layout = UiLayoutCalculator.Calculate(new Viewport(0, 0, 1920, 1080), 1f);
        int capacity = UiCategoryPalette.CalculateVisibleCardCapacity(layout.BottomPalette, font);
        Require(capacity is >= 8 and <= 11,
            $"1920px material palette capacity must stay within 8-11 cards, got {capacity}.");
        Console.WriteLine($"[PASS] Material titles, aspect-fill crop, and 1920px capacity ({capacity} cards).");
    }

    private static void TestMaterialCategorization(MaterialRegistry registry)
    {
        int categorizedCount = 0;
        foreach (MaterialDefinition material in registry.SelectableMaterials)
        {
            Require(!material.Hidden, $"Hidden material '{material.Id}' included in selectable materials.");
            _ = MaterialCategoryResolver.Resolve(material);
            categorizedCount++;
        }
        Require(categorizedCount == registry.SelectableMaterials.Count, "Material category count mismatch.");
        Console.WriteLine($"[PASS] Material categorization for {categorizedCount} materials.");
    }

    private static void TestExternalCategoryValidation()
    {
        MaterialProperties properties = default;
        MaterialDefinition externalValid = new(
            "ext:custom_sand", 100, "Custom Sand", Color.Yellow, properties, 0, false, "powders");
        Require(MaterialCategoryResolver.Resolve(externalValid) == MaterialCategoryType.Powders,
            "Valid external category was rejected.");

        MaterialDefinition externalUnknown = new(
            "ext:unknown", 101, "Unknown", Color.Green, properties, 0, false, "порошок");
        Require(!MaterialCategoryResolver.TryResolveExplicitCategory(externalUnknown.Category, out _) &&
                MaterialCategoryResolver.Resolve(externalUnknown) == MaterialCategoryType.Solids,
            "Unknown external category did not fall back to kind.");
        Require(!MaterialCategoryResolver.TryResolveExplicitCategory("powder", out _),
            "Non-contract category alias was accepted.");
        Console.WriteLine("[PASS] Strict ui.category contract and external fallback.");
    }

    private static void TestPauseContinueLogic()
    {
        SimulationSettings settings = new();
        bool initial = settings.Paused;
        settings.Paused = !initial;
        Require(settings.Paused != initial, "Pause state toggle failed.");
        Console.WriteLine("[PASS] Pause / Continue toggle.");
    }

    private static void TestMaterialPreviewMapping()
    {
        Dictionary<string, string> expected = new(StringComparer.OrdinalIgnoreCase)
        {
            [CoreMaterialIds.Sand] = "sand.png",
            [CoreMaterialIds.Water] = "water.png",
            [CoreMaterialIds.Steam] = "steam.png",
            [CoreMaterialIds.Co2] = "co2.png",
            [CoreMaterialIds.Ice] = "ice.png",
            [CoreMaterialIds.Metal] = "metal.png",
            [CoreMaterialIds.Stone] = "stone.png",
            [CoreMaterialIds.Wood] = "wood.png",
            [CoreMaterialIds.Fire] = "fire.png",
            [CoreMaterialIds.Coal] = "charcoal.png",
            [CoreMaterialIds.StoneCoal] = "stone_coal.png"
        };
        foreach ((string materialId, string fileName) in expected)
        {
            Require(string.Equals(MaterialCardPreviewCache.GetPreviewFileName(materialId), fileName,
                    StringComparison.OrdinalIgnoreCase),
                $"Preview mapping mismatch for '{materialId}'.");
        }
        Require(MaterialCardPreviewCache.GetPreviewFileName("external:custom") is null,
            "External material unexpectedly received a core preview.");
        Console.WriteLine("[PASS] Material preview mapping and external fallback.");
    }

    private static RawInputSnapshot Input(
        Point point,
        bool leftDown = false,
        bool leftPressed = false,
        bool leftReleased = false,
        int wheelDelta = 0) =>
        new(
            point,
            wheelDelta,
            leftDown,
            false,
            leftPressed,
            leftReleased,
            false,
            false,
            false,
            false,
            false,
            false,
            1f / 60f);

    private static bool IsInside(Rectangle child, Rectangle parent) =>
        child.Left >= parent.Left && child.Top >= parent.Top &&
        child.Right <= parent.Right && child.Bottom <= parent.Bottom;

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
