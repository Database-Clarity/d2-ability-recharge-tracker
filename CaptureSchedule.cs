using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace D2AbilityTracker;

internal static class CaptureSchedule
{
    public static long NextFrame(long started, long finished, int targetRate, long frequency)
    {
        var targetInterval = frequency / targetRate;
        var restInterval = Math.Max(frequency / 1000, (finished - started) * 3);
        return Math.Max(started + targetInterval, finished + restInterval);
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