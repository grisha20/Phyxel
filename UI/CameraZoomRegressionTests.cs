using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Input;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.UI;

internal static class CameraZoomRegressionTests
{
    private static RawInputSnapshot Input(Point pointer) => default(RawInputSnapshot) with
        { MousePosition = pointer, DeltaSeconds = 1f / 60f };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Camera zoom: " + message);
    }

    internal static Rectangle Settle(CanvasCameraController camera, Rectangle canvas, Rectangle fitted)
    {
        for (int i = 0; camera.IsZooming && i < 120; i++)
            camera.Update(Input(new Point(-1, -1)), canvas, fitted, false, false);
        Check(!camera.IsZooming, "zoom never reached its target");
        return camera.GetWorldBounds(fitted);
    }

    public static void Run(MaterialRegistry registry, SandboxUiCoordinator ui)
    {
        Rectangle canvas = new(100, 80, 800, 450);
        Point pointer = canvas.Center + new Point(70, 30);
        var camera = new CanvasCameraController();
        Rectangle view = camera.Update(Input(pointer), canvas, canvas, false, false);
        foreach (int wheel in new[] { 120, 240, -120, -240, -120, -120 })
        {
            Vector2 cellBefore = new((pointer.X - view.X) / (float)view.Width,
                (pointer.Y - view.Y) / (float)view.Height);
            view = camera.Update(Input(pointer) with { WheelDelta = wheel }, canvas, canvas, false, false);
            for (int i = 0; camera.IsZooming && i < 120; i++)
            {
                Vector2 anchored = new(view.X + cellBefore.X * view.Width, view.Y + cellBefore.Y * view.Height);
                Check(Vector2.Distance(anchored, pointer.ToVector2()) <= 1.5f, "animated zoom drifted away from its anchor");
                view = camera.Update(Input(pointer), canvas, canvas, false, false);
            }
            Check(!camera.IsZooming, "anchored zoom never settled");
            Vector2 screenAfter = new(view.X + cellBefore.X * view.Width, view.Y + cellBefore.Y * view.Height);
            Check(Vector2.Distance(screenAfter, pointer.ToVector2()) <= 1.5f,
                "wheel lost the point under the pointer at zoom " + camera.Zoom);
        }
        Check(camera.Zoom < 1 && canvas.Contains(view), "zoom-out did not retain the whole world inside the canvas");
        float zoom = camera.Zoom;
        foreach (var blocked in new[]
        {
            Input(pointer) with { WheelDelta = 120, ShiftDown = true },
            Input(new Point(-1, -1)) with { WheelDelta = 120 }
        }) camera.Update(blocked, canvas, canvas, false, false);
        camera.Update(Input(pointer) with { WheelDelta = 120 }, canvas, canvas, false, true);
        Check(camera.Zoom == zoom, "Shift, outside pointer or UI consumption changed zoom");

        camera.Reset();
        camera.Update(Input(canvas.Center) with { WheelDelta = 240 }, canvas, canvas, false, false);
        Settle(camera, canvas, canvas);
        Check(Math.Abs(camera.Zoom - 1.5625f) < .001f, "multiple wheel notches were discarded");
        camera.Reset();
        camera.Update(Input(canvas.Center) with { WheelDelta = 60 }, canvas, canvas, false, false);
        camera.Update(Input(canvas.Center) with { WheelDelta = 60 }, canvas, canvas, false, false);
        Settle(camera, canvas, canvas);
        Check(Math.Abs(camera.Zoom - 1.25f) < .001f, "high-resolution wheel deltas were discarded");
        camera.Update(Input(canvas.Center) with { WheelDelta = 12000 }, canvas, canvas, false, false);
        Settle(camera, canvas, canvas);
        Check(camera.Zoom == 16, "maximum zoom missing");
        camera.Update(Input(canvas.Center) with { WheelDelta = -12000 }, canvas, canvas, false, false);
        Settle(camera, canvas, canvas);
        Check(camera.Zoom == .25f, "minimum zoom missing");
        camera.Reset();
        Check(camera.Zoom == 1 && camera.GetWorldBounds(canvas) == canvas, "reset is not the original view");

        view = camera.Update(Input(canvas.Center) with { WheelDelta = 480 }, canvas, canvas, false, false);
        view = Settle(camera, canvas, canvas);
        camera.Update(Input(canvas.Center) with { LeftDown = true }, canvas, canvas, false, false);
        Rectangle noPan = camera.Update(Input(pointer) with { LeftDown = true }, canvas, canvas, false, false);
        Check(noPan == view, "ordinary painting moved the camera");
        camera.Update(Input(canvas.Center) with { MiddleDown = true }, canvas, canvas, false, false);
        view = camera.Update(Input(canvas.Center + new Point(50, 20)) with { MiddleDown = true }, canvas, canvas, false, false);
        Check(view.X == noPan.X + 50 && view.Y == noPan.Y + 20, "middle-button pan requires the camera tool");
        Rectangle blockedPan = camera.Update(Input(pointer) with { MiddleDown = true }, canvas, canvas, false, true);
        Check(blockedPan == view, "UI-owned middle drag moved the camera");
        Rectangle reentered = camera.Update(Input(pointer) with { MiddleDown = true }, canvas, canvas, false, false);
        Check(reentered == view, "re-entering canvas jumped during middle drag");

        ushort metal = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal);
        var settings = new SimulationSettings { Width = 480, Height = 270, BrushRadius = 3 };
        var brush = new CanvasBrushController();
        var click = Input(pointer) with { LeftDown = true, LeftPressed = true };
        var commands = brush.CreateCommands(click, view, settings, metal, false, false, 20, false);
        Point? probe = GpuTemperatureProbe.MapPointerToCell(pointer, view, settings.Width, settings.Height);
        Check(commands.Count == 1 && probe is { } p && commands[0].X == p.X && commands[0].Y == p.Y,
            "zoomed brush and temperature probe disagree");
        Check(TemperatureSensorOverlay.Edit(click, view, settings) &&
            settings.TemperatureSensors[0].X == probe!.Value.X && settings.TemperatureSensors[0].Y == probe.Value.Y,
            "zoomed sensor and temperature probe disagree");

        foreach (bool pendingLine in new[] { false, true })
        {
            brush = new CanvasBrushController();
            if (pendingLine) brush.CreateCommands(click with { ShiftDown = true }, view, settings, metal, false, false, 20, false);
            brush.CancelStroke(suppressDrawingUntilRelease: true);
            Check(brush.LinePreview is null && brush.CreateCommands(click, view, settings, metal, false, false, 20, false).Count == 0,
                "navigation leaked a held button or a pending line");
            brush.CreateCommands(Input(pointer) with { LeftReleased = true }, view, settings, metal, false, false, 20, false);
            Check(brush.CreateCommands(click, view, settings, metal, false, false, 20, false).Count == 1,
                "navigation swallowed a fresh brush press");
        }
        brush = new CanvasBrushController();
        brush.CreateCommands(click, view, settings, metal, false, false, 20, false);
        Rectangle changed = camera.Update(Input(pointer) with { WheelDelta = 120 }, canvas, canvas, false, false);
        Check(brush.CreateCommands(click, changed, settings, metal, false, false, 20, false).Count == 0,
            "zoom during a held stroke drew across the world");
        camera.Reset();
        Rectangle small = camera.Update(Input(canvas.Center) with { WheelDelta = -600 }, canvas, canvas, false, false);
        small = Settle(camera, canvas, canvas);
        Check(new CanvasBrushController().CreateCommands(Input(canvas.Location) with { LeftDown = true },
            small, settings, metal, false, false, 20, false).Count == 0, "empty margin painted into the world");

        // Exercise the UI wheel route across layout/DPI changes, not just camera arithmetic.
        foreach (var size in new[] { new Point(1280, 720), new Point(1920, 1080), new Point(2560, 1440) })
        foreach (float dpi in new[] { 1f, 1.25f, 1.5f })
        {
            var viewport = new Viewport(0, 0, size.X, size.Y);
            settings = new SimulationSettings();
            float scale = settings.Scale; int width = settings.Width, height = settings.Height;
            ui.ActiveTool = PhyxelToolId.Brush; ui.SelectedMaterial = metal;
            ui.Update(Input(new Point(-1, -1)), viewport, dpi, settings);
            int radius = settings.BrushRadius;
            Rectangle fitted = CanvasWorldExpansion.CoverBounds(ui.CanvasBounds, width, height);
            camera.Reset();
            var wheel = Input(ui.CanvasBounds.Center) with { WheelDelta = 120 };
            ui.Update(wheel, viewport, dpi, settings);
            camera.Update(wheel, ui.CanvasBounds, fitted, ui.PanToolActive, ui.PointerConsumed);
            Settle(camera, ui.CanvasBounds, fitted);
            Check(camera.Zoom == 1.25f && settings.BrushRadius == radius && ui.SelectedMaterial == metal &&
                settings.Width == width && settings.Height == height && settings.Scale == scale,
                "plain wheel changed brush/material/grid instead of the view");
            ui.Update(wheel with { ShiftDown = true }, viewport, dpi, settings);
            camera.Update(wheel with { ShiftDown = true }, ui.CanvasBounds, fitted, ui.PanToolActive, ui.PointerConsumed);
            Check(camera.Zoom == 1.25f && settings.BrushRadius == radius + 2, "Shift+wheel did not exclusively resize the brush");
            ui.Update(Input(Point.Zero) with { EscapePressed = true }, viewport, dpi, settings);
            ui.Update(wheel, viewport, dpi, settings);
            camera.Update(wheel, ui.CanvasBounds, fitted, ui.PanToolActive, ui.PointerConsumed);
            Check(camera.Zoom == 1.25f, "pause menu leaked wheel into camera");
            ui.Update(Input(Point.Zero) with { EscapePressed = true }, viewport, dpi, settings);
            ui.Update(Input(Point.Zero), viewport, dpi, settings);
        }
        TestSmoothZoomAndFreePan(canvas);
        Console.WriteLine("[PASS] Smooth global zoom, 0.25-16x limits, frame-rate independence, pointer anchoring, free middle pan, UI/modal isolation, brush/probe/sensor mapping and stroke cancellation.");
    }

    private static void TestSmoothZoomAndFreePan(Rectangle canvas)
    {
        var camera = new CanvasCameraController();
        var wheel = Input(canvas.Center) with { WheelDelta = 120 };
        camera.Update(wheel, canvas, canvas, false, false);
        Check(camera.Zoom > 1 && camera.Zoom < 1.25f && camera.IsZooming, "wheel jumped instantly to target");
        float previous = camera.Zoom;
        for (int i = 0; i < 60; i++)
        {
            camera.Update(Input(canvas.Center), canvas, canvas, false, false);
            Check(camera.Zoom >= previous && camera.Zoom <= 1.25f, "zoom overshot or reversed while settling");
            previous = camera.Zoom;
        }
        Check(!camera.IsZooming && camera.Zoom == 1.25f, "smooth zoom did not finish");
        float AtTime(float dt, int frames)
        {
            var test = new CanvasCameraController();
            for (int i = 0; i < frames; i++)
                test.Update(wheel with { WheelDelta = i == 0 ? 120 : 0, DeltaSeconds = dt }, canvas, canvas, false, false);
            return test.Zoom;
        }
        Check(Math.Abs(AtTime(.01f, 10) - AtTime(.05f, 2)) < .00001f, "zoom speed depends on rendered FPS");
        camera.Reset();
        camera.Update(Input(canvas.Center) with { MiddleDown = true }, canvas, canvas, false, false);
        Rectangle shifted = camera.Update(Input(canvas.Center + new Point(120, 80)) with { MiddleDown = true }, canvas, canvas, false, false);
        Check(shifted.Location == canvas.Location + new Point(120, 80), "middle pan is locked at the original view");
        camera.Reset();
        var zooming = Input(canvas.Center) with { WheelDelta = 480, MiddleDown = true };
        camera.Update(zooming, canvas, canvas, false, false);
        var moved = zooming with { WheelDelta = 0, MousePosition = canvas.Center + new Point(50, 20) };
        camera.Update(moved, canvas, canvas, false, false);
        Rectangle settled = Settle(camera, canvas, canvas);
        Check(Math.Abs(settled.Center.X - moved.MousePosition.X) <= 1 &&
            Math.Abs(settled.Center.Y - moved.MousePosition.Y) <= 1, "zoom settling undid a simultaneous middle pan");
    }
}
