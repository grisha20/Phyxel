using System;
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
    public bool Danger { get; set; }
    public string? IconKey { get; set; }
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
        UiIconTextureCache iconCache,
        bool showSwatch = false)
    {
        Color background = VisualState switch
        {
            UiButtonVisualState.Hover => UiTheme.CardHover,
            UiButtonVisualState.Pressed => UiTheme.CardPressed,
            UiButtonVisualState.Active => UiTheme.CardActive,
            UiButtonVisualState.Disabled => new Color(22, 28, 35, 210),
            _ => UiTheme.CardBackground
        };
        renderer.DrawRoundedRectangle(spriteBatch, Bounds, background, 6);
        Color border = Danger
            ? UiTheme.Danger * (Enabled ? 0.75f : 0.3f)
            : VisualState is UiButtonVisualState.Hover or UiButtonVisualState.Pressed
                ? UiTheme.BorderHighlight
                : UiTheme.BorderColor;
        UiIconRenderer.DrawStrokedRectangle(spriteBatch, pixel, Bounds, 1, border);
        if (Active)
        {
            renderer.DrawLine(spriteBatch, new Rectangle(Bounds.X, Bounds.Y + 5, 3, Bounds.Height - 10), AccentColor);
        }

        int textOffset = 12;
        if (!string.IsNullOrEmpty(IconKey))
        {
            int iconSize = Math.Clamp(Bounds.Height - 16, 14, 22);
            Rectangle iconBounds = new(
                Bounds.X + 10,
                Bounds.Center.Y - iconSize / 2,
                iconSize,
                iconSize);
            Color iconColor = Enabled ? (Danger ? UiTheme.Danger : UiTheme.TextSecondary) : UiTheme.TextDisabled;
            if (iconCache.TryGet(IconKey, out Texture2D iconTexture))
            {
                UiIconRenderer.DrawIcon(spriteBatch, iconTexture, iconBounds, iconColor);
            }
            else
            {
                UiIconRenderer.DrawActionIcon(spriteBatch, pixel, IconKey, iconBounds, iconColor);
            }
            textOffset = iconBounds.Right - Bounds.X + 8;
        }
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
