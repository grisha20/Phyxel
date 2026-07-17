using System;

namespace Phyxel.Graphics;

public sealed class FixedStepThermalScheduler
{
    private const double FixedStepSeconds = 0.05;
    private double accumulator;

    public ulong TotalTicks { get; private set; }

    public int Advance(double elapsedSeconds, bool paused, bool active)
    {
        if (paused || !active)
        {
            return 0;
        }

        double maximumAccumulation =
            FixedStepSeconds *
            SimulationDispatchCoordinator.MaximumThermalTicksPerFrame;
        accumulator = Math.Min(
            accumulator + Math.Clamp(elapsedSeconds, 0, maximumAccumulation),
            maximumAccumulation);
        int ticks = 0;
        while (accumulator + 1e-9 >= FixedStepSeconds &&
            ticks < SimulationDispatchCoordinator.MaximumThermalTicksPerFrame)
        {
            accumulator -= FixedStepSeconds;
            TotalTicks++;
            ticks++;
        }
        return ticks;
    }

    public void Reset()
    {
        accumulator = 0;
        TotalTicks = 0;
    }
}
