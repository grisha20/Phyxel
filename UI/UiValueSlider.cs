using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Phyxel.Input;

namespace Phyxel.UI;

public sealed class UiValueSlider
{
    private bool dragging;
    private readonly float[]? presets;
    private readonly string valueFormat;

    public UiValueSlider(
        string label,
        float minimum,
        float maximum,
        float step,
        float initialValue,
        string suffix,
        string valueFormat = "0.##")
    {
        Label = label;
        Minimum = minimum;
        Maximum = maximum;
        Step = step;
        Value = initialValue;
        Suffix = suffix;
        this.valueFormat = valueFormat;
    }

    public UiValueSlider(
        string label,
        ReadOnlySpan<float> presets,
        float initialValue,
        string suffix,
        string valueFormat = "0.##")
    {
        if (presets.Length < 2)
        {
            throw new ArgumentException("A preset slider requires at least two values.", nameof(presets));
        }

        this.presets = presets.ToArray();
        for (int index = 0; index < this.presets.Length; index++)
        {
            if (!float.IsFinite(this.presets[index]) ||
                index > 0 && this.presets[index] <= this.presets[index - 1])
            {
                throw new ArgumentException(
                    "Slider presets must be finite and strictly increasing.",
                    nameof(presets));
            }
        }

        Label = label;
        Minimum = this.presets[0];
        Maximum = this.presets[^1];
        Step = 0;
        Value = FindNearestPreset(initialValue);
        Suffix = suffix;
        this.valueFormat = valueFormat;
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
        float previous = Value;
        if (presets is not null)
        {
            int presetIndex = Math.Clamp(
                (int)MathF.Round(normalized * (presets.Length - 1)),
                0,
                presets.Length - 1);
            Value = presets[presetIndex];
        }
        else
        {
            float requested = Minimum + normalized * (Maximum - Minimum);
            float snapped = MathF.Round(requested / Step) * Step;
            Value = Math.Clamp(snapped, Minimum, Maximum);
        }
        return Math.Abs(previous - Value) > 0.0001f;
    }

    public void Draw(SpriteBatch spriteBatch, SpriteFont font, UiPanelBackdropRenderer renderer, Texture2D pixel)
    {
        string text = $"{Label}: {Value.ToString(valueFormat)}{Suffix}";
        spriteBatch.DrawString(font, text, new Vector2(Bounds.X, Bounds.Y - 24), new Color(225, 225, 225));
        Rectangle track = new(Bounds.X, Bounds.Y + Bounds.Height / 2 - 2, Bounds.Width, 4);
        renderer.DrawLine(spriteBatch, track, new Color(82, 82, 82));
        float normalized = presets is null
            ? (Value - Minimum) / (Maximum - Minimum)
            : FindNearestPresetIndex(Value) / (float)(presets.Length - 1);
        int filledWidth = (int)(Bounds.Width * normalized);
        renderer.DrawLine(spriteBatch, new Rectangle(track.X, track.Y, filledWidth, track.Height), new Color(76, 148, 207));
        int handleX = Bounds.X + filledWidth - 5;
        spriteBatch.Draw(pixel, new Rectangle(handleX, Bounds.Y + 2, 10, Bounds.Height - 4), new Color(230, 230, 230));
    }

    private float FindNearestPreset(float value) => presets![FindNearestPresetIndex(value)];

    private int FindNearestPresetIndex(float value)
    {
        int nearestIndex = 0;
        float nearestDistance = Math.Abs(value - presets![0]);
        for (int index = 1; index < presets.Length; index++)
        {
            float distance = Math.Abs(value - presets[index]);
            if (distance < nearestDistance)
            {
                nearestIndex = index;
                nearestDistance = distance;
            }
        }
        return nearestIndex;
    }
}
