using System;

namespace Phyxel.Core;

public sealed class SimulationSettings
{
    public const int NativeWidth = 1920;
    public const int NativeHeight = 1080;
    public const int MaximumBrushCommands = 256;

    /// <summary>
    /// Ширина одной клетки поля воздуха в клетках симуляции. Зеркало
    /// <c>AirCellSize</c> из PhysicsShared.hlsli — менять только вместе с ним.
    /// The Powder Toy использует CELL = 4 по той же причине: попиксельное
    /// решение давления одновременно избыточно и слишком медленно.
    /// </summary>
    public const int AirCellSize = 4;
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

    /// <summary>
    /// Поле воздуха: давление и скорость на грубой сетке. Пламя берёт из него
    /// снос, поэтому по умолчанию включено — с выключенным полем огонь теряет
    /// боковое движение и снова разваливается на независимые искры.
    /// </summary>
    public bool AirSimulation { get; set; } = true;

    /// <summary>
    /// Диагностический режим: рисовать поле воздуха вместо сцены. Красный —
    /// поток вверх, синий — вниз, зелёный — вбок, яркость по модулю скорости.
    /// </summary>
    public bool ShowAirField { get; set; }

    /// <summary>
    /// Режим сравнения с TPT Nothing Display: только собственный цвет каждой
    /// занятой клетки, без свечения, сглаживания, дымки и прочих эффектов
    /// композиции. Это настройка рендера, не физики.
    /// </summary>
    public bool RenderWithoutEffects { get; set; }

    /// <summary>
    /// Открытые границы: всё, что доходит до левого, правого или верхнего края,
    /// исчезает. Пол остаётся сплошным, иначе строить будет не на чем.
    /// В игре включено — иначе сцена просто заполняется огнём и дымом, и
    /// оценить форму пламени невозможно. Acceptance-харнесс выключает: его
    /// сцены калиброваны под замкнутый мир и развалятся, если стенки исчезнут.
    /// </summary>
    public bool OpenBoundaries { get; set; } = true;

    public void ApplyScale(float requestedScale)
    {
        Scale = Math.Clamp(requestedScale, 0.25f, 1f);
        Width = Math.Max(320, (int)MathF.Round(NativeWidth * Scale));
        Height = Math.Max(180, (int)MathF.Round(NativeHeight * Scale));
    }
}
