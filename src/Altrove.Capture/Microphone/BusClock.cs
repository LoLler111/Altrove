// Adapted from celmic (MIT, Celmic contributors): see third_party/celmic/NOTICE.md.
using System.Runtime.InteropServices;

namespace Altrove.Capture.Microphone;

/// <summary>
/// The USB bus clock as Windows' USB audio sees it: usbip-win2 reports the frame number as
/// KeQueryInterruptTime() / 10000, which only advances on each clock interrupt. Completions are timed against the
/// same coarse value, or the host sees transfers on frames it thinks have not happened yet and resets the pipe.
/// </summary>
internal static class BusClock
{
    /// <summary>100 ns interrupt-time ticks per full-speed frame (1 ms).</summary>
    public const ulong TicksPerFrame = 10_000;

    /// <summary>The coarse interrupt time (what the host reads) and the precise one.</summary>
    public static (ulong Coarse, ulong Precise) Ticks()
    {
        QueryInterruptTimePrecise(out var precise); // first, so coarse never appears ahead of it
        QueryInterruptTime(out var coarse);
        return (Math.Min(coarse, precise), precise);
    }

    public static ulong Frame() => Ticks().Coarse / TicksPerFrame;

    /// <summary>Time left until bus frame <paramref name="frame"/> begins on the precise clock.</summary>
    public static TimeSpan UntilFrame(ulong frame)
    {
        var target = frame * TicksPerFrame;
        var now = Ticks().Precise;
        return target > now ? TimeSpan.FromTicks((long)(target - now)) : TimeSpan.Zero;
    }

    /// <summary>
    /// A 1 ms timer resolution while alive, so the coarse interrupt time advances every frame like a real host
    /// controller's. Windows 11 ignores it for minimized windows unless the process opts out of power throttling.
    /// </summary>
    public static IDisposable FineTimer()
    {
        var state = new PowerThrottlingState { Version = 1, ControlMask = 0x1 | 0x4, StateMask = 0 };
        SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, ref state, Marshal.SizeOf<PowerThrottlingState>());
        timeBeginPeriod(1);
        return new Release();
    }

    private sealed class Release : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                timeEndPeriod(1);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version, ControlMask, StateMask;
    }

    [DllImport("api-ms-win-core-realtime-l1-1-1.dll")]
    private static extern void QueryInterruptTime(out ulong time);

    [DllImport("api-ms-win-core-realtime-l1-1-1.dll")]
    private static extern void QueryInterruptTimePrecise(out ulong time);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState information, int size);
}
