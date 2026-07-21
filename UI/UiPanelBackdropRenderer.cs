using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Phyxel.UI;

public sealed class UiPanelBackdropRenderer
{
    private readonly Texture2D pixel;
    private readonly Texture2D circle;

    public UiPanelBackdropRenderer(Texture2D pixel, Texture2D circle)
    {
        this.pixel = pixel;
        this.circle = circle;
    }

    public void Draw(SpriteBatch spriteBatch, Rectangle bounds, int radius = 10)
    {
        Rectangle shadow = new(bounds.X + 2, bounds.Y + 3, bounds.Width, bounds.Height);
        DrawRoundedRectangle(spriteBatch, shadow, new Color(0, 0, 0, 105), radius);
        DrawRoundedRectangle(spriteBatch, bounds, UiTheme.PanelBackground, radius);
        UiIconRenderer.DrawStrokedRectangle(spriteBatch, pixel, bounds, 1, UiTheme.BorderColor);
    }

    public void DrawRoundedRectangle(SpriteBatch spriteBatch, Rectangle bounds, Color color, int radius)
    {
        int diameter = radius * 2;
        spriteBatch.Draw(pixel, new Rectangle(bounds.X + radius, bounds.Y, bounds.Width - diameter, bounds.Height), color);
        spriteBatch.Draw(pixel, new Rectangle(bounds.X, bounds.Y + radius, bounds.Width, bounds.Height - diameter), color);
        spriteBatch.Draw(circle, new Rectangle(bounds.X, bounds.Y, diameter, diameter), color);
        spriteBatch.Draw(circle, new Rectangle(bounds.Right - diameter, bounds.Y, diameter, diameter), color);
        spriteBatch.Draw(circle, new Rectangle(bounds.X, bounds.Bottom - diameter, diameter, diameter), color);
        spriteBatch.Draw(circle, new Rectangle(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter), color);
    }

    public void DrawLine(SpriteBatch spriteBatch, Rectangle bounds, Color color)
    {
        spriteBatch.Draw(pixel, bounds, color);
    }
}
