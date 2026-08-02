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
/// Diagnostics-only synchronous readback of three coarse Air cells.  It never
/// writes an air resource and is enabled solely by the fire pressure scenarios.
/// </summary>
internal sealed class AirPressureTimeSeries
{
    private const uint SampleFrames = 300;
    private readonly List<Sample> samples = [];

    public void Record(uint frame, GpuSimulationResources resources, int airY)
    {
        if (frame >= SampleFrames || samples.Count >= SampleFrames)
        {
            return;
        }

        int x52 = Math.Clamp(52, 0, resources.AirWidth - 1);
        int x60 = Math.Clamp(60, 0, resources.AirWidth - 1);
        int x68 = Math.Clamp(68, 0, resources.AirWidth - 1);
        int y = Math.Clamp(airY, 0, resources.AirHeight - 1);
        resources.Context.CopyResource(resources.Air.Buffer, resources.AirStaging);
        resources.Context.Flush();
        DataBox mapping = resources.Context.MapSubresource(
            resources.AirStaging,
            0,
            MapMode.Read,
            MapFlags.None);
        try
        {
            samples.Add(new Sample(
                frame,
                ReadPressure(mapping, y * resources.AirWidth + x52),
                ReadPressure(mapping, y * resources.AirWidth + x60),
                ReadPressure(mapping, y * resources.AirWidth + x68)));
        }
        finally
        {
            resources.Context.UnmapSubresource(resources.AirStaging, 0);
        }
    }

    public string WriteCsv(string artifactDirectory, string name)
    {
        Directory.CreateDirectory(artifactDirectory);
        string path = Path.Combine(artifactDirectory, name);
        using StreamWriter writer = new(path, false);
        writer.WriteLine("frame,pressure_x52,pressure_x60,pressure_x68");
        foreach (Sample sample in samples)
        {
            writer.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{sample.Frame},{sample.X52:F6},{sample.X60:F6},{sample.X68:F6}"));
        }
        return path;
    }

    private static float ReadPressure(DataBox mapping, int airIndex)
    {
        int stride = Marshal.SizeOf<AirCell>();
        IntPtr address = IntPtr.Add(mapping.DataPointer, checked(airIndex * stride));
        return Marshal.PtrToStructure<AirCell>(address).Pressure;
    }

    private readonly record struct Sample(uint Frame, float X52, float X60, float X68);
}
