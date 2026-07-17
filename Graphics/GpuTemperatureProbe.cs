using System;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Phyxel.Physics;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.Mathematics.Interop;

namespace Phyxel.Graphics;

public sealed class GpuTemperatureProbe
{
    public const float RequestIntervalSeconds = 0.1f;

    private GpuSimulationResources? boundResources;
    private bool requestPending;
    private int generation;
    private int pendingGeneration;
    private double requestAccumulator;
    private bool timingPending;
    private int timingSamples;
    private double timingTotalMilliseconds;
    private double timingMinimumMilliseconds = double.PositiveInfinity;
    private double timingMaximumMilliseconds;

    public TemperatureProbeResult? Latest { get; private set; }
    public ThermalGpuTimingStatistics GpuTiming => new(
        timingSamples,
        timingSamples == 0 ? 0 : timingTotalMilliseconds / timingSamples,
        timingSamples == 0 ? 0 : timingMinimumMilliseconds,
        timingMaximumMilliseconds);

    public void Update(
        GpuSimulationResources resources,
        Point? coordinate,
        float elapsedSeconds)
    {
        if (!ReferenceEquals(resources, boundResources))
        {
            boundResources = resources;
            requestPending = false;
            timingPending = false;
            timingSamples = 0;
            timingTotalMilliseconds = 0;
            timingMinimumMilliseconds = double.PositiveInfinity;
            timingMaximumMilliseconds = 0;
            Reset();
        }

        PollTiming(resources);
        Poll(resources);
        if (coordinate is null || !resources.IsSimulationAllocated)
        {
            if (Latest is not null)
            {
                Reset();
            }
            requestAccumulator = 0;
            return;
        }

        requestAccumulator = Math.Min(
            RequestIntervalSeconds,
            requestAccumulator + Math.Clamp((double)elapsedSeconds, 0, 0.25));
        if (requestPending || timingPending || requestAccumulator < RequestIntervalSeconds)
        {
            return;
        }

        Dispatch(resources, coordinate.Value);
        requestAccumulator = 0;
    }

    public void Reset()
    {
        Latest = null;
        generation++;
        requestAccumulator = 0;
    }

    public static Point? MapPointerToCell(
        Point pointer,
        Rectangle worldBounds,
        int worldWidth,
        int worldHeight)
    {
        if (worldWidth <= 0 || worldHeight <= 0 ||
            worldBounds.Width <= 0 || worldBounds.Height <= 0 ||
            !worldBounds.Contains(pointer))
        {
            return null;
        }

        int x = (int)(((long)pointer.X - worldBounds.X) * worldWidth / worldBounds.Width);
        int y = (int)(((long)pointer.Y - worldBounds.Y) * worldHeight / worldBounds.Height);
        return new Point(
            Math.Clamp(x, 0, worldWidth - 1),
            Math.Clamp(y, 0, worldHeight - 1));
    }

    private void Poll(GpuSimulationResources resources)
    {
        if (!requestPending)
        {
            return;
        }

        DeviceContext context = resources.Context;
        bool ready = context.GetData(
            resources.TemperatureProbeQuery,
            AsynchronousFlags.DoNotFlush,
            out RawBool completed);
        if (!ready || !completed)
        {
            return;
        }

        DataBox mapping = context.MapSubresource(
            resources.TemperatureProbeStaging,
            0,
            MapMode.Read,
            MapFlags.None);
        TemperatureProbeResult result = Marshal.PtrToStructure<TemperatureProbeResult>(mapping.DataPointer);
        context.UnmapSubresource(resources.TemperatureProbeStaging, 0);
        requestPending = false;
        if (pendingGeneration == generation)
        {
            Latest = result;
        }
    }

    private void Dispatch(GpuSimulationResources resources, Point coordinate)
    {
        TemperatureProbeConstants constants = new()
        {
            X = (uint)coordinate.X,
            Y = (uint)coordinate.Y,
            Width = (uint)resources.Width,
            Height = (uint)resources.Height
        };
        DeviceContext context = resources.Context;
        context.UpdateSubresource(ref constants, resources.TemperatureProbeConstants);
        context.ComputeShader.Set(resources.TemperatureProbeShader);
        context.ComputeShader.SetConstantBuffer(0, resources.TemperatureProbeConstants);
        context.ComputeShader.SetShaderResource(0, resources.Grid.ReadView);
        context.ComputeShader.SetUnorderedAccessView(0, resources.TemperatureProbeResult.UnorderedView);
        context.Begin(resources.ProbeTimestampDisjointQuery);
        context.End(resources.ProbeTimestampStartQuery);
        context.Dispatch(1, 1, 1);
        context.End(resources.ProbeTimestampEndQuery);
        context.End(resources.ProbeTimestampDisjointQuery);
        timingPending = true;
        context.ComputeShader.SetShaderResource(0, null);
        context.ComputeShader.SetUnorderedAccessView(0, null);
        context.ComputeShader.Set(null);
        context.CopyResource(
            resources.TemperatureProbeResult.Buffer,
            resources.TemperatureProbeStaging);
        context.End(resources.TemperatureProbeQuery);
        pendingGeneration = generation;
        requestPending = true;
    }

    private void PollTiming(GpuSimulationResources resources)
    {
        if (!timingPending)
        {
            return;
        }
        DeviceContext context = resources.Context;
        bool disjointReady = context.GetData(
            resources.ProbeTimestampDisjointQuery,
            AsynchronousFlags.DoNotFlush,
            out QueryDataTimestampDisjoint disjoint);
        bool startReady = context.GetData(
            resources.ProbeTimestampStartQuery,
            AsynchronousFlags.DoNotFlush,
            out long start);
        bool endReady = context.GetData(
            resources.ProbeTimestampEndQuery,
            AsynchronousFlags.DoNotFlush,
            out long end);
        if (!disjointReady || !startReady || !endReady)
        {
            return;
        }
        timingPending = false;
        if (disjoint.Disjoint || disjoint.Frequency <= 0 || end < start)
        {
            return;
        }
        double milliseconds = (end - start) * 1000d / disjoint.Frequency;
        timingSamples++;
        timingTotalMilliseconds += milliseconds;
        timingMinimumMilliseconds = Math.Min(timingMinimumMilliseconds, milliseconds);
        timingMaximumMilliseconds = Math.Max(timingMaximumMilliseconds, milliseconds);
    }
}
