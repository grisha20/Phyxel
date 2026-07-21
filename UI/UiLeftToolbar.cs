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
        new(PhyxelToolId.Line, "line", "Линия", false, "Скоро"),
        new(PhyxelToolId.Rectangle, "rectangle", "Прямоугольник", false, "Скоро"),
        new(PhyxelToolId.Circle, "circle", "Круг", false, "Скоро"),
        new(PhyxelToolId.Fill, "fill", "Заливка", false, "Скоро"),
        new(PhyxelToolId.Eyedropper, "eyedropper", "Пипетка", false, "Скоро"),
        new(PhyxelToolId.Pan, "pan", "Камера / панорама", true, "ЛКМ — перемещение, колесо — масштаб")
    ];

    private PhyxelToolId activeTool = PhyxelToolId.Brush;
    private ToolDefinition? hoveredTool;
    private ToolDefinition? previousHoveredTool;
    private float hoverSeconds;

    public PhyxelToolId ActiveTool
    {
        get => activeTool;
        set => activeTool = value;
    }

    public void Update(
        RawInputSnapshot input,
        Rectangle bounds,
        SpriteFont font,
        out bool pointerConsumed)
    {
        pointerConsumed = bounds.Contains(input.MousePosition);
        hoveredTool = null;

        int padding = 10;
        int itemHeight = GetItemHeight(font, bounds);
        int itemY = bounds.Y + GetHeaderHeight(font);
        int itemWidth = bounds.Width - padding * 2;

        for (int index = 0; index < Tools.Count; index++)
        {
            ToolDefinition tool = Tools[index];
            if (tool.Id == PhyxelToolId.Pan)
            {
                itemY += 8;
            }
            Rectangle itemBounds = new(bounds.X + padding, itemY, itemWidth, itemHeight);
            if (itemBounds.Contains(input.MousePosition))
            {
                hoveredTool = tool;
                if (tool.Enabled && input.LeftPressed)
                {
                    activeTool = tool.Id;
                }
            }

            itemY += itemHeight + 5;
        }

        if (hoveredTool == previousHoveredTool && hoveredTool is not null)
        {
            hoverSeconds += input.DeltaSeconds;
        }
        else
        {
            hoverSeconds = 0;
            previousHoveredTool = hoveredTool;
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
            new Vector2(bounds.X + 14, bounds.Y + 10),
            UiTheme.TextMuted);
        spriteBatch.Draw(pixel, new Rectangle(bounds.X + 12, bounds.Y + GetHeaderHeight(font) - 7, bounds.Width - 24, 1), UiTheme.BorderColor);

        int padding = 10;
        int itemHeight = GetItemHeight(font, bounds);
        int itemY = bounds.Y + GetHeaderHeight(font);
        int itemWidth = bounds.Width - padding * 2;

        for (int index = 0; index < Tools.Count; index++)
        {
            ToolDefinition tool = Tools[index];
            if (tool.Id == PhyxelToolId.Pan)
            {
                spriteBatch.Draw(pixel, new Rectangle(bounds.X + 14, itemY + 1, bounds.Width - 28, 1), UiTheme.BorderColor);
                itemY += 8;
            }
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

            backdrop.DrawRoundedRectangle(spriteBatch, itemBounds, bgColor, 5);
            if (isActive)
            {
                UiIconRenderer.DrawStrokedRectangle(spriteBatch, pixel, itemBounds, 2, UiTheme.ActiveToolAccent);
            }
            else if (isHovered && tool.Enabled)
            {
                UiIconRenderer.DrawStrokedRectangle(spriteBatch, pixel, itemBounds, 1, UiTheme.BorderColor);
            }

            // Icon
            int iconSize = Math.Clamp(itemHeight - 16, 18, 25);
            Rectangle iconBox = new(itemBounds.X + 10, itemBounds.Center.Y - iconSize / 2, iconSize, iconSize);
            UiIconRenderer.DrawToolIcon(spriteBatch, pixel, tool.Key, iconBox, iconColor);

            // Title — measure available width to prevent overlap with "Скоро" tag
            float textY = itemBounds.Y + (itemHeight - font.LineSpacing) / 2f;
            if (!tool.Enabled)
            {
                const float tagScale = 0.72f;
                Vector2 tagSize = font.MeasureString("Скоро") * tagScale;
                int badgeWidth = (int)tagSize.X + 12;
                int badgeHeight = (int)(font.LineSpacing * tagScale) + 8;
                Rectangle badge = new(itemBounds.Right - badgeWidth - 8, itemBounds.Center.Y - badgeHeight / 2, badgeWidth, badgeHeight);
                float maxTextWidth = badge.X - (iconBox.Right + 9) - 6;

                // Truncate display name if it would overlap
                string displayText = tool.DisplayName;
                Vector2 nameSize = font.MeasureString(displayText);
                if (nameSize.X > maxTextWidth && displayText.Length > 2)
                {
                    while (displayText.Length > 2 && font.MeasureString(displayText + "…").X > maxTextWidth)
                    {
                        displayText = displayText[..^1];
                    }
                    displayText += "…";
                }

                Vector2 textPos = new(iconBox.Right + 8, textY);
                spriteBatch.DrawString(font, displayText, textPos, textColor);

                // "Скоро" tag
                backdrop.DrawRoundedRectangle(spriteBatch, badge, UiTheme.FieldBackground, 4);
                UiIconRenderer.DrawStrokedRectangle(spriteBatch, pixel, badge, 1, UiTheme.BorderColor);
                Vector2 tagPos = new(badge.Center.X - tagSize.X / 2, badge.Center.Y - tagSize.Y / 2);
                spriteBatch.DrawString(
                    font,
                    "Скоро",
                    tagPos,
                    UiTheme.TextMuted,
                    0,
                    Vector2.Zero,
                    tagScale,
                    SpriteEffects.None,
                    0);
            }
            else
            {
                Vector2 textPos = new(iconBox.Right + 8, textY);
                spriteBatch.DrawString(font, tool.DisplayName, textPos, textColor);
            }

            itemY += itemHeight + 5;
        }

        int footerHeight = font.LineSpacing * 2 + 18;
        if (itemY + footerHeight + 10 < bounds.Bottom)
        {
            Rectangle footer = new(bounds.X + 10, bounds.Bottom - footerHeight - 10, bounds.Width - 20, footerHeight);
            backdrop.DrawRoundedRectangle(spriteBatch, footer, UiTheme.FieldBackground, 5);
            spriteBatch.DrawString(font, "ЛКМ  Рисовать", new Vector2(footer.X + 10, footer.Y + 6), UiTheme.TextMuted);
            spriteBatch.DrawString(font, "ПКМ  Стирать", new Vector2(footer.X + 10, footer.Y + 7 + font.LineSpacing), UiTheme.TextMuted);
        }

        // Draw Tooltip if hovered
        if (hoveredTool is not null && hoverSeconds >= 0.35f)
        {
            string tooltipText = hoveredTool.Tooltip;
            Vector2 tipSize = font.MeasureString(tooltipText);
            Rectangle tipBounds = new(bounds.Right + 8, bounds.Y + GetHeaderHeight(font), (int)tipSize.X + 20, (int)tipSize.Y + 12);

            backdrop.Draw(spriteBatch, tipBounds, 4);
            UiIconRenderer.DrawStrokedRectangle(spriteBatch, pixel, tipBounds, 1, UiTheme.BorderColor);
            spriteBatch.DrawString(font, tooltipText, new Vector2(tipBounds.X + 8, tipBounds.Y + 5), UiTheme.TextPrimary);
        }
    }

    internal Rectangle GetToolBounds(Rectangle bounds, SpriteFont font, PhyxelToolId toolId)
    {
        int padding = 10;
        int itemHeight = GetItemHeight(font, bounds);
        int itemY = bounds.Y + GetHeaderHeight(font);
        for (int index = 0; index < Tools.Count; index++)
        {
            ToolDefinition tool = Tools[index];
            if (tool.Id == PhyxelToolId.Pan)
            {
                itemY += 8;
            }
            Rectangle itemBounds = new(bounds.X + padding, itemY, bounds.Width - padding * 2, itemHeight);
            if (tool.Id == toolId)
            {
                return itemBounds;
            }
            itemY += itemHeight + 5;
        }
        return Rectangle.Empty;
    }

    private static int GetHeaderHeight(SpriteFont font) => font.LineSpacing + 28;

    private static int GetItemHeight(SpriteFont font, Rectangle bounds)
    {
        int desired = Math.Clamp(font.LineSpacing + 16, 38, 52);
        int fixedSpacing = (Tools.Count - 1) * 5 + 8;
        int available = Math.Max(Tools.Count * 34, bounds.Height - GetHeaderHeight(font) - fixedSpacing);
        return Math.Clamp(Math.Min(desired, available / Tools.Count), 34, 52);
    }
}
