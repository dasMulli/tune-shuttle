using System.Diagnostics;

namespace Airplay2.Sender.Rtp;

/// <summary>
/// The one clock everything in the audio plane reads (SPEC-audio.md 2.1, 8.5): monotonic, never corrected by the
/// wall clock, with an NTP epoch fixed at construction. Timing replies, sync packets, the pacer and the PTP
/// grandmaster must all derive their times from the same instance.
/// </summary>
public interface IMasterClock
{
    /// <summary>Monotonic nanoseconds since an arbitrary origin.</summary>
    long NowNanos { get; }

    /// <summary>The current time as a 64-bit NTP timestamp (seconds since 1900 in the high word, 2^-32 s in the low word).</summary>
    ulong NtpNow { get; }

    /// <summary>The current time as nanoseconds on the PTP timeline (TAI-like; only differences matter to receivers).</summary>
    ulong PtpNowNanos { get; }
}

/// <summary>NTP timestamp arithmetic (SPEC-audio.md 2.1).</summary>
public static class NtpTimestamp
{
    /// <summary>Seconds between 1900-01-01 and 1970-01-01.</summary>
    public const ulong UnixEpochOffset = 0x83AA7E80;

    /// <summary>Builds an NTP timestamp from Unix nanoseconds.</summary>
    public static ulong FromUnixNanos(long unixNanos)
    {
        var seconds = (ulong)(unixNanos / 1_000_000_000L) + UnixEpochOffset;
        var nanos = (ulong)(unixNanos % 1_000_000_000L);
        var fraction = (nanos << 32) / 1_000_000_000UL;
        return (seconds << 32) | fraction;
    }

    /// <summary>Converts an NTP timestamp to Unix nanoseconds.</summary>
    public static long ToUnixNanos(ulong ntp)
    {
        var seconds = (long)(ntp >> 32) - (long)UnixEpochOffset;
        var fraction = ntp & 0xFFFFFFFFUL;
        var nanos = (long)((fraction * 1_000_000_000UL) >> 32);
        return seconds * 1_000_000_000L + nanos;
    }

    /// <summary>Seconds part.</summary>
    public static uint Seconds(ulong ntp) => (uint)(ntp >> 32);

    /// <summary>Fraction part.</summary>
    public static uint Fraction(ulong ntp) => (uint)ntp;
}

/// <summary>
/// <see cref="IMasterClock"/> over <see cref="Stopwatch"/>: the NTP/PTP epoch is captured from the wall clock once at
/// construction and then advanced by monotonic elapsed time only.
/// </summary>
public sealed class MasterClock : IMasterClock
{
    private readonly long _startTicks = Stopwatch.GetTimestamp();
    private readonly long _unixNanosAtStart;

    /// <summary>Creates a clock anchored to the current wall-clock time.</summary>
    public MasterClock()
        : this((DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).Ticks * 100L)
    {
    }

    /// <summary>Creates a clock whose origin corresponds to the given Unix nanoseconds (tests).</summary>
    public MasterClock(long unixNanosAtStart)
    {
        _unixNanosAtStart = unixNanosAtStart;
    }

    private long ElapsedNanos => (long)((Stopwatch.GetTimestamp() - _startTicks) * (1_000_000_000.0 / Stopwatch.Frequency));

    /// <inheritdoc />
    public long NowNanos => ElapsedNanos;

    /// <summary>Unix nanoseconds on this clock's timeline (not wall-clock corrected).</summary>
    public long UnixNanos => _unixNanosAtStart + ElapsedNanos;

    /// <inheritdoc />
    public ulong NtpNow => NtpTimestamp.FromUnixNanos(UnixNanos);

    /// <inheritdoc />
    public ulong PtpNowNanos => (ulong)UnixNanos;
}
