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
/// Reads the cumulative number of steam cells actually created by the held
/// brush. It observes the brush result and is never consumed by simulation.
/// </summary>
public sealed class SteamJetInjectionTrace
{
    private readonly Dictionary<uint, SteamJetInjectionStatistics> samples = [];

    public void Record(uint frame, GpuSimulationResources resources)
    {
        if (samples.ContainsKey(frame)) return;

        DeviceContext context = resources.Context;
        context.CopyResource(resources.SteamJetInjectionStatistics.Buffer, resources.SteamJetInjectionStatisticsStaging);
        DataBox mapping = context.MapSubresource(
            resources.SteamJetInjectionStatisticsStaging, 0, MapMode.Read, MapFlags.None);
        samples.Add(frame, Marshal.PtrToStructure<SteamJetInjectionStatistics>(mapping.DataPointer));
        context.UnmapSubresource(resources.SteamJetInjectionStatisticsStaging, 0);
    }

    public bool TryGet(uint frame, out SteamJetInjectionStatistics statistics) =>
        samples.TryGetValue(frame, out statistics);

    public string WriteCsv(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        using StreamWriter writer = new(path, false);
        writer.WriteLine("frame,createdSteamCells,meanCreatedCellsPerFrame");
        foreach ((uint frame, SteamJetInjectionStatistics statistics) in samples)
        {
            double mean = statistics.CreatedSteamCells / (double)Math.Max(1u, frame);
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{frame},{statistics.CreatedSteamCells},{mean:0.000000}"));
        }
        return path;
    }
}
