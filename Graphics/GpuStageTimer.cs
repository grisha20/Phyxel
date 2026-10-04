using System;
using SharpDX.Direct3D11;

namespace Phyxel.Graphics;

// Diagnostic only: asynchronous timestamps, never Flush or block the game.
internal sealed class GpuStageTimer : IDisposable
{
    private readonly Query disjoint, start, end;
    private bool pending, recording;
    private int samples;
    private double total, minimum = double.PositiveInfinity, maximum;
    internal GpuStageTimer(Device device)
    {
        disjoint = new(device, new QueryDescription { Type = QueryType.TimestampDisjoint });
        start = new(device, new QueryDescription { Type = QueryType.Timestamp });
        end = new(device, new QueryDescription { Type = QueryType.Timestamp });
    }
    internal ThermalGpuTimingStatistics Statistics => new(samples, samples == 0 ? 0 : total / samples,
        samples == 0 ? 0 : minimum, maximum);
    internal void Begin(DeviceContext context)
    {
        if (pending && context.GetData(disjoint, AsynchronousFlags.DoNotFlush, out QueryDataTimestampDisjoint d) &&
            context.GetData(start, AsynchronousFlags.DoNotFlush, out long s) &&
            context.GetData(end, AsynchronousFlags.DoNotFlush, out long e))
        {
            pending = false;
            if (!d.Disjoint && d.Frequency > 0 && e >= s)
            {
                double ms = (e - s) * 1000d / d.Frequency;
                samples++; total += ms; minimum = Math.Min(minimum, ms); maximum = Math.Max(maximum, ms);
            }
        }
        recording = !pending;
        if (recording) { context.Begin(disjoint); context.End(start); }
    }
    internal void End(DeviceContext context)
    {
        if (!recording) return;
        context.End(end); context.End(disjoint); pending = true; recording = false;
    }
    public void Dispose() { disjoint.Dispose(); start.Dispose(); end.Dispose(); }
}
