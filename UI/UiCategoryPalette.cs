using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Phyxel.Input;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.UI;

public sealed class UiCategoryPalette
{
    private readonly MaterialRegistry registry;
    private SpriteFont font;
    private readonly MaterialCardPreviewCache previewCache;
    private MaterialCategoryType activeCategory = MaterialCategoryType.Powders;
    private int scrollOffset;
    private ushort? hoveredMaterial;
    private bool hoveredMaterialPressed;
    private MaterialCategoryType? hoveredCategory;
    private bool hoveredCategoryPressed;
    private readonly Dictionary<MaterialCategoryType, List<MaterialDefinition>> categorizedMaterials = new();
    private readonly List<MaterialDefinition> allMaterials = new();
    private readonly Dictionary<MaterialCategoryType, int> categoryStartIndices = new();

    private readonly UiIconButton leftArrowButton = new("<");
    private readonly UiIconButton rightArrowButton = new(">");

    public UiCategoryPalette(
        MaterialRegistry registry,
        SpriteFont font,
        MaterialCardPreviewCache previewCache)
    {
        this.registry = registry;
        this.font = font;
        this.previewCache = previewCache;
        RebuildCategoryCache();
    }

    public SpriteFont Font { set => font = value; }

    private static int ComputeCardWidth(int cardHeight) => Math.Clamp((int)(cardHeight * 1.55f), 96, 132);

    public void RebuildCategoryCache()
    {
        categorizedMaterials.Clear();
        allMaterials.Clear();
        categoryStartIndices.Clear();
        foreach (MaterialCategoryDefinition cat in MaterialCategoryResolver.AllCategories)
        {
            categorizedMaterials[cat.Type] = new List<MaterialDefinition>();
        }

        foreach (MaterialDefinition mat in registry.SelectableMaterials)
        {
            if (mat.Hidden || mat.Id == CoreMaterialIds.Empty) continue;
            MaterialCategoryType category = MaterialCategoryResolver.Resolve(mat);
            categorizedMaterials[category].Add(mat);
        }

        foreach (MaterialCategoryDefinition category in MaterialCategoryResolver.AllCategories)
        {
            categoryStartIndices[category.Type] = allMaterials.Count;
            allMaterials.AddRange(categorizedMaterials[category.Type]);
        }
    }

    public ushort? Update(
        RawInputSnapshot input,
        Rectangle bounds,
        ushort currentSelectedMaterial,
        bool isTemperatureActive,
        out bool pointerConsumed)
    {
        pointerConsumed = bounds.Contains(input.MousePosition);
        ushort? newlySelectedMaterial = null;
        hoveredMaterial = null;
        hoveredMaterialPressed = false;
        hoveredCategory = null;
        hoveredCategoryPressed = false;
        MaterialCategoryType? jumpToCategory = null;

        int tabHeight = GetTabHeight();
        int tabY = bounds.Y + 8;
        int tabX = bounds.X + 12;
        int tabGap = 6;
        int[] tabWidths = GetTabWidths(bounds.Width - 24, tabGap);

        // 1. Update Category Tabs
        for (int index = 0; index < MaterialCategoryResolver.AllCategories.Count; index++)
        {
            MaterialCategoryDefinition cat = MaterialCategoryResolver.AllCategories[index];
            int tabWidth = tabWidths[index];
            Rectangle tabBounds = new(tabX, tabY, tabWidth, tabHeight);

            if (tabBounds.Contains(input.MousePosition))
            {
                hoveredCategory = cat.Type;
                hoveredCategoryPressed = input.LeftDown;
            }

            if (input.LeftPressed && tabBounds.Contains(input.MousePosition))
            {
                activeCategory = cat.Type;
                jumpToCategory = cat.Type;
            }

            tabX += tabWidth + tabGap;
        }

        // 2. Material Cards Strip
        Rectangle fullCardsBounds = GetCardsStripBounds(bounds, false);

        List<MaterialDefinition> currentList = allMaterials;
        int cardHeight = fullCardsBounds.Height;
        int cardWidth = ComputeCardWidth(cardHeight);
        int gap = 6;

        if (jumpToCategory.HasValue && categoryStartIndices.TryGetValue(jumpToCategory.Value, out int categoryIndex))
        {
            scrollOffset = categoryIndex * (cardWidth + gap);
        }

        // Compute total cards width for scrolling
        int totalCardsWidth = 0;
        for (int i = 0; i < currentList.Count; i++)
        {
            totalCardsWidth += cardWidth + gap;
        }
        if (currentList.Count > 0) totalCardsWidth -= gap;
        bool overflow = totalCardsWidth > fullCardsBounds.Width;
        Rectangle cardsStripBounds = GetCardsStripBounds(bounds, overflow);
        int maxScroll = Math.Max(0, totalCardsWidth - cardsStripBounds.Width);
        scrollOffset = Math.Clamp(scrollOffset, 0, maxScroll);

        if (cardsStripBounds.Contains(input.MousePosition) && input.WheelDelta != 0)
        {
            scrollOffset = Math.Clamp(scrollOffset - Math.Sign(input.WheelDelta) * 80, 0, maxScroll);
        }

        // Arrow Buttons
        leftArrowButton.Enabled = overflow;
        rightArrowButton.Enabled = overflow;
        leftArrowButton.Bounds = new Rectangle(bounds.X + 8, cardsStripBounds.Y, 28, cardHeight);
        rightArrowButton.Bounds = new Rectangle(cardsStripBounds.Right + 4, cardsStripBounds.Y, 28, cardHeight);

        if (leftArrowButton.Update(input))
        {
            scrollOffset = Math.Max(0, scrollOffset - 120);
        }
        if (rightArrowButton.Update(input))
        {
            scrollOffset = Math.Min(maxScroll, scrollOffset + 120);
        }

        // Card clicks
        int cardX = cardsStripBounds.X - scrollOffset;
        for (int i = 0; i < currentList.Count; i++)
        {
            MaterialDefinition mat = currentList[i];
            Rectangle cardBounds = new(cardX, cardsStripBounds.Y, cardWidth, cardHeight);

            if (cardBounds.Right >= cardsStripBounds.X && cardBounds.X <= cardsStripBounds.Right)
            {
                if (cardBounds.Contains(input.MousePosition))
                {
                    hoveredMaterial = mat.RuntimeIndex;
                    hoveredMaterialPressed = input.LeftDown;
                }
                if (input.LeftPressed && cardBounds.Contains(input.MousePosition))
                {
                    newlySelectedMaterial = mat.RuntimeIndex;
                }
            }

            cardX += cardWidth + gap;
        }

        return newlySelectedMaterial;
    }

    public void Draw(
        SpriteBatch spriteBatch,
        SpriteFont font,
        UiPanelBackdropRenderer backdrop,
        Texture2D pixel,
        Rectangle bounds,
        ushort selectedMaterial,
        bool isTemperatureActive)
    {
        backdrop.Draw(spriteBatch, bounds, 8);

        int tabHeight = GetTabHeight();
        int tabY = bounds.Y + 8;
        int tabX = bounds.X + 12;
        int tabGapDraw = 6;
        int[] tabWidths = GetTabWidths(bounds.Width - 24, tabGapDraw);

        // Draw Category Tabs
        for (int index = 0; index < MaterialCategoryResolver.AllCategories.Count; index++)
        {
            MaterialCategoryDefinition cat = MaterialCategoryResolver.AllCategories[index];
            int tabWidthDraw = tabWidths[index];
            Rectangle tabBounds = new(tabX, tabY, tabWidthDraw, tabHeight);
            bool isSelectedTab = cat.Type == activeCategory;

            bool isHoveredTab = hoveredCategory == cat.Type;
            Color bgColor = isSelectedTab
                ? UiTheme.CardActive
                : isHoveredTab && hoveredCategoryPressed
                    ? UiTheme.CardPressed
                    : isHoveredTab ? UiTheme.CardHover : UiTheme.CardBackground;
            Color borderColor = isSelectedTab ? cat.AccentColor : UiTheme.BorderColor;

            backdrop.DrawRoundedRectangle(spriteBatch, tabBounds, bgColor, 5);
            UiIconRenderer.DrawStrokedRectangle(spriteBatch, pixel, tabBounds, 1, borderColor);
            if (isSelectedTab)
            {
                spriteBatch.Draw(pixel, new Rectangle(tabBounds.X + 6, tabBounds.Bottom - 3, tabBounds.Width - 12, 3), cat.AccentColor);
            }

            // Icon + Label
            int iconSize = Math.Clamp(tabHeight - 18, 14, 22);
            Rectangle dotRect = new(tabBounds.X + 10, tabBounds.Center.Y - iconSize / 2, iconSize, iconSize);
            UiIconRenderer.DrawCategoryIcon(spriteBatch, pixel, cat.Type, dotRect, cat.AccentColor);

            Vector2 textPos = new(dotRect.Right + 6, tabBounds.Y + (tabHeight - font.LineSpacing) / 2);
                string tabLabel = TruncateToWidth(font, cat.DisplayName, tabBounds.Right - (int)textPos.X - 8);
            spriteBatch.DrawString(font, tabLabel, textPos, isSelectedTab ? UiTheme.TextPrimary : UiTheme.TextSecondary);

            tabX += tabWidthDraw + tabGapDraw;
        }

        // Draw Material Cards Strip
        Rectangle cardsStripBounds = new(
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height);

        // Arrows if needed
        List<MaterialDefinition> currentList = allMaterials;
        int gap = 6;
        Rectangle fullCardsBounds = GetCardsStripBounds(bounds, false);
        int cardWidth = ComputeCardWidth(fullCardsBounds.Height);
        int totalCardsWidthDraw = 0;
        for (int i = 0; i < currentList.Count; i++)
        {
            totalCardsWidthDraw += cardWidth + gap;
        }
        bool overflow = totalCardsWidthDraw - gap > fullCardsBounds.Width;
        cardsStripBounds = GetCardsStripBounds(bounds, overflow);

        if (overflow)
        {
            leftArrowButton.Draw(spriteBatch, font, backdrop, pixel);
            rightArrowButton.Draw(spriteBatch, font, backdrop, pixel);
        }

        int cardX = cardsStripBounds.X - scrollOffset;
        for (int i = 0; i < currentList.Count; i++)
        {
            MaterialDefinition mat = currentList[i];
            Rectangle cardBounds = new(cardX, cardsStripBounds.Y, cardWidth, cardsStripBounds.Height);

            // Clip drawing inside strip bounds
            if (cardBounds.Right >= cardsStripBounds.X && cardBounds.X <= cardsStripBounds.Right)
            {
                bool isSelected = !isTemperatureActive && mat.RuntimeIndex == selectedMaterial;
                bool isHovered = hoveredMaterial == mat.RuntimeIndex;
                Color cardBg = isSelected
                    ? UiTheme.CardActive
                    : isHovered && hoveredMaterialPressed
                        ? UiTheme.CardPressed
                        : isHovered ? UiTheme.CardHover : UiTheme.CardBackground;
                Color cardBorder = isSelected ? UiTheme.CardSelectedBorder : UiTheme.BorderColor;

                spriteBatch.Draw(pixel, cardBounds, cardBg);

                int inset = isSelected ? 3 : 2;
                int labelHeight = Math.Clamp(font.LineSpacing + 8, 22, Math.Max(22, cardBounds.Height / 2));
                Rectangle previewBounds = new(
                    cardBounds.X + inset,
                    cardBounds.Y + inset,
                    cardBounds.Width - inset * 2,
                    Math.Max(1, cardBounds.Height - labelHeight - inset));
                DrawPreview(spriteBatch, pixel, mat, previewBounds);

                Rectangle labelBounds = new(
                    cardBounds.X + inset,
                    previewBounds.Bottom,
                    cardBounds.Width - inset * 2,
                    cardBounds.Bottom - inset - previewBounds.Bottom);
                spriteBatch.Draw(pixel, labelBounds, new Color(13, 16, 21, 245));

                string title = TruncateToWidth(font, mat.Name, labelBounds.Width - 12);
                Vector2 titleSize = font.MeasureString(title);
                Vector2 namePos = new(
                    labelBounds.Center.X - titleSize.X * 0.5f,
                    labelBounds.Center.Y - font.LineSpacing * 0.5f);
                spriteBatch.DrawString(
                    font,
                    title,
                    namePos,
                    isSelected ? UiTheme.TextPrimary : UiTheme.TextSecondary);

                UiIconRenderer.DrawStrokedRectangle(
                    spriteBatch,
                    pixel,
                    cardBounds,
                    isSelected ? 3 : 1,
                    cardBorder);
            }

            cardX += cardWidth + gap;
        }
    }

    private int GetTabHeight() => Math.Clamp(font.LineSpacing + 13, 32, 45);

    private int[] GetTabWidths(int availableWidth, int gap)
    {
        int count = MaterialCategoryResolver.AllCategories.Count;
        int[] widths = new int[count];
        int total = gap * (count - 1);
        for (int index = 0; index < count; index++)
        {
            int desired = (int)MathF.Ceiling(font.MeasureString(MaterialCategoryResolver.AllCategories[index].DisplayName).X) + 48;
            widths[index] = Math.Max(92, desired);
            total += widths[index];
        }

        if (total <= availableWidth)
        {
            return widths;
        }

        int widthWithoutGaps = Math.Max(count, availableWidth - gap * (count - 1));
        int equalWidth = widthWithoutGaps / count;
        for (int index = 0; index < count; index++)
        {
            widths[index] = equalWidth;
        }
        return widths;
    }

    private Rectangle GetCardsStripBounds(Rectangle bounds, bool reserveArrows)
    {
        int inset = reserveArrows ? 40 : 12;
        int top = bounds.Y + 8 + GetTabHeight() + 8;
        return new Rectangle(
            bounds.X + inset,
            top,
            bounds.Width - inset * 2,
            Math.Max(1, bounds.Bottom - top - 10));
    }

    private void DrawPreview(
        SpriteBatch spriteBatch,
        Texture2D pixel,
        MaterialDefinition material,
        Rectangle destination)
    {
        spriteBatch.Draw(pixel, destination, material.Color);

        if (previewCache.TryGetPreview(material.Id, out Texture2D preview))
        {
            Rectangle source = CalculateAspectFillSource(preview, destination);
            spriteBatch.Draw(preview, destination, source, Color.White);
            return;
        }

        spriteBatch.Draw(previewCache.FallbackTexture, destination, Color.White);
        Rectangle swatch = new(destination.X + 6, destination.Bottom - 8, destination.Width - 12, 3);
        spriteBatch.Draw(pixel, swatch, Color.Lerp(material.Color, Color.White, 0.35f));
    }

    internal static Rectangle CalculateAspectFillSource(Texture2D texture, Rectangle destination)
        => CalculateAspectFillSource(texture.Width, texture.Height, destination);

    internal static Rectangle CalculateAspectFillSource(
        int sourceWidth,
        int sourceHeight,
        Rectangle destination)
    {
        float sourceAspect = sourceWidth / (float)Math.Max(1, sourceHeight);
        float destinationAspect = destination.Width / (float)Math.Max(1, destination.Height);

        if (sourceAspect > destinationAspect)
        {
            int width = Math.Max(1, (int)MathF.Round(sourceHeight * destinationAspect));
            return new Rectangle((sourceWidth - width) / 2, 0, width, sourceHeight);
        }

        int height = Math.Max(1, (int)MathF.Round(sourceWidth / destinationAspect));
        return new Rectangle(0, (sourceHeight - height) / 2, sourceWidth, height);
    }

    internal static string TruncateToWidth(SpriteFont font, string text, int maximumWidth)
    {
        if (font.MeasureString(text).X <= maximumWidth)
        {
            return text;
        }

        const string ellipsis = "…";
        int length = text.Length;
        while (length > 0 && font.MeasureString(text[..length] + ellipsis).X > maximumWidth)
        {
            length--;
        }
        return length == 0 ? ellipsis : text[..length] + ellipsis;
    }
}
