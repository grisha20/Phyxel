using System;
using System.Diagnostics;

namespace Phyxel.Diagnostics;

public sealed class RestingPerformanceVerifier
{
    private readonly string scenario;
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private double sleepingSince = -1;
    private double windowStart;
    private int windowFrames;
    private int measuredFrames;
    private double minimumFramesPerSecond = double.MaxValue;
    private ulong baselineDispatches;
    private bool measuring;
    private bool completed;
    private double movingWindowStart = 2;
    private int movingWindowFrames;
    private double movingMinimumFramesPerSecond = double.MaxValue;
    private double movingMinimumAt;

    public RestingPerformanceVerifier(string scenario)
    {
        this.scenario = scenario;
    }

    public bool RecordFrame(
        bool sleeping,
        ulong physicsDispatches,
        out bool passed,
        out string report)
    {
        passed = false;
        report = string.Empty;
        if (completed)
        {
            return false;
        }

        double elapsed = stopwatch.Elapsed.TotalSeconds;
        if (!sleeping)
        {
            sleepingSince = -1;
            if (elapsed >= 2)
            {
                movingWindowFrames++;
                double movingWindowDuration = elapsed - movingWindowStart;
                if (movingWindowDuration >= 0.5)
                {
                    double movingFramesPerSecond = movingWindowFrames / movingWindowDuration;
                    if (movingFramesPerSecond < movingMinimumFramesPerSecond)
                    {
                        movingMinimumFramesPerSecond = movingFramesPerSecond;
                        movingMinimumAt = elapsed;
                    }
                    movingWindowStart = elapsed;
                    movingWindowFrames = 0;
                }
            }
            return false;
        }

        sleepingSince = sleepingSince < 0 ? elapsed : sleepingSince;
        if (!measuring && elapsed - sleepingSince >= 0.75)
        {
            measuring = true;
            windowStart = elapsed;
            baselineDispatches = physicsDispatches;
        }

        if (!measuring)
        {
            return false;
        }

        windowFrames++;
        measuredFrames++;
        double measurementDuration = elapsed - (sleepingSince + 0.75);
        double windowDuration = elapsed - windowStart;
        if (windowDuration >= 0.5)
        {
            minimumFramesPerSecond = Math.Min(minimumFramesPerSecond, windowFrames / windowDuration);
            windowStart = elapsed;
            windowFrames = 0;
        }

        if (measurementDuration < 5)
        {
            return false;
        }

        completed = true;
        double averageFramesPerSecond = measuredFrames / measurementDuration;
        ulong restingDispatches = physicsDispatches - baselineDispatches;
        passed = movingMinimumFramesPerSecond >= 55 && minimumFramesPerSecond >= 55 && restingDispatches == 0;
        string geometry = scenario == "RestSand" ? " pileHeight=300" : " layerWidth=500 layerHeight=20";
        report = $"PHYXEL_REST_PERFORMANCE scenario={scenario}{geometry} movingMinimumFps={movingMinimumFramesPerSecond:0.0} movingMinimumAt={movingMinimumAt:0.00} restingAverageFps={averageFramesPerSecond:0.0} restingMinimumFps={minimumFramesPerSecond:0.0} restingPhysicsDispatches={restingDispatches} sleepingAt={sleepingSince:0.00}";
        return true;
    }
}
