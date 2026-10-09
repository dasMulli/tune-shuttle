using Airplay2.Sender.Rtp;

namespace Airplay2.Sender.Streams;

/// <summary>
/// Converts instants of one <see cref="IMasterClock"/> between its three faces: monotonic nanoseconds
/// (<see cref="IMasterClock.NowNanos"/>, what the pacer sleeps on), NTP timestamps (0xD4 syncs and timing replies) and
/// PTP nanoseconds (0xD7 syncs). The clock only exposes "now" for each face; the constant offsets between them are
/// measured once by bracketing reads, so a sync packet can describe the instant a packet is <b>due</b> rather than the
/// instant the pacer happened to wake (SPEC-audio.md 2.3: <c>T(n) = T0 + (now_frame(n) - now_frame(0)) / 44100</c> must
/// hold exactly, which it does when T is derived from the pacer's deadlines).
/// </summary>
/// <remarks>
/// All three faces advance at the same rate by contract (one monotonic source, SPEC-audio.md 2.1, 8.5), so a measured
/// offset never drifts. The measurement error is half the shortest bracket of five tries (well below a microsecond).
/// </remarks>
internal sealed class ClockMapping
{
    private const int Tries = 5;

    private readonly long _unixMinusMonotonic;
    private readonly long _ptpMinusMonotonic;

    /// <summary>Measures the offsets of <paramref name="clock"/>.</summary>
    public ClockMapping(IMasterClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        Clock = clock;
        _unixMinusMonotonic = Measure(clock, static c => NtpTimestamp.ToUnixNanos(c.NtpNow));
        _ptpMinusMonotonic = Measure(clock, static c => unchecked((long)c.PtpNowNanos));
    }

    /// <summary>The clock.</summary>
    public IMasterClock Clock { get; }

    /// <summary>The NTP timestamp of a monotonic instant.</summary>
    public ulong NtpAt(long monotonicNanos) => NtpTimestamp.FromUnixNanos(monotonicNanos + _unixMinusMonotonic);

    /// <summary>The monotonic instant of an NTP timestamp (inverse of <see cref="NtpAt"/> up to 1 ns of rounding).</summary>
    public long MonotonicOfNtp(ulong ntp) => NtpTimestamp.ToUnixNanos(ntp) - _unixMinusMonotonic;

    /// <summary>The PTP nanoseconds of a monotonic instant.</summary>
    public ulong PtpAt(long monotonicNanos) => unchecked((ulong)(monotonicNanos + _ptpMinusMonotonic));

    /// <summary>The PTP nanoseconds of an NTP timestamp.</summary>
    public ulong PtpOfNtp(ulong ntp) => PtpAt(MonotonicOfNtp(ntp));

    private static long Measure(IMasterClock clock, Func<IMasterClock, long> face)
    {
        var bestSpan = long.MaxValue;
        var best = 0L;
        for (var i = 0; i < Tries; i++)
        {
            var before = clock.NowNanos;
            var value = face(clock);
            var after = clock.NowNanos;
            var span = after - before;
            if (span < bestSpan)
            {
                bestSpan = span;
                best = value - (before + (span / 2));
            }
        }

        return best;
    }
}
