using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Phyxel.Core;
using Phyxel.Input;

namespace Phyxel.UI;

internal sealed class UiSimulationModeSelector
{
    public Rectangle Bounds { get; set; }
    public Rectangle SandboxBounds => new(Bounds.X, Bounds.Y, (Bounds.Width - 6) / 2, Bounds.Height);
    public Rectangle SimulationBounds => new(SandboxBounds.Right + 6, Bounds.Y,
        Bounds.Right - SandboxBounds.Right - 6, Bounds.Height);
    private Point pointer;

    public bool Update(RawInputSnapshot input, SimulationSettings settings)
    {
        pointer = input.MousePosition;
        if (!input.LeftPressed) return false;
        SimulationMode next = SandboxBounds.Contains(pointer) ? SimulationMode.Sandbox
            : SimulationBounds.Contains(pointer) ? SimulationMode.Simulation : settings.Mode;
        bool changed = settings.Mode != next;
        settings.Mode = next;
        return changed;
    }

    public void Draw(SpriteBatch sprites, SpriteFont font, UiPanelBackdropRenderer renderer,
        Texture2D pixel, SimulationMode mode)
    {
        DrawOption(SandboxBounds, "Песочница", mode == SimulationMode.Sandbox);
        DrawOption(SimulationBounds, "Симуляция", mode == SimulationMode.Simulation);
        void DrawOption(Rectangle bounds, string text, bool selected)
        {
            renderer.DrawRoundedRectangle(sprites, bounds,
                selected ? UiTheme.CardActive : bounds.Contains(pointer) ? UiTheme.CardHover : UiTheme.CardBackground, 5);
            UiIconRenderer.DrawStrokedRectangle(sprites, pixel, bounds, 1,
                selected ? UiTheme.PrimaryAccent : UiTheme.BorderColor);
            Vector2 size = font.MeasureString(text);
            float scale = Math.Min(.85f, Math.Min((bounds.Width - 12) / size.X, (bounds.Height - 6) / size.Y));
            sprites.DrawString(font, text, new Vector2(bounds.Center.X - size.X * scale / 2,
                bounds.Center.Y - size.Y * scale / 2), UiTheme.TextPrimary, 0, Vector2.Zero, scale, SpriteEffects.None, 0);
        }
    }
}
