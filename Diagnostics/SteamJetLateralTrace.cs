using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Phyxel.Graphics;
using Phyxel.Physics;
using SharpDX;
using SharpDX.Direct3D11;

namespace Phyxel.Diagnostics;

public sealed class SteamJetLateralTrace
{
    private readonly Dictionary<uint, SteamJetLateralBandStatistics[]> samples = [];

    public void Record(uint frame, GpuSimulationResources resources)
    {
        if (samples.ContainsKey(frame) || resources.SteamJetLateralBands is null || resources.SteamJetLateralBandsStaging is null) return;
        DeviceContext context = resources.Context;
        context.CopyResource(resources.SteamJetLateralBands.Buffer, resources.SteamJetLateralBandsStaging);
        DataBox map = context.MapSubresource(resources.SteamJetLateralBandsStaging, 0, MapMode.Read, MapFlags.None);
        int count = resources.SteamJetLateralBands.Buffer.Description.SizeInBytes /
            Marshal.SizeOf<SteamJetLateralBandStatistics>();
        SteamJetLateralBandStatistics[] values = new SteamJetLateralBandStatistics[count];
        int stride = Marshal.SizeOf<SteamJetLateralBandStatistics>();
        for (int i = 0; i < count; i++) values[i] = Marshal.PtrToStructure<SteamJetLateralBandStatistics>(IntPtr.Add(map.DataPointer, i * stride));
        context.UnmapSubresource(resources.SteamJetLateralBandsStaging, 0);
        samples.Add(frame, values);
    }

    public string WriteCsv(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "steam-jet-lateral-steps.csv");
        using StreamWriter writer = new(path, false);
        writer.WriteLine("frame,heightAboveSourceStart,heightAboveSourceEnd,leftSteps,rightSteps,rejectedLeft,rejectedRight");
        foreach ((uint frame, SteamJetLateralBandStatistics[] bands) in samples)
        for (int band = 0; band < bands.Length; band++)
        {
            SteamJetLateralBandStatistics v = bands[band];
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{frame},{band * 20},{band * 20 + 19},{v.LeftSteps},{v.RightSteps},{v.RejectedLeft},{v.RejectedRight}"));
        }
        return path;
    }
}
