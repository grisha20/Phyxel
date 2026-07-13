using System;

namespace Phyxel.Core;

public sealed class SimulationSettings
{
    public const int NativeWidth = 1920;
    public const int NativeHeight = 1080;
    public const int MaximumBrushCommands = 256;
    public int Width { get; set; } = NativeWidth;
    public int Height { get; set; } = NativeHeight;
    public float Scale { get; set; } = 1f;
    public float Gravity { get; set; } = 980f;
    public int SolverIterations { get; set; } = 4;
    public int BrushRadius { get; set; } = 18;
    public float SpawnDensity { get; set; } = 0.82f;
    public bool Paused { get; set; }
    public bool StressView { get; set; }

    public void ApplyScale(float requestedScale)
    {
        Scale = Math.Clamp(requestedScale, 0.25f, 1f);
        Width = Math.Max(320, (int)MathF.Round(NativeWidth * Scale));
        Height = Math.Max(180, (int)MathF.Round(NativeHeight * Scale));
    }
}
