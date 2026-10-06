using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Phyxel.Input;

namespace Phyxel.UI;

public sealed class UiPauseMenu
{
    private readonly UiIconButton resume = new("Продолжить") { IconKey = "play" };
    private readonly UiIconButton save = new("Сохранить сцену…") { IconKey = "save" };
    private readonly UiIconButton load = new("Загрузить сцену…") { IconKey = "load" };
    private readonly UiIconButton exit = new("Выйти из игры…") { Danger = true };
    private readonly UiIconButton back = new("Назад");
    private readonly UiIconButton confirmExit = new("Выйти без сохранения") { Danger = true };
    private Rectangle panel;
    private bool confirmingExit;
    private bool suppressUntilRelease;

    public bool IsOpen { get; private set; }
    internal bool ConfirmingExit => confirmingExit;
    internal Rectangle PanelBounds => panel;
    internal Rectangle ResumeBounds => resume.Bounds;
    internal Rectangle SaveBounds => save.Bounds;
    internal Rectangle LoadBounds => load.Bounds;
    internal Rectangle ExitBounds => exit.Bounds;
    internal Rectangle ConfirmExitBounds => confirmExit.Bounds;

    // Returns true even on the closing frame, so that a click cannot paint behind the menu.
    public bool Update(RawInputSnapshot input, Rectangle viewport, SpriteFont font,
        bool fileOperationPending, out UiFrameActions actions)
    {
        actions = default;
        bool wasOpen = IsOpen;
        if (input.EscapePressed)
        {
            if (confirmingExit) confirmingExit = false;
            else IsOpen = !IsOpen;
            suppressUntilRelease = true;
        }
        if (!IsOpen)
        {
            bool blocked = wasOpen || suppressUntilRelease;
            if (!input.LeftDown && !input.RightDown) suppressUntilRelease = false;
            return blocked;
        }
        Layout(viewport, font);
        bool suppress = suppressUntilRelease;
        if (!input.LeftDown && !input.RightDown) suppressUntilRelease = false;
        RawInputSnapshot menuInput = suppress ? input with { LeftPressed = false } : input;
        save.Enabled = load.Enabled = confirmExit.Enabled = !fileOperationPending;
        if (confirmingExit)
        {
            if (back.Update(menuInput)) confirmingExit = false;
            actions = actions with { ExitRequested = confirmExit.Update(menuInput) };
        }
        else
        {
            if (resume.Update(menuInput))
            {
                IsOpen = false;
                suppressUntilRelease = true;
            }
            actions = actions with
            {
                SaveRequested = save.Update(menuInput) || !fileOperationPending && input.SavePressed,
                SaveAsRequested = true,
                LoadRequested = load.Update(menuInput) || !fileOperationPending && input.LoadPressed
            };
            if (exit.Update(menuInput)) confirmingExit = true;
        }
        return true;
    }

    private void Layout(Rectangle viewport, SpriteFont font)
    {
        int padding = 24, gap = 10;
        int height = Math.Max(38, font.LineSpacing + 18);
        int width = Math.Min(viewport.Width - 32, Math.Max(340,
            (int)Math.Max(font.MeasureString(confirmExit.Label).X,
                font.MeasureString("Меню · Esc — продолжить").X) + 2 * padding + 24));
        int panelHeight = height * 4 + gap * 3 + font.LineSpacing * 2 + padding * 3;
        panel = new(viewport.Center.X - width / 2, viewport.Center.Y - panelHeight / 2, width, panelHeight);
        int y = panel.Y + padding + font.LineSpacing * 2;
        resume.Bounds = new(panel.X + padding, y, width - padding * 2, height);
        save.Bounds = new(resume.Bounds.X, resume.Bounds.Bottom + gap, resume.Bounds.Width, height);
        load.Bounds = new(resume.Bounds.X, save.Bounds.Bottom + gap, resume.Bounds.Width, height);
        exit.Bounds = new(resume.Bounds.X, load.Bounds.Bottom + gap, resume.Bounds.Width, height);
        back.Bounds = resume.Bounds;
        confirmExit.Bounds = save.Bounds;
    }

    public void Draw(SpriteBatch batch, SpriteFont font, UiPanelBackdropRenderer renderer,
        Texture2D pixel, UiIconTextureCache icons, Rectangle viewport, string status)
    {
        if (!IsOpen) return;
        batch.Draw(pixel, viewport, new Color(0, 0, 0, 175));
        renderer.Draw(batch, panel, UiTheme.PanelBackground, 10);
        batch.DrawString(font, confirmingExit ? "Выйти из игры?" : "Меню · Esc — продолжить",
            new Vector2(panel.X + 24, panel.Y + 20), UiTheme.TextPrimary);
        if (confirmingExit)
        {
            back.Draw(batch, font, renderer, pixel, icons);
            confirmExit.Draw(batch, font, renderer, pixel, icons);
            batch.DrawString(font, "Несохранённая сцена будет потеряна.",
                new Vector2(panel.X + 24, load.Bounds.Y), UiTheme.TextSecondary,
                0, Vector2.Zero, 0.8f, SpriteEffects.None, 0);
        }
        else
        {
            resume.Draw(batch, font, renderer, pixel, icons);
            save.Draw(batch, font, renderer, pixel, icons);
            load.Draw(batch, font, renderer, pixel, icons);
            exit.Draw(batch, font, renderer, pixel, icons);
        }
        if (!string.IsNullOrEmpty(status))
        {
            string shown = status;
            float scale = 0.7f;
            while (shown.Length > 1 && font.MeasureString(shown + "…").X * scale > panel.Width - 48)
                shown = shown[..^1];
            if (shown.Length < status.Length) shown += "…";
            batch.DrawString(font, shown, new Vector2(panel.X + 24, panel.Bottom - font.LineSpacing - 12),
                UiTheme.TextSecondary, 0, Vector2.Zero, scale, SpriteEffects.None, 0);
        }
    }
}
