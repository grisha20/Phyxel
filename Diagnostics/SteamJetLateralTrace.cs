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

public sealed class SteamJetBlockingTrace
{
    private readonly Dictionary<uint, SteamJetBlockingSubstepStatistics[]> substepSamples = [];
    private readonly Dictionary<uint, SteamJetBlockingFrameStatistics[]> frameSamples = [];

    public void Record(uint frame, GpuSimulationResources resources)
    {
        if (substepSamples.ContainsKey(frame) || resources.SteamJetBlockingSubsteps is null ||
            resources.SteamJetBlockingSubstepsStaging is null || resources.SteamJetBlockingFrames is null ||
            resources.SteamJetBlockingFramesStaging is null)
        {
            return;
        }
        DeviceContext context = resources.Context;
        substepSamples.Add(frame, Read(context, resources.SteamJetBlockingSubsteps, resources.SteamJetBlockingSubstepsStaging));
        frameSamples.Add(frame, Read(context, resources.SteamJetBlockingFrames, resources.SteamJetBlockingFramesStaging));
    }

    public string WriteCsv(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "steam-jet-blocking-trace.csv");
        using StreamWriter writer = new(path, false);
        writer.WriteLine("frame,region,substep,rejected,successful,freedNextSubstep,freedFraction,sameMaterial,otherMaterial,solid,steamCellFrames,stalledWithWholeOffset,stalledPerFrame");
        foreach ((uint frame, SteamJetBlockingSubstepStatistics[] steps) in substepSamples)
        {
            SteamJetBlockingFrameStatistics[] frames = frameSamples[frame];
            for (int group = 0; group < 2; group++)
            {
                string region = group == 0 ? "trunk_0_79" : "cap_200_279";
                SteamJetBlockingFrameStatistics frameStats = frames[group];
                for (int substep = 0; substep < 8; substep++)
                {
                    SteamJetBlockingSubstepStatistics v = steps[group * 8 + substep];
                    double fraction = v.Rejected == 0 ? 0.0 : v.FreedNextSubstep / (double)v.Rejected;
                    writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"{frame},{region},{substep + 1},{v.Rejected},{v.Successful},{v.FreedNextSubstep},{fraction:F6},{v.SameMaterial},{v.OtherMaterial},{v.Solid},{frameStats.SteamCellFrames},{frameStats.StalledWithWholeOffset},{frameStats.StalledWithWholeOffset / (double)Math.Max(1, frame):F6}"));
                }
            }
        }
        return path;
    }

    private static T[] Read<T>(DeviceContext context, GpuStructuredBuffer<T> buffer, SharpDX.Direct3D11.Buffer staging) where T : struct
    {
        context.CopyResource(buffer.Buffer, staging);
        DataBox map = context.MapSubresource(staging, 0, MapMode.Read, MapFlags.None);
        int stride = Marshal.SizeOf<T>();
        int count = buffer.Buffer.Description.SizeInBytes / stride;
        T[] values = new T[count];
        for (int index = 0; index < count; index++) values[index] = Marshal.PtrToStructure<T>(IntPtr.Add(map.DataPointer, index * stride));
        context.UnmapSubresource(staging, 0);
        return values;
    }
}