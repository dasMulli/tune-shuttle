using Airplay2.Sender.Rtp;

namespace Airplay2.Sender.Playback;

/// <summary>
/// The shared RTP timeline of a group (SPEC-audio.md 1.5, 3.1, 8): one sequence counter, one timestamp counter, and the
/// anchor that maps timestamps to instants of the master clock. Timestamps only ever advance; a flush announces the
/// next values and re-anchors (SPEC-audio.md 3.4: "do not rewind timestamps").
/// </summary>
/// <remarks>
/// <para>
/// Anchoring (SPEC-audio.md 1.5): <see cref="Anchor"/> fixes <c>T0</c> for the packet with timestamp
/// <c>ts0 = NextTimestamp</c>; packet <c>k</c> after it is due at <c>T0 + 352 k / 44100</c> s, computed from the frame
/// distance in exact integer arithmetic so the deadlines never drift against the clock. The sync packets describe the
/// same instants (<see cref="DeadlineOf"/>), which makes <c>now_frame</c> and <c>T</c> advance in lock-step as the
/// receivers require (SPEC-audio.md 2.3).
/// </para>
/// <para>
/// The frame distance from the anchor is kept as a 64-bit counter next to the 32-bit RTP timestamp, so the deadlines keep
/// increasing after 2^32 frames (27 h) of uninterrupted playback since the last anchor; the 32-bit timestamp simply wraps.
/// </para>
/// <para>Not thread-safe: the group player mutates it on the pacer thread and reads it under its own lock.</para>
/// </remarks>
public sealed class Timeline
{
    private const long NanosPerSecond = 1_000_000_000L;

    // Frames from AnchorTimestamp to NextTimestamp, unwrapped (64 bits: never ambiguous, SPEC-audio.md 3.4 "never rewind").
    private long _framesSinceAnchor;

    /// <summary>Creates a timeline starting at random sequence/timestamp values (SPEC-audio.md 1.2: start values random).</summary>
    public Timeline(IMasterClock clock, int latencyFrames = RealtimeStreamConstants.DefaultLatencyFrames)
        : this(clock, latencyFrames, RandomStart(out var timestamp), timestamp)
    {
    }

    /// <summary>Creates a timeline with given start values (tests, probes).</summary>
    public Timeline(IMasterClock clock, int latencyFrames, ushort firstSequence, uint firstTimestamp)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegative(latencyFrames);
        Clock = clock;
        LatencyFrames = latencyFrames;
        NextSequence = firstSequence;
        NextTimestamp = firstTimestamp;
    }

    /// <summary>The clock that paces this timeline.</summary>
    public IMasterClock Clock { get; }

    /// <summary>L in frames.</summary>
    public int LatencyFrames { get; }

    /// <summary>Sequence number of the next packet.</summary>
    public ushort NextSequence { get; private set; }

    /// <summary>RTP timestamp of the next packet.</summary>
    public uint NextTimestamp { get; private set; }

    /// <summary>The frame that should be at the loudspeakers when the next packet is due: <c>NextTimestamp - L</c>.</summary>
    public uint NowFrame => unchecked(NextTimestamp - (uint)LatencyFrames);

    /// <summary>True between <see cref="Anchor"/> and <see cref="ClearAnchor"/>.</summary>
    public bool IsAnchored { get; private set; }

    /// <summary><c>T0</c>: the monotonic instant (<see cref="IMasterClock.NowNanos"/>) at which <see cref="AnchorTimestamp"/> is due.</summary>
    public long AnchorNanos { get; private set; }

    /// <summary><c>ts0</c>: the timestamp that was next when the timeline was anchored.</summary>
    public uint AnchorTimestamp { get; private set; }

    /// <summary>The instant the next packet is due (anchored timelines only).</summary>
    public long NextDeadline => DeadlineOf(NextTimestamp);

    /// <summary>Consumes one packet's worth of sequence/timestamp.</summary>
    public (ushort Sequence, uint Timestamp) Advance()
    {
        var r = (NextSequence, NextTimestamp);
        NextSequence = unchecked((ushort)(NextSequence + 1));
        NextTimestamp = unchecked(NextTimestamp + RealtimeStreamConstants.FramesPerPacket);
        _framesSinceAnchor += RealtimeStreamConstants.FramesPerPacket;
        return r;
    }

    /// <summary>
    /// Jumps the timestamp forward by <paramref name="frames"/> (never backwards); the sequence and the anchor stay. On an
    /// anchored timeline the next packet's deadline moves forward with it: the skipped frames become a silent gap at the
    /// receivers while their <c>(now_frame, T)</c> relation stays valid (the pacer uses this to recover from a stall
    /// longer than the receivers can absorb).
    /// </summary>
    public void Skip(uint frames)
    {
        NextTimestamp = unchecked(NextTimestamp + frames);
        _framesSinceAnchor += frames;
    }

    /// <summary>Anchors the timeline: the next packet (<see cref="NextTimestamp"/>) is due at <paramref name="monotonicNanos"/>.</summary>
    public void Anchor(long monotonicNanos)
    {
        AnchorNanos = monotonicNanos;
        AnchorTimestamp = NextTimestamp;
        _framesSinceAnchor = 0;
        IsAnchored = true;
    }

    /// <summary>Forgets the anchor (pause, flush, silence timeout); the counters stay.</summary>
    public void ClearAnchor() => IsAnchored = false;

    /// <summary>
    /// The instant the packet with <paramref name="timestamp"/> is due: <c>T0 + (ts - ts0) / 44100</c> s, exact to the
    /// nanosecond. <paramref name="timestamp"/> is read relative to <see cref="NextTimestamp"/> (within ±2^31 frames, i.e.
    /// ±13.5 h of it), so the result stays correct however long the timeline has run since the anchor.
    /// </summary>
    /// <exception cref="InvalidOperationException">The timeline is not anchored.</exception>
    public long DeadlineOf(uint timestamp)
    {
        if (!IsAnchored)
        {
            throw new InvalidOperationException("The timeline is not anchored.");
        }

        var frames = _framesSinceAnchor + unchecked((int)(timestamp - NextTimestamp));
        return AnchorNanos + (long)((Int128)frames * NanosPerSecond / RealtimeStreamConstants.SampleRate);
    }

    /// <summary>
    /// The frame at the loudspeakers at <paramref name="monotonicNanos"/> (SPEC-audio.md 7, <c>cur_pos</c>): on an anchored
    /// timeline <c>ts0 + (t - T0) * 44100 - L</c>; otherwise (paused) <see cref="NowFrame"/>, where playback resumes.
    /// </summary>
    public uint FrameAtSpeaker(long monotonicNanos)
    {
        if (!IsAnchored)
        {
            return NowFrame;
        }

        var frames = (long)((Int128)(monotonicNanos - AnchorNanos) * RealtimeStreamConstants.SampleRate / NanosPerSecond);
        return unchecked((uint)(AnchorTimestamp + frames) - (uint)LatencyFrames);
    }

    private static ushort RandomStart(out uint timestamp)
    {
        Span<byte> r = stackalloc byte[6];
        System.Security.Cryptography.RandomNumberGenerator.Fill(r);
        timestamp = BitConverter.ToUInt32(r[2..]);
        return BitConverter.ToUInt16(r);
    }
}
