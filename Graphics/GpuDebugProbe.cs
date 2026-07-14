using System;
using System.Runtime.InteropServices;
using Phyxel.Physics;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.Mathematics.Interop;

namespace Phyxel.Graphics;

public sealed class GpuDebugProbe
{
    private readonly int interval;
    private bool requestPending;
    private uint lastRequestedFrame;

    public GpuDebugProbe(int interval = 10)
    {
        this.interval = Math.Max(1, interval);
    }

    public SimulationStatistics Latest { get; private set; }

    public void Update(GpuSimulationResources resources, uint frameIndex)
    {
        DeviceContext context = resources.Context;
        if (requestPending)
        {
            bool ready = context.GetData(resources.StatisticsQuery, AsynchronousFlags.DoNotFlush, out RawBool completed);
            if (ready && completed)
            {
                DataBox mapping = context.MapSubresource(resources.StatisticsStaging, 0, MapMode.Read, MapFlags.None);
                Latest = Marshal.PtrToStructure<SimulationStatistics>(mapping.DataPointer);
                context.UnmapSubresource(resources.StatisticsStaging, 0);
                requestPending = false;
            }
        }

        if (!requestPending && frameIndex - lastRequestedFrame >= interval)
        {
            context.CopyResource(resources.Statistics.ReadBuffer, resources.StatisticsStaging);
            context.End(resources.StatisticsQuery);
            requestPending = true;
            lastRequestedFrame = frameIndex;
        }
    }
}
