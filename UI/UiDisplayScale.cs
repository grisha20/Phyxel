using System;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework.Graphics;

namespace Phyxel.UI;

public sealed class UiFontSet
{
    public UiFontSet(SpriteFont regular, SpriteFont medium, SpriteFont large)
    {
        Regular = regular;
        Medium = medium;
        Large = large;
    }

    public SpriteFont Regular { get; }
    public SpriteFont Medium { get; }
    public SpriteFont Large { get; }

    public SpriteFont Select(float dpiScale, float layoutScale)
    {
        if (dpiScale >= 1.45f && layoutScale >= 1.1f || layoutScale >= 1.34f)
        {
            return Large;
        }
        if (dpiScale >= 1.2f && layoutScale >= 0.95f || layoutScale >= 1.12f)
        {
            return Medium;
        }
        return Regular;
    }
}

public static class UiDisplayScale
{
    public static float GetDpiScale(IntPtr windowHandle)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == IntPtr.Zero)
        {
            return 1f;
        }

        try
        {
            uint dpi = GetDpiForWindow(windowHandle);
            return dpi == 0 ? 1f : Math.Clamp(dpi / 96f, 1f, 2f);
        }
        catch (EntryPointNotFoundException)
        {
            return 1f;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);
}
