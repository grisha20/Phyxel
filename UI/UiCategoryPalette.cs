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
    private readonly SpriteFont font;
    private MaterialCategoryType activeCategory = MaterialCategoryType.Powders;
    private int scrollOffset;
    private readonly Dictionary<MaterialCategoryType, List<MaterialDefinition>> categorizedMaterials = new();

    private readonly UiIconButton leftArrowButton = new("<");
    private readonly UiIconButton rightArrowButton = new(">");

    public UiCategoryPalette(MaterialRegistry registry, SpriteFont font)
    {
        this.registry = registry;
        this.font = font;
        RebuildCategoryCache();
    }

    private int ComputeCardWidth(MaterialDefinition mat)
    {
        // 6px pad + 24px swatch + 8px gap + text + 8px right pad
        int textWidth = (int)font.MeasureString(mat.Name).X;
        return Math.Max(70, 46 + textWidth);
    }

    public void RebuildCategoryCache()
    {
        categorizedMaterials.Clear();
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

        int tabHeight = 30;
        int tabY = bounds.Y + 6;
        int tabX = bounds.X + 12;
        int numCategories = MaterialCategoryResolver.AllCategories.Count;
        int tabGap = 4;
        int tabAvailableWidth = bounds.Width - 24; // 12px padding on each side
        int tabWidth = (tabAvailableWidth - (numCategories - 1) * tabGap) / numCategories;

        // 1. Update Category Tabs
        foreach (MaterialCategoryDefinition cat in MaterialCategoryResolver.AllCategories)
        {
            Rectangle tabBounds = new(tabX, tabY, tabWidth, tabHeight);
            bool isSelectedTab = cat.Type == activeCategory;

            if (input.LeftPressed && tabBounds.Contains(input.MousePosition))
            {
                activeCategory = cat.Type;
                scrollOffset = 0;
            }

            tabX += tabWidth + tabGap;
        }

        // 2. Material Cards Strip
        Rectangle cardsStripBounds = new(
            bounds.X + 36,
            tabY + tabHeight + 6,
            bounds.Width - 72,
            bounds.Height - tabHeight - 18);

        if (cardsStripBounds.Contains(input.MousePosition) && input.WheelDelta != 0)
        {
            scrollOffset = Math.Max(0, scrollOffset - Math.Sign(input.WheelDelta) * 40);
        }

        List<MaterialDefinition> currentList = categorizedMaterials[activeCategory];
        int cardHeight = cardsStripBounds.Height;
        int gap = 6;

        // Compute total cards width for scrolling
        int totalCardsWidth = 0;
        for (int i = 0; i < currentList.Count; i++)
        {
            totalCardsWidth += ComputeCardWidth(currentList[i]) + gap;
        }
        if (currentList.Count > 0) totalCardsWidth -= gap;
        int maxScroll = Math.Max(0, totalCardsWidth - cardsStripBounds.Width);
        scrollOffset = Math.Clamp(scrollOffset, 0, maxScroll);

        // Arrow Buttons
        leftArrowButton.Bounds = new Rectangle(bounds.X + 8, cardsStripBounds.Y, 24, cardHeight);
        rightArrowButton.Bounds = new Rectangle(cardsStripBounds.Right + 4, cardsStripBounds.Y, 24, cardHeight);

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
            int cw = ComputeCardWidth(mat);
            Rectangle cardBounds = new(cardX, cardsStripBounds.Y, cw, cardHeight);

            if (cardBounds.Right >= cardsStripBounds.X && cardBounds.X <= cardsStripBounds.Right)
            {
                if (input.LeftPressed && cardBounds.Contains(input.MousePosition))
                {
                    newlySelectedMaterial = mat.RuntimeIndex;
                }
            }

            cardX += cw + gap;
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

        int tabHeight = 30;
        int tabY = bounds.Y + 6;
        int tabX = bounds.X + 12;
        int numCategoriesDraw = MaterialCategoryResolver.AllCategories.Count;
        int tabGapDraw = 4;
        int tabAvailableWidthDraw = bounds.Width - 24;
        int tabWidthDraw = (tabAvailableWidthDraw - (numCategoriesDraw - 1) * tabGapDraw) / numCategoriesDraw;

        // Draw Category Tabs
        foreach (MaterialCategoryDefinition cat in MaterialCategoryResolver.AllCategories)
        {
            Rectangle tabBounds = new(tabX, tabY, tabWidthDraw, tabHeight);
            bool isSelectedTab = cat.Type == activeCategory;

            Color bgColor = isSelectedTab ? UiTheme.CardActive : UiTheme.CardBackground;
            Color borderColor = isSelectedTab ? cat.AccentColor : UiTheme.BorderColor;

            backdrop.Draw(spriteBatch, tabBounds, 4);
            UiIconRenderer.DrawStrokedRectangle(spriteBatch, pixel, tabBounds, isSelectedTab ? 2 : 1, borderColor);

            // Icon + Label
            Rectangle dotRect = new(tabBounds.X + 8, tabBounds.Y + (tabHeight - 8) / 2, 8, 8);
            spriteBatch.Draw(pixel, dotRect, cat.AccentColor);

            Vector2 textPos = new(dotRect.Right + 6, tabBounds.Y + (tabHeight - font.LineSpacing) / 2);
            spriteBatch.DrawString(font, cat.DisplayName, textPos, isSelectedTab ? UiTheme.TextPrimary : UiTheme.TextSecondary);

            tabX += tabWidthDraw + tabGapDraw;
        }

        // Draw Material Cards Strip
        Rectangle cardsStripBounds = new(
            bounds.X + 36,
            tabY + tabHeight + 6,
            bounds.Width - 72,
            bounds.Height - tabHeight - 18);

        // Arrows if needed
        List<MaterialDefinition> currentList = categorizedMaterials[activeCategory];
        int gap = 6;
        int totalCardsWidthDraw = 0;
        for (int i = 0; i < currentList.Count; i++)
        {
            totalCardsWidthDraw += ComputeCardWidth(currentList[i]) + gap;
        }
        bool overflow = totalCardsWidthDraw > cardsStripBounds.Width + gap;

        if (overflow)
        {
            leftArrowButton.Draw(spriteBatch, font, backdrop, pixel);
            rightArrowButton.Draw(spriteBatch, font, backdrop, pixel);
        }

        int cardX = cardsStripBounds.X - scrollOffset;
        for (int i = 0; i < currentList.Count; i++)
        {
            MaterialDefinition mat = currentList[i];
            int cw = ComputeCardWidth(mat);
            Rectangle cardBounds = new(cardX, cardsStripBounds.Y, cw, cardsStripBounds.Height);

            // Clip drawing inside strip bounds
            if (cardBounds.Right >= cardsStripBounds.X && cardBounds.X <= cardsStripBounds.Right)
            {
                bool isSelected = !isTemperatureActive && mat.RuntimeIndex == selectedMaterial;
                Color cardBg = isSelected ? UiTheme.CardActive : UiTheme.CardBackground;
                Color cardBorder = isSelected ? UiTheme.CardSelectedBorder : UiTheme.BorderColor;

                backdrop.Draw(spriteBatch, cardBounds, 4);
                UiIconRenderer.DrawStrokedRectangle(spriteBatch, pixel, cardBounds, isSelected ? 2 : 1, cardBorder);

                // Swatch / Preview
                Rectangle swatch = new(cardBounds.X + 6, cardBounds.Y + 6, 24, cardBounds.Height - 12);
                spriteBatch.Draw(pixel, swatch, mat.Color);
                UiIconRenderer.DrawStrokedRectangle(spriteBatch, pixel, swatch, 1, UiTheme.BorderColor);

                // Title
                Vector2 namePos = new(swatch.Right + 8, cardBounds.Y + (cardBounds.Height - font.LineSpacing) / 2);
                spriteBatch.DrawString(font, mat.Name, namePos, isSelected ? UiTheme.TextPrimary : UiTheme.TextSecondary);
            }

            cardX += cw + gap;
        }
    }
}
