using System;
using System.Diagnostics;

namespace Phyxel.Diagnostics;

public sealed class StartupPerformanceVerifier
{
    private readonly double durationSeconds;
    private readonly double requiredFramesPerSecond;
    private readonly string metricPrefix;
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private double windowStart;
    private int windowFrames;
    private int totalFrames;
    private double minimumFramesPerSecond = double.MaxValue;
    private bool completed;

    public StartupPerformanceVerifier(
        double durationSeconds = 10,
        double requiredFramesPerSecond = 55,
        string metricPrefix = "PHYXEL_STARTUP_PERFORMANCE")
    {
        this.durationSeconds = durationSeconds;
        this.requiredFramesPerSecond = requiredFramesPerSecond;
        this.metricPrefix = metricPrefix;
    }

    public bool RecordFrame(out bool passed, out string report)
    {
        passed = false;
        report = string.Empty;
        if (completed)
        {
            return false;
        }

        windowFrames++;
        totalFrames++;
        double elapsed = stopwatch.Elapsed.TotalSeconds;
        double windowDuration = elapsed - windowStart;
        if (windowDuration >= 0.5)
        {
            double framesPerSecond = windowFrames / windowDuration;
            minimumFramesPerSecond = Math.Min(minimumFramesPerSecond, framesPerSecond);
            windowStart = elapsed;
            windowFrames = 0;
        }

        if (elapsed < durationSeconds)
        {
            return false;
        }

        completed = true;
        double averageFramesPerSecond = totalFrames / elapsed;
        passed = minimumFramesPerSecond >= requiredFramesPerSecond;
        report = $"{metricPrefix} seconds={elapsed:0.00} averageFps={averageFramesPerSecond:0.0} minimumWindowFps={minimumFramesPerSecond:0.0}";
        return true;
    }
}
