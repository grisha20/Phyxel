using System;

namespace Phyxel.Graphics;

public sealed class FixedStepGasScheduler
{
    private double accumulator;

    public ulong TotalTicks { get; private set; }

    public int Advance(double elapsedSeconds, bool paused, bool active)
    {
        if (paused || !active)
        {
            return 0;
        }

        double maximumAccumulation =
            SimulationDispatchCoordinator.FixedGasStep *
            SimulationDispatchCoordinator.MaximumGasTicksPerFrame;
        accumulator = Math.Min(
            accumulator + Math.Clamp(elapsedSeconds, 0, maximumAccumulation),
            maximumAccumulation);
        int ticks = 0;
        while (accumulator + 1e-9 >= SimulationDispatchCoordinator.FixedGasStep &&
            ticks < SimulationDispatchCoordinator.MaximumGasTicksPerFrame)
        {
            accumulator -= SimulationDispatchCoordinator.FixedGasStep;
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
