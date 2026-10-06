using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Phyxel.Core;
using Phyxel.Input;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.UI;

internal static class UiEditorInputRegressionTests
{
    public static void Run(MaterialRegistry registry, UiFontSet fonts, SandboxUiCoordinator ui)
    {
        TestLines(registry);
        TestMenu(fonts, ui);
        Console.WriteLine("[PASS] Esc menu, modal input isolation, exit confirmation, Shift line preview/commit/cancel.");
    }

    private static RawInputSnapshot Input(Point point) => default(RawInputSnapshot) with
        { MousePosition = point, DeltaSeconds = 1f / 60f };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void TestLines(MaterialRegistry registry)
    {
        ushort metal = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal);
        var settings = new SimulationSettings { Width = 480, Height = 270, BrushRadius = 5 };
        Rectangle canvas = new(100, 60, 960, 540);
        Point origin = canvas.Center;
        foreach (Point offset in new[] { new Point(240, 120), new Point(-240, 120),
            new Point(240, -120), new Point(-240, -120), new Point(240, 0), new Point(0, -120), Point.Zero })
        {
            var brush = new CanvasBrushController();
            var press = Input(origin) with { ShiftDown = true, LeftDown = true, LeftPressed = true };
            Check(brush.CreateCommands(press, canvas, settings, metal, false, false, 20, false).Count == 0,
                "Shift press painted before release.");
            Point end = origin + offset;
            Check(brush.CreateCommands(Input(end) with { LeftDown = true }, canvas, settings,
                metal, false, false, 20, false).Count == 0, "Line drag painted before release.");
            var preview = brush.LinePreview!.Value;
            Check(preview.EndX - preview.X == offset.X / 2 && preview.EndY - preview.Y == offset.Y / 2,
                "Line was snapped or mapped incorrectly.");
            // Captured tool and thickness survive modifier release and wheel/property changes.
            settings.BrushRadius = 11;
            var commands = brush.CreateCommands(Input(end) with { LeftReleased = true }, canvas,
                settings, metal, false, true, 900, false);
            Check(commands.Count == 1 && commands[0].Mode == BrushCommandMode.Material && commands[0].Radius == 5,
                "Line lost its captured brush or was not committed once.");
            Check(commands[0].EndX == preview.EndX && commands[0].EndY == preview.EndY && brush.LinePreview is null,
                "Committed line differs from preview.");
            Check(brush.CreateCommands(Input(end), canvas, settings, metal, false, false, 20, false).Count == 0,
                "Line committed more than once.");
            Vector2 screen = SandboxUiCoordinator.GridToScreen(preview.EndX, preview.EndY, canvas, settings);
            Check(Math.Abs(screen.X - end.X) <= 1 && Math.Abs(screen.Y - end.Y) <= 1,
                "Preview does not match grid-cell coordinates.");
            settings.BrushRadius = 5;
        }

        foreach (string cancel in new[] { "ui", "outside", "resize", "escape" })
        {
            var brush = new CanvasBrushController();
            brush.CreateCommands(Input(origin) with { ShiftDown = true, LeftDown = true, LeftPressed = true },
                canvas, settings, metal, false, false, 20, false);
            Rectangle changed = cancel == "resize" ? new Rectangle(101, 60, 960, 540) : canvas;
            Point end = cancel == "outside" ? new Point(-1, -1) : origin;
            if (cancel == "escape") brush.CancelStroke();
            Check(brush.CreateCommands(Input(end) with { LeftDown = true }, changed, settings,
                metal, false, false, 20, cancel == "ui").Count == 0 && brush.LinePreview is null,
                "Cancelled line survived " + cancel);
            Check(brush.CreateCommands(Input(origin) with { LeftReleased = true }, changed, settings,
                metal, false, false, 20, false).Count == 0, "Cancelled line committed on release.");
        }

        foreach (bool erase in new[] { false, true })
        {
            var brush = new CanvasBrushController();
            var press = Input(origin) with { ShiftDown = true, LeftDown = !erase, LeftPressed = !erase,
                RightDown = erase, RightPressed = erase };
            brush.CreateCommands(press, canvas, settings, metal, false, false, 20, false,
                filterTool: true, filterRule: 123);
            var release = Input(origin + new Point(100, 50)) with { LeftReleased = !erase, RightReleased = erase };
            var commands = brush.CreateCommands(release, canvas, settings, metal, false, false, 20, false);
            Check(commands.Count == 1 && commands[0].Mode == (erase ? BrushCommandMode.Erase : BrushCommandMode.Filter),
                "Shift filter / right erase emitted wrong mode.");
            Check(erase || commands[0].Reserved == 123, "Line lost captured filter rule.");
        }
        var outsideRelease = new CanvasBrushController();
        var newPress = Input(origin) with { ShiftDown = true, LeftDown = true, LeftPressed = true };
        outsideRelease.CreateCommands(newPress, canvas, settings, metal, false, false, 20, false);
        outsideRelease.CreateCommands(Input(new Point(-1, -1)) with { LeftReleased = true },
            canvas, settings, metal, false, false, 20, false);
        outsideRelease.CreateCommands(newPress, canvas, settings, metal, false, false, 20, false);
        Check(outsideRelease.LinePreview is not null, "Release outside canvas swallowed the next fresh press.");
    }

    private static void TestMenu(UiFontSet fonts, SandboxUiCoordinator ui)
    {
        foreach (var size in new[] { new Point(1280, 720), new Point(1920, 1080), new Point(2560, 1440) })
        foreach (float dpi in new[] { 1f, 1.25f, 1.5f })
        foreach (bool paused in new[] { false, true })
        {
            var settings = new SimulationSettings { Paused = paused };
            var view = new Viewport(0, 0, size.X, size.Y);
            ui.Update(Input(new Point(-1, -1)), view, dpi, settings);
            var opened = ui.Update(Input(ui.CanvasBounds.Center) with { EscapePressed = true }, view, dpi, settings);
            Check(ui.PauseMenuOpen && ui.BlocksBrushInput && !opened.ExitRequested && settings.Paused == paused,
                "Escape exited, failed to block input, or changed underlying pause state.");
            Check(view.Bounds.Contains(ui.PauseMenu.PanelBounds), "Pause menu lies outside viewport.");
            int radius = settings.BrushRadius;
            ui.Update(Input(ui.CanvasBounds.Center) with { WheelDelta = 120, RightDown = true }, view, dpi, settings);
            Check(settings.BrushRadius == radius && ui.PointerConsumed, "Modal allowed background controls.");
            var save = ui.Update(Input(ui.PauseMenu.SaveBounds.Center) with { LeftDown = true, LeftPressed = true },
                view, dpi, settings);
            Check(save.SaveRequested && save.SaveAsRequested && ui.PauseMenuOpen, "Menu save action failed.");
            var load = ui.Update(Input(ui.PauseMenu.LoadBounds.Center) with { LeftDown = true, LeftPressed = true },
                view, dpi, settings);
            Check(load.LoadRequested && !load.SaveRequested, "Menu load action failed.");
            var busy = ui.Update(Input(ui.PauseMenu.SaveBounds.Center) with { LeftPressed = true }, view, dpi,
                settings, fileOperationPending: true);
            Check(!busy.SaveRequested, "Menu permitted overlapping file operations.");
            var exit = ui.Update(Input(ui.PauseMenu.ExitBounds.Center) with { LeftPressed = true }, view, dpi, settings);
            Check(ui.PauseMenu.ConfirmingExit && !exit.ExitRequested, "Exit did not ask before losing scene.");
            var confirm = ui.Update(Input(ui.PauseMenu.ConfirmExitBounds.Center) with { LeftPressed = true },
                view, dpi, settings);
            Check(confirm.ExitRequested, "Explicit exit confirmation failed.");
            ui.Update(Input(Point.Zero) with { EscapePressed = true }, view, dpi, settings);
            Check(ui.PauseMenuOpen && !ui.PauseMenu.ConfirmingExit, "Escape did not cancel exit confirmation.");
            var closed = ui.Update(Input(Point.Zero) with { EscapePressed = true }, view, dpi, settings);
            Check(!ui.PauseMenuOpen && !closed.ExitRequested && ui.BlocksBrushInput && settings.Paused == paused,
                "Escape close resumed an already paused world or leaked input.");
            ui.Update(Input(Point.Zero), view, dpi, settings);
        }
        // Continue is a press action: a held mouse must not paint after closing.
        var state = new SimulationSettings(); var viewport = new Viewport(0, 0, 1920, 1080);
        ui.Update(Input(Point.Zero) with { EscapePressed = true }, viewport, 1, state);
        ui.Update(Input(Point.Zero), viewport, 1, state);
        ui.Update(Input(ui.PauseMenu.ResumeBounds.Center) with { LeftDown = true, LeftPressed = true }, viewport, 1, state);
        Check(!ui.PauseMenuOpen && ui.BlocksBrushInput, "Continue leaked its press to canvas.");
        ui.Update(Input(ui.CanvasBounds.Center) with { LeftDown = true }, viewport, 1, state);
        Check(ui.BlocksBrushInput, "Continue leaked a held mouse to canvas.");
        ui.Update(Input(Point.Zero), viewport, 1, state);
        ui.Update(Input(ui.CanvasBounds.Center), viewport, 1, state);
        Check(!ui.BlocksBrushInput, "Menu failed to release canvas input.");
    }
}
