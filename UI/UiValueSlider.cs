using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Phyxel.Input;

namespace Phyxel.UI;

public sealed class UiValueSlider
{
    private bool dragging;
    private bool hovered;
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
    public bool IsDragging => dragging;
    public string FormattedValue => $"{Value.ToString(valueFormat)}{Suffix}";
    public void CancelDrag() => dragging = false;

    public bool Update(RawInputSnapshot input)
    {
        Rectangle hitBounds = new(Bounds.X, Bounds.Bottom - 24, Bounds.Width, 24);
        hovered = hitBounds.Contains(input.MousePosition);
        if (input.LeftPressed && hovered)
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

        float normalized = Math.Clamp(
            (input.MousePosition.X - Bounds.X) / (float)Math.Max(1, Bounds.Width),
            0f,
            1f);
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
        string valueText = FormattedValue;
        Vector2 valueSize = font.MeasureString(valueText);
        Rectangle valueBox = new(
            Bounds.Right - (int)valueSize.X - 16,
            Bounds.Y - 2,
            (int)valueSize.X + 16,
            font.LineSpacing + 6);
        Vector2 labelSize = font.MeasureString(Label);
        int labelWidth = Math.Max(1, valueBox.X - Bounds.X - 8);
        float labelScale = Math.Clamp(labelWidth / Math.Max(1f, labelSize.X), 0.72f, 1f);
        spriteBatch.DrawString(
            font,
            Label,
            new Vector2(Bounds.X, Bounds.Y + (font.LineSpacing - labelSize.Y * labelScale) * 0.5f),
            UiTheme.TextSecondary,
            0,
            Vector2.Zero,
            labelScale,
            SpriteEffects.None,
            0);

        renderer.DrawRoundedRectangle(spriteBatch, valueBox, UiTheme.FieldBackground, 4);
        UiIconRenderer.DrawStrokedRectangle(spriteBatch, pixel, valueBox, 1, UiTheme.BorderColor);
        spriteBatch.DrawString(
            font,
            valueText,
            new Vector2(valueBox.Center.X - valueSize.X / 2, valueBox.Center.Y - font.LineSpacing / 2f),
            UiTheme.TextPrimary);

        Rectangle track = new(Bounds.X, Bounds.Bottom - 11, Bounds.Width, 6);
        renderer.DrawRoundedRectangle(spriteBatch, track, new Color(53, 64, 76), 3);
        float normalized = presets is null
            ? (Value - Minimum) / (Maximum - Minimum)
            : FindNearestPresetIndex(Value) / (float)(presets.Length - 1);
        int filledWidth = (int)(Bounds.Width * normalized);
        if (filledWidth > 0)
        {
            renderer.DrawRoundedRectangle(
                spriteBatch,
                new Rectangle(track.X, track.Y, filledWidth, track.Height),
                UiTheme.SliderAccent,
                3);
        }
        int handleSize = dragging ? 16 : hovered ? 15 : 14;
        int handleX = Bounds.X + filledWidth;
        Rectangle handle = new(handleX - handleSize / 2, track.Center.Y - handleSize / 2, handleSize, handleSize);
        renderer.DrawRoundedRectangle(spriteBatch, handle, UiTheme.TextPrimary, handleSize / 2);
        UiIconRenderer.DrawStrokedRectangle(spriteBatch, pixel, handle, 1, UiTheme.BorderHighlight);
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
