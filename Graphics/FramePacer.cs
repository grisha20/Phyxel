using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Phyxel.Graphics;

// Presentation only. Wait after Present so the next GameTime includes this
// interval; the simulation's fixed-step accumulators remain authoritative.
internal sealed class FramePacer : IDisposable
{
    private readonly long frameTicks;
    private readonly SafeWaitHandle? timer;
    private readonly bool timerResolutionRequested;
    private long nextDeadline;
    internal bool UsesHighResolutionTimer => timer is not null;

    internal FramePacer(int framesPerSecond)
    {
        frameTicks = (long)Math.Ceiling(Stopwatch.Frequency / (double)framesPerSecond);
        // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_MODIFY_STATE | SYNCHRONIZE.
        timer = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x00100002);
        if (timer.IsInvalid)
        {
            timer.Dispose();
            timer = null;
            timerResolutionRequested = timeBeginPeriod(1) == 0;
        }
    }

    internal void BeginFrame()
    {
        long now = Stopwatch.GetTimestamp();
        // Preserve the cadence across small wake-up delays. After a complete
        // missed frame (dialogs, resize, heavy GPU work), start a fresh cadence.
        if (nextDeadline == 0 || now - nextDeadline >= frameTicks)
            nextDeadline = now + frameTicks;
    }

    internal double WaitForNextFrame()
    {
        long before = Stopwatch.GetTimestamp();
        if (nextDeadline == 0) return 0;
        long remaining = nextDeadline - before;
        if (remaining > 0 && timer is not null)
        {
            // Negative due time is relative, in 100 ns units.
            long dueTime = -(long)Math.Ceiling(remaining * (10_000_000d / Stopwatch.Frequency));
            if (SetWaitableTimer(timer, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
                WaitForSingleObject(timer, 0xFFFFFFFF);
            else
                SleepRemaining(remaining);
        }
        else if (remaining > 0) SleepRemaining(remaining);
        long after = Stopwatch.GetTimestamp();
        nextDeadline += frameTicks;
        if (after >= nextDeadline) nextDeadline = after + frameTicks;
        return remaining > 0 ? (after - before) * 1000d / Stopwatch.Frequency : 0;
    }

    private static void SleepRemaining(long remaining) =>
        Thread.Sleep((int)Math.Ceiling(remaining * (1000d / Stopwatch.Frequency)));

    public void Dispose()
    {
        timer?.Dispose();
        if (timerResolutionRequested) timeEndPeriod(1);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period,
        IntPtr callback, IntPtr state, [MarshalAs(UnmanagedType.Bool)] bool resume);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}
