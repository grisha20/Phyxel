using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Phyxel.Graphics;
using Phyxel.Physics;
using SharpDX;
using SharpDX.Direct3D11;

namespace Phyxel.Diagnostics;

/// <summary>
/// Reads the cumulative acceptance-only FIRE vertical-motion counters. The
/// trace observes GPU state after a frame; it does not alter gas movement.
/// </summary>
public sealed class GasVerticalMotionTrace
{
    private GasVerticalMotionStatistics latest;

    public int Count { get; private set; }

    public GasVerticalMotionStatistics Latest => latest;

    public void Record(GpuSimulationResources resources)
    {
        DeviceContext context = resources.Context;
        context.CopyResource(
            resources.GasVerticalMotionStatistics.Buffer,
            resources.GasVerticalMotionStatisticsStaging);
        DataBox mapping = context.MapSubresource(
            resources.GasVerticalMotionStatisticsStaging,
            0,
            MapMode.Read,
            MapFlags.None);
        latest = Marshal.PtrToStructure<GasVerticalMotionStatistics>(mapping.DataPointer);
        context.UnmapSubresource(resources.GasVerticalMotionStatisticsStaging, 0);
        Count++;
    }

    public string WriteCsv(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        double cellFrames = Math.Max(1, latest.FireCellFrames);
        double meanVelocityY = latest.FireVelocityYMillisteps / (1000.0 * cellFrames);
        double actualRisePerFrame = latest.FireUpwardSteps / cellFrames;
        double offsetYClampFraction = latest.FireOffsetYClampFrames / cellFrames;
        double blockedByGasFraction = latest.FireUpwardBlockedByGas /
            (double)Math.Max(1, latest.FireUpwardCandidates);
        using StreamWriter writer = new(path, false);
        double blockedCellFrameFraction = latest.FireUpwardBlockedCellFrames / cellFrames;
        writer.WriteLine("fireCellFrames,meanVelocityY,actualRisePerFireFrame,offsetYClampFraction,upwardCandidates,upwardSteps,upwardBlockedByGas,upwardBlockedByGasFraction,upwardBlockedCellFrames,upwardBlockedCellFrameFraction");
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{latest.FireCellFrames},{meanVelocityY:F6},{actualRisePerFrame:F6},{offsetYClampFraction:F6},{latest.FireUpwardCandidates},{latest.FireUpwardSteps},{latest.FireUpwardBlockedByGas},{blockedByGasFraction:F6},{latest.FireUpwardBlockedCellFrames},{blockedCellFrameFraction:F6}"));
        return path;
    }

    public static string FormatMetalChimneyBands(GasVerticalMotionStatistics statistics)
    {
        return FormatBand("low", statistics.PipeLow) + " " +
            FormatBand("mid", statistics.PipeMid) + " " +
            FormatBand("high", statistics.PipeHigh);
    }

    private static string FormatBand(string name, GasPipeBandMotionStatistics band)
    {
        double frames = Math.Max(1, band.GasCellFrames);
        return $"pipe{name}CellFrames={band.GasCellFrames} " +
            $"pipe{name}MeanVelocityY={band.GasVelocityYMillisteps / (1000.0 * frames):0.000000} " +
            $"pipe{name}ActualUpwardStepsPerGasFrame={band.GasUpwardSteps / frames:0.000000} " +
            $"pipe{name}OffsetYClampFraction={band.GasOffsetYClampFrames / frames:0.000000}";
    }
}
