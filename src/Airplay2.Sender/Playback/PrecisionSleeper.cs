using System.Runtime.InteropServices;
using Airplay2.Sender.Rtp;
using Microsoft.Win32.SafeHandles;

namespace Airplay2.Sender.Playback;

/// <summary>
/// Sleeps the pacer thread until a deadline on the master clock with sub-millisecond accuracy (SPEC-audio.md 8.2:
/// "sleep until then (Stopwatch plus a short spin for the last millisecond; Windows timer resolution must be raised with
/// <c>timeBeginPeriod(1)</c> or <c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c>; on Linux <c>nanosleep</c> is fine)").
/// The coarse part sleeps in the OS; the last <see cref="SpinWindow"/> is spun against the clock.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Windows 10 1803 and later: a high-resolution waitable timer (<c>CreateWaitableTimerExW</c> with
/// <c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c>), which does not change the system-wide timer resolution.</item>
/// <item>Older Windows: <c>timeBeginPeriod(1)</c> for the lifetime of the sleeper and <see cref="Thread.Sleep(int)"/>,
/// with a longer spin window because a 1 ms sleep may last up to 2 ms.</item>
/// <item>Linux (glibc) and macOS: <c>nanosleep</c> from libc (CLOCK_MONOTONIC semantics; accurate to tens of microseconds).</item>
/// <item>Anything else: <see cref="Thread.Sleep(int)"/>.</item>
/// </list>
/// One owner thread; not thread-safe.
/// </remarks>
internal sealed class PrecisionSleeper : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;
    private const uint Infinite = 0xFFFFFFFF;

    private readonly WaitableTimerHandle? _timer;
    private readonly bool _timePeriodRaised;
    private readonly bool _nanoSleep;
    private readonly long _spinNanos;
    private bool _disposed;

    /// <summary>Creates the sleeper for this platform.</summary>
    public PrecisionSleeper()
    {
        if (OperatingSystem.IsWindows())
        {
            WaitableTimerHandle? timer = null;
            try
            {
                timer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, CreateWaitableTimerHighResolution, TimerAllAccess);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                timer = null;
            }

            if (timer is { IsInvalid: false })
            {
                _timer = timer;
                Mechanism = "high-resolution waitable timer";
                _spinNanos = 600_000;
                return;
            }

            timer?.Dispose();
            try
            {
                _timePeriodRaised = TimeBeginPeriod(1) == 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _timePeriodRaised = false;
            }

            Mechanism = _timePeriodRaised ? "timeBeginPeriod(1) + Sleep" : "Sleep";
            _spinNanos = 2_000_000;
            return;
        }

        if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && ProbeNanoSleep())
        {
            Mechanism = "nanosleep";
            _nanoSleep = true;
            _spinNanos = 200_000;
            return;
        }

        Mechanism = "Sleep";
        _spinNanos = 2_000_000;
    }

    /// <summary>Which OS mechanism does the coarse part (diagnostics).</summary>
    public string Mechanism { get; }

    /// <summary>The final stretch before a deadline that is spun rather than slept.</summary>
    public TimeSpan SpinWindow => TimeSpan.FromTicks(_spinNanos / 100);

    /// <summary>Returns at or just after <paramref name="deadlineNanos"/> (<see cref="IMasterClock.NowNanos"/> units); at once when it has passed.</summary>
    public void SleepUntil(IMasterClock clock, long deadlineNanos)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (true)
        {
            var remaining = deadlineNanos - clock.NowNanos;
            if (remaining <= 0)
            {
                return;
            }

            if (remaining <= _spinNanos)
            {
                while (clock.NowNanos < deadlineNanos)
                {
                    Thread.SpinWait(20);
                }

                return;
            }

            CoarseSleep(remaining - _spinNanos);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
        if (_timePeriodRaised)
        {
            _ = TimeEndPeriod(1);
        }
    }

    private void CoarseSleep(long nanos)
    {
        if (_timer is not null)
        {
            // Relative due time in 100 ns units is negative.
            var due = -Math.Max(1, nanos / 100);
            if (SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                _ = WaitForSingleObject(_timer, Infinite);
                return;
            }

            Thread.Sleep(1);
            return;
        }

        if (_nanoSleep)
        {
            var request = new Timespec { Seconds = (nint)(nanos / 1_000_000_000), Nanoseconds = (nint)(nanos % 1_000_000_000) };
            // EINTR returns early; the caller's loop re-checks the clock.
            _ = OperatingSystem.IsMacOS() ? NanoSleepMac(ref request, IntPtr.Zero) : NanoSleepGlibc(ref request, IntPtr.Zero);
            return;
        }

        Thread.Sleep((int)Math.Max(1, nanos / 1_000_000));
    }

    /// <summary>
    /// glibc's <c>libc.so.6</c> (the Debian-based .NET container images) or macOS's <c>libSystem</c>; on musl (Alpine) the
    /// library name differs and the sleeper falls back to <see cref="Thread.Sleep(int)"/> with a longer spin window.
    /// </summary>
    private static bool ProbeNanoSleep()
    {
        var zero = default(Timespec);
        try
        {
            _ = OperatingSystem.IsMacOS() ? NanoSleepMac(ref zero, IntPtr.Zero) : NanoSleepGlibc(ref zero, IntPtr.Zero);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public nint Seconds;
        public nint Nanoseconds;
    }

    private sealed class WaitableTimerHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public WaitableTimerHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

#pragma warning disable SYSLIB1054 // LibraryImport needs AllowUnsafeBlocks, which this project does not enable; these signatures are trivially marshalled.
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern WaitableTimerHandle CreateWaitableTimerExW(IntPtr attributes, IntPtr name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(WaitableTimerHandle timer, ref long dueTime, int period, IntPtr completionRoutine, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern uint WaitForSingleObject(WaitableTimerHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", ExactSpelling = true)]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod", ExactSpelling = true)]
    private static extern uint TimeEndPeriod(uint milliseconds);

    [DllImport("libc.so.6", EntryPoint = "nanosleep", SetLastError = true)]
    private static extern int NanoSleepGlibc(ref Timespec request, IntPtr remaining);

    [DllImport("libSystem.dylib", EntryPoint = "nanosleep", SetLastError = true)]
    private static extern int NanoSleepMac(ref Timespec request, IntPtr remaining);
#pragma warning restore SYSLIB1054
}
