using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Phyxel.Input;

namespace Phyxel.UI;

public enum PhyxelToolId
{
    Brush,
    Eraser,
    Temperature,
    Pan,
    Line,
    Rectangle,
    Circle,
    Fill,
    Eyedropper
}

public sealed record ToolDefinition(
    PhyxelToolId Id,
    string Key,
    string DisplayName,
    bool Enabled,
    string Tooltip);

public sealed class UiLeftToolbar
{
    public static readonly IReadOnlyList<ToolDefinition> Tools =
    [
        new(PhyxelToolId.Brush, "brush", "Кисть", true, "Обычное рисование материалом"),
        new(PhyxelToolId.Eraser, "eraser", "Ластик", true, "Стирание элементов"),
        new(PhyxelToolId.Temperature, "temperature", "Температура", true, "Изменение температуры"),
        new(PhyxelToolId.Pan, "pan", "Камера", true, "Панорамирование вида"),
        new(PhyxelToolId.Line, "line", "Линия", false, "Скоро"),
        new(PhyxelToolId.Rectangle, "rectangle", "Прямоуг.", false, "Скоро"),
        new(PhyxelToolId.Circle, "circle", "Круг", false, "Скоро"),
        new(PhyxelToolId.Fill, "fill", "Заливка", false, "Скоро"),
        new(PhyxelToolId.Eyedropper, "eyedropper", "Пипетка", false, "Скоро")
    ];

    private PhyxelToolId activeTool = PhyxelToolId.Brush;
    private ToolDefinition? hoveredTool;

    public PhyxelToolId ActiveTool
    {
        get => activeTool;
        set => activeTool = value;
    }

    public void Update(RawInputSnapshot input, Rectangle bounds, out bool pointerConsumed)
    {
        pointerConsumed = bounds.Contains(input.MousePosition);
        hoveredTool = null;

        int padding = 8;
        int itemHeight = bounds.Height < 600 ? 28 : 34;
        int itemY = bounds.Y + padding + 24; // Below header
        int itemWidth = bounds.Width - padding * 2;

        foreach (ToolDefinition tool in Tools)
        {
            Rectangle itemBounds = new(bounds.X + padding, itemY, itemWidth, itemHeight);
            if (itemBounds.Contains(input.MousePosition))
            {
                hoveredTool = tool;
                if (tool.Enabled && input.LeftPressed)
                {
                    activeTool = tool.Id;
                }
            }

            itemY += itemHeight + 3;
        }
    }

    public void Draw(
        SpriteBatch spriteBatch,
        SpriteFont font,
        UiPanelBackdropRenderer backdrop,
        Texture2D pixel,
        Rectangle bounds)
    {
        backdrop.Draw(spriteBatch, bounds, 8);

        // Section Title
        spriteBatch.DrawString(
            font,
            "ИНСТРУМЕНТЫ",
            new Vector2(bounds.X + 12, bounds.Y + 8),
            UiTheme.TextMuted);

        int padding = 8;
        int itemHeight = bounds.Height < 600 ? 28 : 34;
        int itemY = bounds.Y + padding + 24;
        int itemWidth = bounds.Width - padding * 2;

        foreach (ToolDefinition tool in Tools)
        {
            Rectangle itemBounds = new(bounds.X + padding, itemY, itemWidth, itemHeight);
            bool isActive = tool.Id == activeTool && tool.Enabled;
            bool isHovered = tool == hoveredTool;

            Color bgColor = isActive
                ? UiTheme.CardActive
                : (isHovered && tool.Enabled ? UiTheme.CardHover : UiTheme.CardBackground);
            Color textColor = tool.Enabled
                ? (isActive ? UiTheme.TextPrimary : UiTheme.TextSecondary)
                : UiTheme.TextDisabled;
            Color iconColor = isActive ? UiTheme.ActiveToolAccent : (tool.Enabled ? UiTheme.TextSecondary : UiTheme.TextDisabled);

            backdrop.Draw(spriteBatch, itemBounds, 4);
            if (isActive)
            {
                UiIconRenderer.DrawStrokedRectangle(spriteBatch, pixel, itemBounds, 2, UiTheme.ActiveToolAccent);
            }
            else if (isHovered && tool.Enabled)
            {
                UiIconRenderer.DrawStrokedRectangle(spriteBatch, pixel, itemBounds, 1, UiTheme.BorderColor);
            }

            // Icon
            Rectangle iconBox = new(itemBounds.X + 6, itemBounds.Y + (itemHeight - 16) / 2, 16, 16);
            UiIconRenderer.DrawToolIcon(spriteBatch, pixel, tool.Key, iconBox, iconColor);

            // Title — measure available width to prevent overlap with "Скоро" tag
            float textY = itemBounds.Y + (itemHeight - font.LineSpacing) / 2f;
            if (!tool.Enabled)
            {
                Vector2 tagSize = font.MeasureString("Скоро");
                float tagX = itemBounds.Right - tagSize.X - 6;
                float maxTextWidth = tagX - (iconBox.Right + 8) - 4;

                // Truncate display name if it would overlap
                string displayText = tool.DisplayName;
                Vector2 nameSize = font.MeasureString(displayText);
                if (nameSize.X > maxTextWidth && displayText.Length > 2)
                {
                    while (displayText.Length > 2 && font.MeasureString(displayText + "..").X > maxTextWidth)
                    {
                        displayText = displayText[..^1];
                    }
                    displayText += "..";
                }

                Vector2 textPos = new(iconBox.Right + 8, textY);
                spriteBatch.DrawString(font, displayText, textPos, textColor);

                // "Скоро" tag
                Vector2 tagPos = new(tagX, textY);
                spriteBatch.DrawString(font, "Скоро", tagPos, UiTheme.TextMuted);
            }
            else
            {
                Vector2 textPos = new(iconBox.Right + 8, textY);
                spriteBatch.DrawString(font, tool.DisplayName, textPos, textColor);
            }

            itemY += itemHeight + 3;
        }

        // Draw Tooltip if hovered
        if (hoveredTool is not null)
        {
            string tooltipText = hoveredTool.Tooltip;
            Vector2 tipSize = font.MeasureString(tooltipText);
            Rectangle tipBounds = new(bounds.Right + 6, bounds.Y + 12, (int)tipSize.X + 16, (int)tipSize.Y + 10);

            backdrop.Draw(spriteBatch, tipBounds, 4);
            UiIconRenderer.DrawStrokedRectangle(spriteBatch, pixel, tipBounds, 1, UiTheme.BorderColor);
            spriteBatch.DrawString(font, tooltipText, new Vector2(tipBounds.X + 8, tipBounds.Y + 5), UiTheme.TextPrimary);
        }
    }
}
