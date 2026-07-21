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
    private readonly Dictionary<MaterialCategoryType, List<MaterialDefinition>> categorizedMaterials = new();

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
        hoveredMaterial = null;

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
        int cardWidth = ComputeCardWidth(cardHeight);
        int gap = 6;

        // Compute total cards width for scrolling
        int totalCardsWidth = 0;
        for (int i = 0; i < currentList.Count; i++)
        {
            totalCardsWidth += cardWidth + gap;
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
            Rectangle cardBounds = new(cardX, cardsStripBounds.Y, cardWidth, cardHeight);

            if (cardBounds.Right >= cardsStripBounds.X && cardBounds.X <= cardsStripBounds.Right)
            {
                if (cardBounds.Contains(input.MousePosition))
                {
                    hoveredMaterial = mat.RuntimeIndex;
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
        int cardWidth = ComputeCardWidth(cardsStripBounds.Height);
        int totalCardsWidthDraw = 0;
        for (int i = 0; i < currentList.Count; i++)
        {
            totalCardsWidthDraw += cardWidth + gap;
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
            Rectangle cardBounds = new(cardX, cardsStripBounds.Y, cardWidth, cardsStripBounds.Height);

            // Clip drawing inside strip bounds
            if (cardBounds.Right >= cardsStripBounds.X && cardBounds.X <= cardsStripBounds.Right)
            {
                bool isSelected = !isTemperatureActive && mat.RuntimeIndex == selectedMaterial;
                bool isHovered = hoveredMaterial == mat.RuntimeIndex;
                Color cardBg = isSelected
                    ? UiTheme.CardActive
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

                string title = TruncateToWidth(mat.Name, labelBounds.Width - 12);
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
    {
        float sourceAspect = texture.Width / (float)Math.Max(1, texture.Height);
        float destinationAspect = destination.Width / (float)Math.Max(1, destination.Height);

        if (sourceAspect > destinationAspect)
        {
            int width = Math.Max(1, (int)MathF.Round(texture.Height * destinationAspect));
            return new Rectangle((texture.Width - width) / 2, 0, width, texture.Height);
        }

        int height = Math.Max(1, (int)MathF.Round(texture.Width / destinationAspect));
        return new Rectangle(0, (texture.Height - height) / 2, texture.Width, height);
    }

    private string TruncateToWidth(string text, int maximumWidth)
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
