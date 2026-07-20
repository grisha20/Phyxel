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
    bool IsCompact);

public static class UiLayoutCalculator
{
    public static UiLayoutBounds Calculate(Viewport viewport)
    {
        int width = viewport.Width;
        int height = viewport.Height;

        // Base scale factor relative to standard 1080p
        float scale = Math.Clamp(height / 1080f, 0.72f, 1.4f);
        bool isCompact = height < 750;

        int margin = Math.Max(4, (int)(8 * scale));
        int topBarHeight = Math.Max(40, (int)(48 * scale));
        int statusBarHeight = Math.Max(24, (int)(28 * scale));
        int leftToolbarWidth = Math.Max(140, (int)(170 * scale));
        int rightPanelWidth = Math.Max(210, (int)(250 * scale));
        int bottomPaletteHeight = Math.Max(95, (int)(120 * scale));

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
            middleHeight);

        Rectangle rightPanel = new(
            width - rightPanelWidth - margin,
            middleTop,
            rightPanelWidth,
            middleHeight);

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
            isCompact);
    }
}
