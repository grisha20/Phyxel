using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Phyxel.Input;

namespace Phyxel.UI;

public sealed class UiValueSlider
{
    private bool dragging;

    public UiValueSlider(string label, float minimum, float maximum, float step, float initialValue, string suffix)
    {
        Label = label;
        Minimum = minimum;
        Maximum = maximum;
        Step = step;
        Value = initialValue;
        Suffix = suffix;
    }

    public string Label { get; }
    public string Suffix { get; }
    public float Minimum { get; }
    public float Maximum { get; }
    public float Step { get; }
    public float Value { get; set; }
    public Rectangle Bounds { get; set; }

    public bool Update(RawInputSnapshot input)
    {
        if (input.LeftPressed && Bounds.Contains(input.MousePosition))
        {
            dragging = true;
        }

        if (input.LeftReleased)
        {
            dragging = false;
        }

        if (!dragging)
        {
            return false;
        }

        float normalized = Math.Clamp((input.MousePosition.X - Bounds.X) / (float)Math.Max(1, Bounds.Width), 0f, 1f);
        float requested = Minimum + normalized * (Maximum - Minimum);
        float snapped = MathF.Round(requested / Step) * Step;
        float previous = Value;
        Value = Math.Clamp(snapped, Minimum, Maximum);
        return Math.Abs(previous - Value) > 0.0001f;
    }

    public void Draw(SpriteBatch spriteBatch, SpriteFont font, UiPanelBackdropRenderer renderer, Texture2D pixel)
    {
        string text = $"{Label}: {Value:0.##}{Suffix}";
        spriteBatch.DrawString(font, text, new Vector2(Bounds.X, Bounds.Y - 24), new Color(225, 225, 225));
        Rectangle track = new(Bounds.X, Bounds.Y + Bounds.Height / 2 - 2, Bounds.Width, 4);
        renderer.DrawLine(spriteBatch, track, new Color(82, 82, 82));
        float normalized = (Value - Minimum) / (Maximum - Minimum);
        int filledWidth = (int)(Bounds.Width * normalized);
        renderer.DrawLine(spriteBatch, new Rectangle(track.X, track.Y, filledWidth, track.Height), new Color(76, 148, 207));
        int handleX = Bounds.X + filledWidth - 5;
        spriteBatch.Draw(pixel, new Rectangle(handleX, Bounds.Y + 2, 10, Bounds.Height - 4), new Color(230, 230, 230));
    }
}
