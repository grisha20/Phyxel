using System;
using System.Diagnostics;
using System.IO;
using Phyxel.Core;
using Phyxel.Graphics;

namespace Phyxel.Diagnostics;

// Opt-in observation of the real update loop: rendered FPS alone cannot tell
// whether physical ticks or the velocity of the carrier changed.
internal sealed class SimulationClockTrace : IDisposable
{
    private readonly string? path = Environment.GetEnvironmentVariable("PHYXEL_SIMULATION_CLOCK_TRACE");
    private StreamWriter? writer;
    private readonly Stopwatch clock = new();
    private double seconds;
    private double nextSample;
    private ulong updates;
    private readonly double stopAfterSeconds = double.TryParse(
        Environment.GetEnvironmentVariable("PHYXEL_CLOCK_TRACE_SECONDS"), out double stop) && stop is >= 1 and <= 300 ? stop : 0;

    public bool ExitRequested => writer is not null && stopAfterSeconds > 0 && clock.Elapsed.TotalSeconds >= stopAfterSeconds;

    public void Observe(float elapsedSeconds, SimulationSettings settings, SimulationDispatchCoordinator coordinator)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (writer is null)
        {
            string fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            writer = new StreamWriter(fullPath);
            writer.WriteLine("wallSeconds,simulationSeconds,updates,mode,paused,airTicks,gasMotionTicks,thermalTicks,combustionTicks,combustionGpuMs,thermalGpuMs,airGpuMs,airHeatGpuMs,gasMotionGpuMs,airInjectGpuMs,reactionGatherGpuMs,airProjectionGpuMs");
            clock.Start();
        }
        seconds += elapsedSeconds;
        updates++;
        double wall = clock.Elapsed.TotalSeconds;
        if (wall < nextSample) return;
        writer.WriteLine(FormattableString.Invariant($"{wall:F6},{seconds:F6},{updates},{settings.Mode},{settings.Paused},{coordinator.AirTicks},{coordinator.GasMotionTicks},{coordinator.ThermalTicks},{coordinator.CombustionDispatches},{coordinator.CombustionGpuTiming.AverageMilliseconds:F6},{coordinator.ThermalGpuTiming.AverageMilliseconds:F6},{coordinator.AirGpuTiming.AverageMilliseconds:F6},{coordinator.AirHeatGpuTiming.AverageMilliseconds:F6},{coordinator.GasMotionGpuTiming.AverageMilliseconds:F6},{coordinator.AirInjectGpuTiming.AverageMilliseconds:F6},{coordinator.ReactionGatherGpuTiming.AverageMilliseconds:F6},{coordinator.AirProjectionGpuTiming.AverageMilliseconds:F6}"));
        writer.Flush();
        nextSample = wall + 1;
    }

    public void Dispose() => writer?.Dispose();
}
