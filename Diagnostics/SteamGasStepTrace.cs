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
/// Reads the successful WTRV moves from one fixed gas tick at each requested
/// steam-puff frame. This diagnostic counter is never read by the simulation.
/// </summary>
public sealed class SteamGasStepTrace
{
    private readonly Dictionary<uint, SteamGasStepStatistics> samples = [];

    public bool HasSample(uint frame) => samples.ContainsKey(frame);

    public void Record(uint frame, GpuSimulationResources resources)
    {
        if (samples.ContainsKey(frame)) return;

        DeviceContext context = resources.Context;
        context.CopyResource(resources.SteamGasStepStatistics.Buffer, resources.SteamGasStepStatisticsStaging);
        DataBox mapping = context.MapSubresource(
            resources.SteamGasStepStatisticsStaging, 0, MapMode.Read, MapFlags.None);
        samples.Add(frame, Marshal.PtrToStructure<SteamGasStepStatistics>(mapping.DataPointer));
        context.UnmapSubresource(resources.SteamGasStepStatisticsStaging, 0);
    }

    public bool TryGet(uint frame, out SteamGasStepStatistics statistics) =>
        samples.TryGetValue(frame, out statistics);

    public string WriteCsv(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        using StreamWriter writer = new(path, false);
        writer.WriteLine("frame,upwardSteps,downwardSteps,noYSteps,leftSteps,rightSteps,noXSteps");
        foreach ((uint frame, SteamGasStepStatistics statistics) in samples)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{frame},{statistics.UpwardSteps},{statistics.DownwardSteps},{statistics.NoYSteps},{statistics.LeftSteps},{statistics.RightSteps},{statistics.NoXSteps}"));
        }
        return path;
    }
}
