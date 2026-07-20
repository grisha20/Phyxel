using Microsoft.Xna.Framework;

namespace Phyxel.UI;

public static class UiTheme
{
    // Backgrounds & Surface Colors
    public static readonly Color WindowBackground = new(13, 15, 18);
    public static readonly Color PanelBackground = new(22, 25, 30, 240);
    public static readonly Color PanelHeader = new(28, 32, 39);
    public static readonly Color CardBackground = new(28, 32, 38);
    public static readonly Color CardHover = new(38, 44, 53);
    public static readonly Color CardActive = new(46, 54, 66);
    public static readonly Color CardSelectedBorder = new(242, 182, 85);
    public static readonly Color BorderColor = new(44, 49, 59);
    public static readonly Color SubtlyTransparentBorder = new(60, 68, 80, 160);

    // Text Colors
    public static readonly Color TextPrimary = new(235, 240, 245);
    public static readonly Color TextSecondary = new(155, 165, 178);
    public static readonly Color TextMuted = new(100, 110, 125);
    public static readonly Color TextDisabled = new(75, 82, 95);

    // Accent Colors
    public static readonly Color PrimaryAccent = new(235, 165, 60); // Phyxel Gold/Amber
    public static readonly Color ActiveToolAccent = new(235, 165, 60);
    public static readonly Color StatusGreen = new(72, 199, 142);
    public static readonly Color StatusRed = new(240, 90, 90);
    public static readonly Color TemperatureAccent = new(235, 120, 65);

    // Category Accent Colors
    public static readonly Color CategoryPowders = new(218, 184, 92);
    public static readonly Color CategoryLiquids = new(75, 160, 235);
    public static readonly Color CategoryGases = new(140, 200, 190);
    public static readonly Color CategorySolids = new(165, 145, 215);
    public static readonly Color CategoryCombustion = new(240, 100, 50);
    public static readonly Color CategoryTools = new(175, 185, 195);
}
