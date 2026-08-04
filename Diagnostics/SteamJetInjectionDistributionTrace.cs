using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Phyxel.Graphics;
using Phyxel.Physics;
using SharpDX;
using SharpDX.Direct3D11;
using Buffer = SharpDX.Direct3D11.Buffer;

namespace Phyxel.Diagnostics;

/// <summary>
/// Readback for the explicitly enabled post-brush source-distribution observer.
/// The buffer contains only newly created cells and never feeds a solver pass.
/// </summary>
public sealed class SteamJetInjectionDistributionTrace
{
    private SteamJetInjectionDistributionFrame[] frames = [];

    public bool HasSamples => frames.Length > 0;

    public void Record(GpuSimulationResources resources)
    {
        if (HasSamples || resources.SteamJetInjectionDistribution is null ||
            resources.SteamJetInjectionDistributionStaging is null) return;

        DeviceContext context = resources.Context;
        Buffer source = resources.SteamJetInjectionDistribution.Buffer;
        context.CopyResource(source, resources.SteamJetInjectionDistributionStaging);
        DataBox mapping = context.MapSubresource(resources.SteamJetInjectionDistributionStaging, 0, MapMode.Read, MapFlags.None);
        try
        {
            byte[] bytes = new byte[source.Description.SizeInBytes];
            Marshal.Copy(mapping.DataPointer, bytes, 0, bytes.Length);
            frames = MemoryMarshal.Cast<byte, SteamJetInjectionDistributionFrame>(bytes.AsSpan()).ToArray();
        }
        finally { context.UnmapSubresource(resources.SteamJetInjectionDistributionStaging, 0); }
    }

    public string WriteCsv(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "steam-jet-source-distribution.csv");
        using StreamWriter writer = new(path, false);
        writer.WriteLine("frame,createdSteamCells,meanOffsetX,sigmaX,ring0_1,ring2_3,ring4_5,ring6_7,ring8_9,ring10Plus");
        for (int index = 0; index < frames.Length; index++)
        {
            SteamJetInjectionDistributionFrame sample = frames[index];
            if (sample.CreatedSteamCells == 0) continue;
            double count = sample.CreatedSteamCells;
            double mean = sample.SumOffsetX / count;
            double variance = Math.Max(0, sample.SumOffsetXSquared / count - mean * mean);
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{index + 1},{sample.CreatedSteamCells},{mean:0.000000},{Math.Sqrt(variance):0.000000},{sample.Ring0},{sample.Ring1},{sample.Ring2},{sample.Ring3},{sample.Ring4},{sample.Ring5OrMore}"));
        }
        return path;
    }
}
