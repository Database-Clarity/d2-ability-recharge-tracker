using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace D2AbilityTracker;

internal static class CaptureSchedule
{
    public static long NextFrame(long started, long finished, int targetRate, long frequency)
    {
        var interval = Math.Max(1, frequency / targetRate);
        var next = started + interval;
        return next > finished ? next : next + ((finished - next) / interval + 1) * interval;
    }
}

internal sealed class CaptureOverloadBackoff
{
    private int _overruns;
    public long Delay(long work, long interval)
    {
        _overruns = work >= interval ? Math.Min(32, _overruns + 1) : Math.Max(0, _overruns - 2);
        return _overruns < 8 ? 0 : Math.Min(interval / 2, interval * (_overruns - 7) / 16);
    }
}

/// <summary>Deadline pacing: rejected inputs do not move the next deadline.</summary>
internal sealed class FramePacer(long interval)
{
    private long? _next;
    public long Skipped { get; private set; }
    public bool Peek(long now) => _next is not { } deadline || now >= deadline;
    public bool IsDue(long now)
    {
        if (_next is { } deadline && now < deadline) { Skipped++; return false; }
        var next = _next ?? now;
        _next = next + ((now - next) / interval + 1) * interval;
        return true;
    }
}

/// <summary>
/// Sub-millisecond sleeps for the capture loop. Default Windows waits round up to the ~15.6 ms system tick,
/// which would cap and alias the capture rate; a high-resolution waitable timer avoids that.
/// </summary>
internal sealed class PrecisionWait : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;
    private readonly WaitHandle? _timer;

    public PrecisionWait()
    {
        var handle = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return;
        }

        _timer = new EventWaitHandle(false, EventResetMode.AutoReset) { SafeWaitHandle = handle };
    }

    public bool IsHighResolution => _timer is not null;

    /// <summary>Waits for the given duration; returns true if <paramref name="cancel"/> was signaled.</summary>
    public bool Wait(TimeSpan duration, WaitHandle cancel)
    {
        if (_timer is null)
        {
            return cancel.WaitOne(Math.Max(1, (int)Math.Ceiling(duration.TotalMilliseconds)));
        }

        // Negative due time means relative, in 100 ns units.
        var dueTime = -Math.Max(1, duration.Ticks);
        if (!SetWaitableTimerEx(_timer.SafeWaitHandle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
        {
            return cancel.WaitOne(Math.Max(1, (int)Math.Ceiling(duration.TotalMilliseconds)));
        }

        return WaitHandle.WaitAny([_timer, cancel]) == 1;
    }

    public void Dispose() => _timer?.Dispose();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completion,
        IntPtr completionArgument, IntPtr wakeContext, uint tolerableDelay);
}