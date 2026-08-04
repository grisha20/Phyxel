using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Phyxel.Graphics;
using Phyxel.Physics;
using SharpDX;
using SharpDX.Direct3D11;

namespace Phyxel.Diagnostics;

/// <summary>
/// Acceptance-only readback of every lateral gas transport path. The buffer is
/// cleared once per fixed gas tick; recording is enabled only by an environment
/// variable and never participates in simulation decisions.
/// </summary>
public sealed class GasLateralTransferTrace
{
    private static readonly string[] PathNames =
    [
        "motion_horizontal",
        "obstacle_x",
        "obstacle_diagonal",
        "redistribution_horizontal",
        "redistribution_diagonal",
        "different_gas_diagonal"
    ];

    private readonly List<(uint Frame, uint[] Counters)> samples = [];

    public int Count => samples.Count;

    public void Record(uint frame, GpuSimulationResources resources)
    {
        DeviceContext context = resources.Context;
        context.CopyResource(
            resources.GasLateralTransferStatistics.Buffer,
            resources.GasLateralTransferStatisticsStaging);
        DataBox mapping = context.MapSubresource(
            resources.GasLateralTransferStatisticsStaging,
            0,
            MapMode.Read,
            MapFlags.None);
        uint[] counters = new uint[GasLateralTransferStatisticsLayout.Count];
        for (int index = 0; index < counters.Length; index++)
        {
            counters[index] = unchecked((uint)Marshal.ReadInt32(mapping.DataPointer, index * sizeof(uint)));
        }
        context.UnmapSubresource(resources.GasLateralTransferStatisticsStaging, 0);
        samples.Add((frame, counters));
    }

    public string WriteCsv(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        using StreamWriter writer = new(path, false);
        writer.WriteLine("frame,path,left,right,fromLeftLeft,fromLeftRight,fromRightLeft,fromRightRight,velocityMatch,velocityMismatch,velocityZero,fireLeft,fireRight,fireVelocityMatch,fireVelocityMismatch,fireVelocityZero");
        foreach ((uint frame, uint[] counters) in samples)
        {
            foreach (GasLateralPathTotal total in BuildTotals(counters))
            {
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{frame},{total.Path},{total.Left},{total.Right},{total.FromLeftLeft},{total.FromLeftRight},{total.FromRightLeft},{total.FromRightRight},{total.VelocityMatch},{total.VelocityMismatch},{total.VelocityZero},{total.FireLeft},{total.FireRight},{total.FireVelocityMatch},{total.FireVelocityMismatch},{total.FireVelocityZero}"));
            }
        }
        return path;
    }

    public IReadOnlyList<GasLateralPathTotal> Sum() => BuildTotals(SumCounters());

    private ulong[] SumCounters()
    {
        ulong[] total = new ulong[GasLateralTransferStatisticsLayout.Count];
        foreach ((_, uint[] counters) in samples)
        {
            for (int index = 0; index < total.Length; index++)
            {
                total[index] += counters[index];
            }
        }
        return total;
    }

    private static IReadOnlyList<GasLateralPathTotal> BuildTotals(IReadOnlyList<uint> counters)
    {
        ulong[] widened = counters.Select(value => (ulong)value).ToArray();
        return BuildTotals(widened);
    }

    private static IReadOnlyList<GasLateralPathTotal> BuildTotals(IReadOnlyList<ulong> counters)
    {
        GasLateralPathTotal[] totals = new GasLateralPathTotal[GasLateralTransferStatisticsLayout.PathCount];
        for (int path = 0; path < totals.Length; path++)
        {
            int offset = path * GasLateralTransferStatisticsLayout.FieldsPerPath;
            totals[path] = new GasLateralPathTotal(
                PathNames[path],
                counters[offset], counters[offset + 1],
                counters[offset + 2], counters[offset + 3],
                counters[offset + 4], counters[offset + 5],
                counters[offset + 6], counters[offset + 7], counters[offset + 8],
                counters[offset + 9], counters[offset + 10],
                counters[offset + 11], counters[offset + 12], counters[offset + 13]);
        }
        return totals;
    }
}

public readonly record struct GasLateralPathTotal(
    string Path,
    ulong Left,
    ulong Right,
    ulong FromLeftLeft,
    ulong FromLeftRight,
    ulong FromRightLeft,
    ulong FromRightRight,
    ulong VelocityMatch,
    ulong VelocityMismatch,
    ulong VelocityZero,
    ulong FireLeft,
    ulong FireRight,
    ulong FireVelocityMatch,
    ulong FireVelocityMismatch,
    ulong FireVelocityZero);
