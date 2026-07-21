using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Phyxel.Input;

namespace Phyxel.UI;

public enum UiButtonVisualState
{
    Idle,
    Hover,
    Pressed,
    Active,
    Disabled
}

public sealed class UiIconButton
{
    public UiIconButton(string label)
    {
        Label = label;
    }

    public string Label { get; set; }
    public Rectangle Bounds { get; set; }
    public bool Active { get; set; }
    public bool Enabled { get; set; } = true;
    public Color AccentColor { get; set; } = new(78, 132, 190);
    public UiButtonVisualState VisualState { get; private set; }

    public bool Update(RawInputSnapshot input)
    {
        if (!Enabled)
        {
            VisualState = UiButtonVisualState.Disabled;
            return false;
        }

        bool hovered = Bounds.Contains(input.MousePosition);
        VisualState = Active
            ? UiButtonVisualState.Active
            : input.LeftDown && hovered
                ? UiButtonVisualState.Pressed
                : hovered
                    ? UiButtonVisualState.Hover
                    : UiButtonVisualState.Idle;
        return hovered && input.LeftPressed;
    }

    public void Draw(
        SpriteBatch spriteBatch,
        SpriteFont font,
        UiPanelBackdropRenderer renderer,
        Texture2D pixel,
        bool showSwatch = false)
    {
        Color background = VisualState switch
        {
            UiButtonVisualState.Hover => new Color(62, 62, 62, 235),
            UiButtonVisualState.Pressed => new Color(46, 86, 122, 245),
            UiButtonVisualState.Active => new Color(48, 88, 124, 245),
            UiButtonVisualState.Disabled => new Color(32, 35, 40, 205),
            _ => new Color(43, 43, 43, 230)
        };
        renderer.DrawRoundedRectangle(spriteBatch, Bounds, background, 6);
        if (Active)
        {
            renderer.DrawLine(spriteBatch, new Rectangle(Bounds.X, Bounds.Y + 5, 3, Bounds.Height - 10), AccentColor);
        }

        int textOffset = 12;
        if (showSwatch)
        {
            spriteBatch.Draw(pixel, new Rectangle(Bounds.X + 11, Bounds.Y + 10, 18, 18), AccentColor);
            textOffset = 39;
        }

        Vector2 size = font.MeasureString(Label);
        Vector2 position = new(Bounds.X + textOffset, Bounds.Y + (Bounds.Height - size.Y) * 0.5f);
        spriteBatch.DrawString(
            font,
            Label,
            position,
            Enabled ? Color.White : UiTheme.TextDisabled);
    }
}
