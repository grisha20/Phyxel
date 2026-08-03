using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Phyxel.Graphics;
using Phyxel.Physics;
using SharpDX;
using SharpDX.Direct3D11;

namespace Phyxel.Diagnostics;

/// <summary>
/// Acceptance-only synchronous readback of the fallback ladder. It is enabled
/// exclusively by PHYXEL_GAS_BYPASS_TRACE=1, so gameplay never pays for it.
/// </summary>
public sealed class GasObstacleBypassTrace
{
    private readonly List<(uint Frame, GasObstacleBypassStatistics Counters)> samples = [];

    public int Count => samples.Count;

    public void Record(uint frame, GpuSimulationResources resources)
    {
        DeviceContext context = resources.Context;
        context.CopyResource(
            resources.GasObstacleBypassStatistics.Buffer,
            resources.GasObstacleBypassStatisticsStaging);
        DataBox mapping = context.MapSubresource(
            resources.GasObstacleBypassStatisticsStaging,
            0,
            MapMode.Read,
            MapFlags.None);
        GasObstacleBypassStatistics counters =
            Marshal.PtrToStructure<GasObstacleBypassStatistics>(mapping.DataPointer);
        context.UnmapSubresource(resources.GasObstacleBypassStatisticsStaging, 0);
        samples.Add((frame, counters));
    }

    public string WriteCsv(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        using StreamWriter writer = new(path, false);
        writer.WriteLine("frame,blocked,xOnly,yOnly,diagonal,stayed");
        foreach ((uint frame, GasObstacleBypassStatistics counters) in samples)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{frame},{counters.Blocked},{counters.XOnly},{counters.YOnly},{counters.Diagonal},{counters.Stayed}"));
        }
        return path;
    }

    public GasObstacleBypassStatistics Sum()
    {
        GasObstacleBypassStatistics total = default;
        foreach ((_, GasObstacleBypassStatistics counters) in samples)
        {
            total.Blocked += counters.Blocked;
            total.XOnly += counters.XOnly;
            total.YOnly += counters.YOnly;
            total.Diagonal += counters.Diagonal;
            total.Stayed += counters.Stayed;
        }
        return total;
    }
}
