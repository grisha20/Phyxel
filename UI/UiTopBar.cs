using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Phyxel.Input;

namespace Phyxel.UI;

public sealed class UiTopBar
{
    private SpriteFont font;
    private readonly UiIconButton saveButton = new("Сохранить");
    private readonly UiIconButton loadButton = new("Загрузить");
    private readonly UiIconButton pauseButton = new("Пауза");
    private readonly UiIconButton settingsButton = new("Настройки");

    public UiTopBar(SpriteFont font)
    {
        this.font = font;
        settingsButton.Enabled = false;
    }

    public SpriteFont Font { set => font = value; }

    private readonly Queue<float> frameTimeHistory = new();
    private const int MaxHistoryCount = 40;

    public bool SaveRequested { get; private set; }
    public bool LoadRequested { get; private set; }
    public bool SettingsRequested { get; private set; }

    public void Update(RawInputSnapshot input, Rectangle bounds, ref bool paused)
    {
        SaveRequested = false;
        LoadRequested = false;
        SettingsRequested = false;

        int buttonPadding = 24;
        int buttonHeight = bounds.Height < 44 ? 26 : 30;
        int startX = bounds.X + 140;
        int startY = bounds.Y + (bounds.Height - buttonHeight) / 2;
        int gap = 6;

        int saveW = (int)font.MeasureString(saveButton.Label).X + buttonPadding;
        int loadW = (int)font.MeasureString(loadButton.Label).X + buttonPadding;
        int pauseW = (int)font.MeasureString(pauseButton.Label).X + buttonPadding;
        int settingsW = (int)font.MeasureString(settingsButton.Label).X + buttonPadding;

        saveButton.Bounds = new Rectangle(startX, startY, saveW, buttonHeight);
        loadButton.Bounds = new Rectangle(saveButton.Bounds.Right + gap, startY, loadW, buttonHeight);
        pauseButton.Bounds = new Rectangle(loadButton.Bounds.Right + gap, startY, pauseW, buttonHeight);
        settingsButton.Bounds = new Rectangle(pauseButton.Bounds.Right + gap, startY, settingsW, buttonHeight);

        pauseButton.Active = paused;
        pauseButton.Label = paused ? "Продолжить" : "Пауза";

        if (saveButton.Update(input)) SaveRequested = true;
        if (loadButton.Update(input)) LoadRequested = true;
        if (settingsButton.Update(input)) SettingsRequested = true;
        if (pauseButton.Update(input))
        {
            paused = !paused;
        }
    }

    public void RecordFrameTime(float deltaSeconds)
    {
        frameTimeHistory.Enqueue(deltaSeconds * 1000f);
        while (frameTimeHistory.Count > MaxHistoryCount)
        {
            frameTimeHistory.Dequeue();
        }
    }

    public void Draw(
        SpriteBatch spriteBatch,
        SpriteFont font,
        UiPanelBackdropRenderer backdrop,
        Texture2D pixel,
        Rectangle bounds,
        double displayedFps,
        bool isPaused)
    {
        backdrop.Draw(spriteBatch, bounds, 0);

        // Logo & Title
        Rectangle logoBounds = new(bounds.X + 12, bounds.Y + (bounds.Height - 24) / 2, 24, 24);
        UiIconRenderer.DrawPhyxelLogo(spriteBatch, pixel, logoBounds, UiTheme.PrimaryAccent);

        Vector2 titlePos = new(logoBounds.Right + 10, bounds.Y + (bounds.Height - font.LineSpacing) / 2);
        spriteBatch.DrawString(font, "PHYXEL", titlePos, UiTheme.TextPrimary);

        // Buttons
        saveButton.Draw(spriteBatch, font, backdrop, pixel);
        loadButton.Draw(spriteBatch, font, backdrop, pixel);
        pauseButton.Draw(spriteBatch, font, backdrop, pixel);
        settingsButton.Draw(spriteBatch, font, backdrop, pixel);

        // Performance info on right
        float frameMs = displayedFps > 0 ? (float)(1000.0 / displayedFps) : 16.6f;
        string fpsText = $"{displayedFps:0} FPS";
        string msText = $"{frameMs:0.0} ms";

        Vector2 msSize = font.MeasureString(msText);
        Vector2 fpsSize = font.MeasureString(fpsText);

        int rightX = bounds.Right - 16;
        Vector2 msPos = new(rightX - msSize.X, bounds.Y + (bounds.Height - font.LineSpacing) / 2);
        spriteBatch.DrawString(font, msText, msPos, UiTheme.TextSecondary);

        Vector2 fpsPos = new(msPos.X - fpsSize.X - 16, msPos.Y);
        spriteBatch.DrawString(font, fpsText, fpsPos, UiTheme.StatusGreen);

        // Mini sparkline graph
        int graphWidth = 60;
        int graphHeight = bounds.Height - 16;
        int graphX = (int)fpsPos.X - graphWidth - 16;
        int graphY = bounds.Y + 8;

        if (graphX > settingsButton.Bounds.Right + 20)
        {
            Rectangle graphBox = new(graphX, graphY, graphWidth, graphHeight);
            UiIconRenderer.DrawStrokedRectangle(spriteBatch, pixel, graphBox, 1, UiTheme.BorderColor);

            int index = 0;
            Vector2? prevPoint = null;
            foreach (float ms in frameTimeHistory)
            {
                float normalized = Math.Clamp(ms / 33.3f, 0f, 1f);
                float px = graphBox.X + (index / (float)MaxHistoryCount) * graphWidth;
                float py = graphBox.Bottom - normalized * graphHeight;
                Vector2 currentPoint = new(px, py);

                if (prevPoint.HasValue)
                {
                    UiIconRenderer.DrawThickLine(spriteBatch, pixel, prevPoint.Value, currentPoint, 1, UiTheme.StatusGreen * 0.7f);
                }
                prevPoint = currentPoint;
                index++;
            }
        }
    }
}
