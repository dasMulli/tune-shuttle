using System.Diagnostics;
using Airplay2.Sender.Rtp;

namespace Airplay2.Receiver.Timing;

/// <summary>
/// The local monotonic clock a <see cref="PtpSlave"/> stamps arrivals with and maps sender time onto, in nanoseconds
/// (SPEC-receiver.md 6.2). In the product it is an adapter over the process's one <see cref="IMasterClock"/>
/// (<see cref="MonotonicClock.FromMasterClock"/>), the clock the grandmaster and the group player's pacer use.
/// </summary>
public interface IMonotonicClock
{
    /// <summary>The current time in nanoseconds; never decreases.</summary>
    ulong NowNanoseconds { get; }
}

/// <summary>The <see cref="IMonotonicClock"/> implementations (SPEC-receiver.md 6.2).</summary>
public static class MonotonicClock
{
    /// <summary>A <see cref="Stopwatch"/> clock that starts at 0 when created.</summary>
    public static IMonotonicClock CreateStopwatch() => new StopwatchClock(Stopwatch.GetTimestamp());

    /// <summary>The adapter over an <see cref="IMasterClock"/>: its <see cref="IMasterClock.NowNanos"/>.</summary>
    public static IMonotonicClock FromMasterClock(IMasterClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return new DelegateClock(() => unchecked((ulong)clock.NowNanos));
    }

    /// <summary>A clock read from a delegate (tests pass a master's clock plus a known offset).</summary>
    public static IMonotonicClock FromDelegate(Func<ulong> now)
    {
        ArgumentNullException.ThrowIfNull(now);
        return new DelegateClock(now);
    }

    private sealed class StopwatchClock(long start) : IMonotonicClock
    {
        public ulong NowNanoseconds => (ulong)((Stopwatch.GetTimestamp() - start) * (1_000_000_000.0 / Stopwatch.Frequency));
    }

    private sealed class DelegateClock(Func<ulong> now) : IMonotonicClock
    {
        public ulong NowNanoseconds => now();
    }
}
