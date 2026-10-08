using System;
using System.Diagnostics;
using System.IO;
using Phyxel.Graphics;

namespace Phyxel.Diagnostics;

// Opt-in CPU stages and asynchronous GPU interval around the whole rendered frame.
// No Flush, blocking readback, physical clock adjustment or quality changes.
internal sealed class FramePerformanceTrace : IDisposable
{
    private readonly string? path = Environment.GetEnvironmentVariable("PHYXEL_FRAME_TRACE");
    private StreamWriter? writer;
    private GpuStageTimer? gpu, computeGpu, drawGpu;
    private long start, dispatch, afterDispatch, draw, present;
    private double updateMs, dispatchMs, drawMs, presentMs;
    private GpuSimulationResources? resources;
    private int frames;
    private readonly Stopwatch wall = Stopwatch.StartNew();
    internal void BeginUpdate(GpuSimulationResources? r)
    {
        if (string.IsNullOrEmpty(path)) return;
        start = Stopwatch.GetTimestamp();
        resources = r;
        if (r is not null) { gpu ??= new(r.Device); gpu.Begin(r.Context); }
    }
    internal void BeginDispatch(GpuSimulationResources? r)
    {
        if(path is null)return; dispatch=Stopwatch.GetTimestamp();
        if(r is not null){computeGpu??=new(r.Device);computeGpu.Begin(r.Context);}
    }
    internal void EndDispatch(GpuSimulationResources? r)
    {
        if(path is null)return;afterDispatch=Stopwatch.GetTimestamp();dispatchMs=Ms(afterDispatch-dispatch);
        if(r is not null)computeGpu?.End(r.Context);
    }
    internal void BeginDraw(GpuSimulationResources? r)
    {
        if (path is null) return;
        draw = Stopwatch.GetTimestamp(); updateMs = Ms(draw - start);
        if(r is not null){drawGpu??=new(r.Device);drawGpu.Begin(r.Context);}
    }
    internal void EndDraw(GpuSimulationResources? r)
    {
        if (path is null) return;
        drawMs = Ms(Stopwatch.GetTimestamp() - draw);
        if (r is not null) {drawGpu?.End(r.Context);gpu?.End(r.Context);}
    }
    internal void BeginPresent() { if (path is not null) present = Stopwatch.GetTimestamp(); }
    internal void EndPresent()
    {
        if (path is not null) presentMs = Ms(Stopwatch.GetTimestamp() - present);
    }
    internal void EndFrame(double pacingMs)
    {
        if (string.IsNullOrEmpty(path) || start == 0) return;
        if (writer is null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            writer = new(path);
            writer.WriteLine("wallSeconds,frame,updateCpuMs,dispatchCpuMs,drawCpuMs,presentCpuMs,frameGpuAverageMs,gpuSamples,dispatchGpuAverageMs,drawGpuAverageMs,pacingCpuMs,fragmentGpuMs,confinementGpuMs");
        }
        var stats = gpu?.Statistics ?? default;
        writer.WriteLine(FormattableString.Invariant($"{wall.Elapsed.TotalSeconds:F6},{++frames},{updateMs:F6},{dispatchMs:F6},{drawMs:F6},{presentMs:F6},{stats.AverageMilliseconds:F6},{stats.Samples},{computeGpu?.Statistics.AverageMilliseconds??0:F6},{drawGpu?.Statistics.AverageMilliseconds??0:F6},{pacingMs:F6},{resources?.FragmentTimer?.Statistics.AverageMilliseconds??0:F6},{resources?.ConfinementTimer?.Statistics.AverageMilliseconds??0:F6}"));
    }
    private static double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;
    public void Dispose() { writer?.Dispose(); gpu?.Dispose(); computeGpu?.Dispose();drawGpu?.Dispose(); }
}
