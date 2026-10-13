using System;
using SharpDX.Direct3D11;

namespace Phyxel.Graphics;

// Diagnostic only: asynchronous timestamps, never Flush or block the game.
internal sealed class GpuStageTimer : IDisposable
{
    private sealed class Slot : IDisposable
    {
        internal readonly Query Disjoint, Start, End;
        internal bool Discard;
        internal Slot(Device device)
        {
            Disjoint=new(device,new QueryDescription{Type=QueryType.TimestampDisjoint});
            Start=new(device,new QueryDescription{Type=QueryType.Timestamp});
            End=new(device,new QueryDescription{Type=QueryType.Timestamp});
        }
        public void Dispose(){Disjoint.Dispose();Start.Dispose();End.Dispose();}
    }
    private readonly Slot[] slots;
    private readonly double[] recent=new double[2048];
    private int head,pending,recording=-1,samples;
    private double total,minimum=double.PositiveInfinity,maximum;
    internal GpuStageTimer(Device device)
    {
        // A replay can queue many frames before a readback. One query pair
        // then samples only a few frame parities. Opt-in buffering measures
        // consecutive frames without forcing GPU/CPU synchronisation.
        int count=int.TryParse(Environment.GetEnvironmentVariable("PHYXEL_TIMING_QUERY_SLOTS"),out int requested)
            ? Math.Clamp(requested,1,256) : 1;
        slots=new Slot[count];for(int i=0;i<count;i++)slots[i]=new Slot(device);
    }
    internal ThermalGpuTimingStatistics Statistics => new(samples,samples==0?0:total/samples,
        samples==0?0:minimum,maximum);
    internal double P95Milliseconds
    {
        get
        {
            int count=Math.Min(samples,recent.Length);if(count==0)return 0;
            var copy=new double[count];Array.Copy(recent,copy,count);Array.Sort(copy);
            return copy[Math.Min(count-1,(int)Math.Ceiling(count*.95)-1)];
        }
    }
    internal void Poll(DeviceContext context)
    {
        while(pending>0)
        {
            var slot=slots[head];
            if(!context.GetData(slot.Disjoint,AsynchronousFlags.DoNotFlush,out QueryDataTimestampDisjoint d) ||
               !context.GetData(slot.Start,AsynchronousFlags.DoNotFlush,out long s) ||
               !context.GetData(slot.End,AsynchronousFlags.DoNotFlush,out long e))break;
            if(!slot.Discard && !d.Disjoint && d.Frequency>0 && e>=s)
            {
                double ms=(e-s)*1000d/d.Frequency;recent[samples%recent.Length]=ms;
                samples++;total+=ms;minimum=Math.Min(minimum,ms);maximum=Math.Max(maximum,ms);
            }
            head=(head+1)%slots.Length;pending--;
        }
    }
    internal void Begin(DeviceContext context)
    {
        Poll(context);recording=-1;if(pending==slots.Length)return;
        recording=(head+pending)%slots.Length;
        var slot=slots[recording];slot.Discard=false;
        context.Begin(slot.Disjoint);context.End(slot.Start);
    }
    internal void End(DeviceContext context)
    {
        if(recording<0)return;
        var slot=slots[recording];context.End(slot.End);context.End(slot.Disjoint);
        pending++;recording=-1;
    }
    internal void ResetStatistics()
    {
        samples=0;total=0;minimum=double.PositiveInfinity;maximum=0;
        for(int i=0;i<pending;i++)slots[(head+i)%slots.Length].Discard=true;
        if(recording>=0)slots[recording].Discard=true;
    }
    public void Dispose(){foreach(var slot in slots)slot.Dispose();}
}
