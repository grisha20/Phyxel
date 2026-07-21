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
            itemY += GetGroupGap(tool.Id);
            Rectangle itemBounds = new(bounds.X + padding, itemY, itemWidth, itemHeight);
            if (itemBounds.Contains(input.MousePosition))
            {
                hoveredTool = tool;
                if (tool.Enabled && input.LeftPressed)
                {
                    activeTool = tool.Id;
                }
            }

            itemY += itemHeight + 6;
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
        UiIconTextureCache iconCache,
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
            int groupGap = GetGroupGap(tool.Id);
            if (groupGap > 0)
            {
                spriteBatch.Draw(pixel, new Rectangle(bounds.X + 14, itemY + groupGap / 2, bounds.Width - 28, 1), UiTheme.BorderColor);
                itemY += groupGap;
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
            int iconSize = Math.Clamp(itemHeight - 14, 22, 30);
            Rectangle iconBox = new(itemBounds.X + 11, itemBounds.Center.Y - iconSize / 2, iconSize, iconSize);
            if (iconCache.TryGet(tool.Key, out Texture2D iconTexture))
            {
                UiIconRenderer.DrawIcon(spriteBatch, iconTexture, iconBox, iconColor);
            }
            else
            {
                UiIconRenderer.DrawToolIcon(spriteBatch, pixel, tool.Key, iconBox, iconColor);
            }

            // Title — measure available width to prevent overlap with "Скоро" tag
            if (!tool.Enabled)
            {
                float nameScale = itemHeight < 44 ? 0.70f : 0.82f;
                float tagScale = itemHeight < 44 ? 0.46f : 0.66f;
                Vector2 tagSize = font.MeasureString("Скоро") * tagScale;
                int badgeWidth = (int)tagSize.X + 12;
                int badgeHeight = (int)(font.LineSpacing * tagScale) + (itemHeight < 44 ? 5 : 8);
                Rectangle badge = new(iconBox.Right + 8, itemBounds.Bottom - badgeHeight - 4, badgeWidth, badgeHeight);
                float maxTextWidth = itemBounds.Right - iconBox.Right - 17;

                // Truncate display name if it would overlap
                string displayText = tool.DisplayName;
                if (font.MeasureString(displayText).X * nameScale > maxTextWidth && displayText.Length > 2)
                {
                    while (displayText.Length > 2 && font.MeasureString(displayText + "…").X * nameScale > maxTextWidth)
                    {
                        displayText = displayText[..^1];
                    }
                    displayText += "…";
                }

                Vector2 textPos = new(iconBox.Right + 8, itemBounds.Y + 3);
                spriteBatch.DrawString(font, displayText, textPos, textColor, 0, Vector2.Zero, nameScale, SpriteEffects.None, 0);

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
                int availableTextWidth = Math.Max(1, itemBounds.Right - iconBox.Right - 12);
                int availableTextHeight = Math.Max(1, itemBounds.Height - 6);
                (string displayText, float nameScale) = FitEnabledToolLabel(
                    font,
                    tool,
                    availableTextWidth,
                    availableTextHeight);
                Vector2 textSize = font.MeasureString(displayText) * nameScale;
                Vector2 textPos = new(
                    iconBox.Right + 6,
                    itemBounds.Center.Y - textSize.Y / 2f);
                spriteBatch.DrawString(
                    font,
                    displayText,
                    textPos,
                    textColor,
                    0,
                    Vector2.Zero,
                    nameScale,
                    SpriteEffects.None,
                    0);
            }

            itemY += itemHeight + 6;
        }

        int footerHeight = font.LineSpacing * 2 + 18;
        if (itemY + footerHeight + 8 < bounds.Bottom)
        {
            Rectangle footer = new(bounds.X + 10, itemY + 4, bounds.Width - 20, footerHeight);
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
            itemY += GetGroupGap(tool.Id);
            Rectangle itemBounds = new(bounds.X + padding, itemY, bounds.Width - padding * 2, itemHeight);
            if (tool.Id == toolId)
            {
                return itemBounds;
            }
            itemY += itemHeight + 6;
        }
        return Rectangle.Empty;
    }

    internal static (string Text, float Scale) FitEnabledToolLabel(
        SpriteFont font,
        ToolDefinition tool,
        int maximumWidth,
        int maximumHeight)
    {
        const float minimumScale = 0.72f;
        string text = tool.Id == PhyxelToolId.Pan ? "Камера\nПанорама" : tool.DisplayName;
        Vector2 size = font.MeasureString(text);
        float scale = Math.Min(
            1f,
            Math.Min(maximumWidth / Math.Max(1f, size.X), maximumHeight / Math.Max(1f, size.Y)));

        if (tool.Id == PhyxelToolId.Pan && scale < minimumScale)
        {
            text = "Камера";
            size = font.MeasureString(text);
            scale = Math.Min(
                1f,
                Math.Min(maximumWidth / Math.Max(1f, size.X), maximumHeight / Math.Max(1f, size.Y)));
        }

        if (scale >= minimumScale)
        {
            return (text, scale);
        }

        scale = minimumScale;
        const string ellipsis = "…";
        while (text.Length > 1 && font.MeasureString(text + ellipsis).X * scale > maximumWidth)
        {
            text = text[..^1];
        }
        return (text + ellipsis, scale);
    }

    private static int GetHeaderHeight(SpriteFont font) => font.LineSpacing + 28;

    private static int GetItemHeight(SpriteFont font, Rectangle bounds)
    {
        int desired = Math.Clamp(font.LineSpacing + 22, 40, 54);
        int fixedSpacing = (Tools.Count - 1) * 6 + GetGroupGap(PhyxelToolId.Line) + GetGroupGap(PhyxelToolId.Pan);
        int available = Math.Max(Tools.Count * 34, bounds.Height - GetHeaderHeight(font) - fixedSpacing);
        return Math.Clamp(Math.Min(desired, available / Tools.Count), 34, 54);
    }

    private static int GetGroupGap(PhyxelToolId toolId) => toolId switch
    {
        PhyxelToolId.Line => 10,
        PhyxelToolId.Pan => 12,
        _ => 0
    };
}
