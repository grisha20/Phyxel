using System;

namespace Phyxel.Core;

public sealed class SimulationSettings
{
    public const int NativeWidth = 1920;
    public const int NativeHeight = 1080;
    public const int MaximumBrushCommands = 256;
    public const float DefaultScale = 0.25f;
    public int Width { get; set; } = NativeWidth / 4;
    public int Height { get; set; } = NativeHeight / 4;
    public float Scale { get; set; } = DefaultScale;
    public float Gravity { get; set; } = 980f;
    public int BrushRadius { get; set; } = 18;
    public float SpawnDensity { get; set; } = 0.82f;
    public bool Paused { get; set; }
    public bool SolidGravity { get; set; }
    public bool HydraulicPressure { get; set; }

    public void ApplyScale(float requestedScale)
    {
        Scale = Math.Clamp(requestedScale, 0.25f, 1f);
        Width = Math.Max(320, (int)MathF.Round(NativeWidth * Scale));
        Height = Math.Max(180, (int)MathF.Round(NativeHeight * Scale));
    }
}
