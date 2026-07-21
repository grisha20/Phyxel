using Microsoft.Xna.Framework;

namespace Phyxel.UI;

public static class UiTheme
{
    // Backgrounds & Surface Colors
    public static readonly Color WindowBackground = new(7, 11, 15);
    public static readonly Color TopBarBackground = new(13, 19, 26, 252);
    public static readonly Color PanelBackground = new(17, 24, 32, 250);
    public static readonly Color PanelHeader = new(24, 32, 42);
    public static readonly Color CardBackground = new(29, 39, 51);
    public static readonly Color CardHover = new(37, 49, 62);
    public static readonly Color CardPressed = new(43, 57, 71);
    public static readonly Color CardActive = new(39, 51, 65);
    public static readonly Color CardSelectedBorder = new(255, 210, 118);
    public static readonly Color BorderColor = new(43, 57, 71);
    public static readonly Color BorderHighlight = new(67, 83, 101);
    public static readonly Color SubtlyTransparentBorder = new(61, 76, 91, 160);
    public static readonly Color FieldBackground = new(14, 19, 25);
    public static readonly Color LabelBackground = new(12, 16, 22, 248);

    // Text Colors
    public static readonly Color TextPrimary = new(237, 242, 247);
    public static readonly Color TextSecondary = new(157, 168, 183);
    public static readonly Color TextMuted = new(105, 118, 135);
    public static readonly Color TextDisabled = new(75, 82, 95);

    // Accent Colors
    public static readonly Color PrimaryAccent = new(242, 182, 85); // Phyxel Gold/Amber
    public static readonly Color AccentPressed = new(216, 154, 53);
    public static readonly Color ActiveToolAccent = new(242, 182, 85);
    public static readonly Color StatusGreen = new(72, 199, 142);
    public static readonly Color StatusRed = new(240, 90, 90);
    public static readonly Color TemperatureAccent = new(235, 120, 65);
    public static readonly Color SliderAccent = new(70, 164, 225);
    public static readonly Color ToggleOn = new(55, 199, 146);
    public static readonly Color Danger = new(238, 82, 82);

    // Category Accent Colors
    public static readonly Color CategoryPowders = new(218, 184, 92);
    public static readonly Color CategoryLiquids = new(75, 160, 235);
    public static readonly Color CategoryGases = new(140, 200, 190);
    public static readonly Color CategorySolids = new(165, 145, 215);
    public static readonly Color CategoryCombustion = new(240, 100, 50);
    public static readonly Color CategoryTools = new(175, 185, 195);
}
