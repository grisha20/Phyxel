using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Phyxel.Input;

namespace Phyxel.UI;

public sealed class UiToggleSwitch
{
    private bool hovered;
    private bool pressed;

    public UiToggleSwitch(string label)
    {
        Label = label;
    }

    public string Label { get; }
    public Rectangle Bounds { get; set; }

    public bool Update(RawInputSnapshot input)
    {
        hovered = Bounds.Contains(input.MousePosition);
        pressed = hovered && input.LeftDown;
        return hovered && input.LeftPressed;
    }

    public void Draw(
        SpriteBatch spriteBatch,
        SpriteFont font,
        UiPanelBackdropRenderer renderer,
        Texture2D pixel,
        bool value)
    {
        Color rowColor = pressed
            ? UiTheme.CardPressed
            : hovered ? UiTheme.CardHover : Color.Transparent;
        if (rowColor != Color.Transparent)
        {
            renderer.DrawRoundedRectangle(spriteBatch, Bounds, rowColor, 5);
        }

        spriteBatch.DrawString(
            font,
            Label,
            new Vector2(Bounds.X + 4, Bounds.Center.Y - font.LineSpacing / 2f),
            UiTheme.TextSecondary);

        int switchHeight = Math.Clamp(Bounds.Height - 12, 20, 28);
        int switchWidth = (int)(switchHeight * 1.75f);
        Rectangle track = new(
            Bounds.Right - switchWidth - 4,
            Bounds.Center.Y - switchHeight / 2,
            switchWidth,
            switchHeight);
        renderer.DrawRoundedRectangle(
            spriteBatch,
            track,
            value ? UiTheme.ToggleOn : new Color(61, 72, 86),
            switchHeight / 2);
        UiIconRenderer.DrawStrokedRectangle(spriteBatch, pixel, track, 1, value ? UiTheme.ToggleOn : UiTheme.BorderColor);

        int knobSize = switchHeight - 6;
        int knobX = value ? track.Right - knobSize - 3 : track.X + 3;
        Rectangle knob = new(knobX, track.Center.Y - knobSize / 2, knobSize, knobSize);
        renderer.DrawRoundedRectangle(spriteBatch, knob, UiTheme.TextPrimary, knobSize / 2);
    }
}
