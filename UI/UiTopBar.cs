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
        saveButton.IconKey = "save";
        loadButton.IconKey = "load";
        pauseButton.IconKey = "pause";
        settingsButton.IconKey = "settings";
    }

    public SpriteFont Font { set => font = value; }

    private readonly Queue<float> frameTimeHistory = new();
    private const int MaxHistoryCount = 40;

    public bool SaveRequested { get; private set; }
    public bool LoadRequested { get; private set; }
    public bool SettingsRequested { get; private set; }
    internal bool SettingsEnabled => settingsButton.Enabled;
    internal Rectangle SettingsButtonBounds => settingsButton.Bounds;
    internal Rectangle SaveButtonBounds => saveButton.Bounds;
    internal Rectangle LoadButtonBounds => loadButton.Bounds;
    internal Rectangle PauseButtonBounds => pauseButton.Bounds;
    internal IReadOnlyList<UiIconButton> Buttons => [saveButton, loadButton, pauseButton, settingsButton];

    public void Update(RawInputSnapshot input, Rectangle bounds, ref bool paused)
    {
        SaveRequested = false;
        LoadRequested = false;
        SettingsRequested = false;

        pauseButton.Active = paused;
        pauseButton.Label = paused ? "Продолжить" : "Пауза";
        pauseButton.IconKey = paused ? "play" : "pause";

        int maximumButtonHeight = Math.Max(28, bounds.Height - 10);
        int buttonHeight = Math.Clamp(font.LineSpacing + 12, Math.Min(32, maximumButtonHeight), maximumButtonHeight);
        int startX = bounds.X + Math.Max(164, (int)font.MeasureString("PHYXEL").X + 72);
        int startY = bounds.Y + (bounds.Height - buttonHeight) / 2;
        int gap = Math.Max(6, bounds.Height / 10);

        int saveW = MeasureButtonWidth(saveButton);
        int loadW = MeasureButtonWidth(loadButton);
        int pauseW = MeasureButtonWidth(pauseButton);
        int settingsW = MeasureButtonWidth(settingsButton);

        saveButton.Bounds = new Rectangle(startX, startY, saveW, buttonHeight);
        loadButton.Bounds = new Rectangle(saveButton.Bounds.Right + gap, startY, loadW, buttonHeight);
        pauseButton.Bounds = new Rectangle(loadButton.Bounds.Right + gap, startY, pauseW, buttonHeight);
        settingsButton.Bounds = new Rectangle(pauseButton.Bounds.Right + gap, startY, settingsW, buttonHeight);

        if (saveButton.Update(input)) SaveRequested = true;
        if (loadButton.Update(input)) LoadRequested = true;
        if (settingsButton.Update(input)) SettingsRequested = true;
        if (pauseButton.Update(input))
        {
            paused = !paused;
        }
    }

    private int MeasureButtonWidth(UiIconButton button) =>
        Math.Max(88, (int)MathF.Ceiling(font.MeasureString(button.Label).X) + 52);

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

        spriteBatch.Draw(pixel, new Rectangle(bounds.X, bounds.Bottom - 1, bounds.Width, 1), UiTheme.BorderColor);

        // Logo & Title
        int logoSize = Math.Clamp(bounds.Height - 18, 26, 38);
        Rectangle logoBounds = new(bounds.X + 16, bounds.Center.Y - logoSize / 2, logoSize, logoSize);
        UiIconRenderer.DrawPhyxelLogo(spriteBatch, pixel, logoBounds, new Color(65, 178, 230));

        Vector2 titlePos = new(logoBounds.Right + 10, bounds.Y + (bounds.Height - font.LineSpacing) / 2f);
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

        int rightX = bounds.Right - 20;
        Vector2 msPos = new(rightX - msSize.X, bounds.Y + (bounds.Height - font.LineSpacing) / 2);
        spriteBatch.DrawString(font, msText, msPos, UiTheme.TextSecondary);

        Vector2 fpsPos = new(msPos.X - fpsSize.X - 16, msPos.Y);
        spriteBatch.DrawString(font, fpsText, fpsPos, UiTheme.StatusGreen);

        // Mini sparkline graph
        int graphWidth = Math.Clamp(bounds.Width / 32, 64, 92);
        int graphHeight = Math.Max(20, bounds.Height - 20);
        int graphX = (int)fpsPos.X - graphWidth - 16;
        int graphY = bounds.Center.Y - graphHeight / 2;

        if (graphX > settingsButton.Bounds.Right + 20)
        {
            Rectangle graphBox = new(graphX, graphY, graphWidth, graphHeight);
            spriteBatch.Draw(pixel, graphBox, UiTheme.FieldBackground);
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
