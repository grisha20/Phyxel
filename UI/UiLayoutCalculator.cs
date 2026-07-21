using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Phyxel.UI;

public readonly record struct UiLayoutBounds(
    Rectangle TopBar,
    Rectangle LeftToolbar,
    Rectangle RightPanel,
    Rectangle BottomPalette,
    Rectangle StatusBar,
    Rectangle SimulationCanvas,
    bool IsCompact,
    float Scale,
    float DpiScale);

public static class UiLayoutCalculator
{
    public static UiLayoutBounds Calculate(Viewport viewport, float dpiScale = 1f)
    {
        int width = viewport.Width;
        int height = viewport.Height;

        float resolutionScale = Math.Min(width / 1920f, height / 1080f);
        float readableDpiScale = Math.Min(
            Math.Clamp(dpiScale, 1f, 2f),
            Math.Max(0.84f, resolutionScale * 1.25f));
        float scale = Math.Clamp(Math.Max(resolutionScale, readableDpiScale), 0.82f, 1.4f);
        bool isCompact = width < 1450 || height < 800;

        int margin = Math.Max(6, (int)MathF.Round(8 * scale));
        int topBarHeight = Math.Max(44, (int)MathF.Round(52 * scale));
        int statusBarHeight = Math.Max(30, (int)MathF.Round(32 * scale));
        int leftToolbarWidth = Math.Max(180, (int)MathF.Round(220 * scale));
        int rightPanelWidth = Math.Max(250, (int)MathF.Round(300 * scale));
        int bottomPaletteHeight = Math.Max(118, (int)MathF.Round(145 * scale));

        Rectangle topBar = new(0, 0, width, topBarHeight);
        Rectangle statusBar = new(0, height - statusBarHeight, width, statusBarHeight);

        int middleTop = topBar.Bottom + margin;
        int middleBottom = statusBar.Top - margin;
        int middleHeight = Math.Max(100, middleBottom - middleTop);

        Rectangle bottomPalette = new(
            leftToolbarWidth + margin * 2,
            middleBottom - bottomPaletteHeight,
            width - leftToolbarWidth - rightPanelWidth - margin * 4,
            bottomPaletteHeight);

        Rectangle leftToolbar = new(
            margin,
            middleTop,
            leftToolbarWidth,
            Math.Max(100, bottomPalette.Top - margin - middleTop));

        Rectangle rightPanel = new(
            width - rightPanelWidth - margin,
            middleTop,
            rightPanelWidth,
            Math.Min(middleHeight, (int)MathF.Round(580 * scale)));

        int canvasLeft = leftToolbar.Right + margin;
        int canvasTop = middleTop;
        int canvasRight = rightPanel.Left - margin;
        int canvasBottom = bottomPalette.Top - margin;

        Rectangle simulationCanvas = new(
            canvasLeft,
            canvasTop,
            Math.Max(10, canvasRight - canvasLeft),
            Math.Max(10, canvasBottom - canvasTop));

        return new UiLayoutBounds(
            topBar,
            leftToolbar,
            rightPanel,
            bottomPalette,
            statusBar,
            simulationCanvas,
            isCompact,
            scale,
            dpiScale);
    }
}
