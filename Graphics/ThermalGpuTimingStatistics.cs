namespace Phyxel.Graphics;

public readonly record struct ThermalGpuTimingStatistics(
    int Samples,
    double AverageMilliseconds,
    double MinimumMilliseconds,
    double MaximumMilliseconds);
