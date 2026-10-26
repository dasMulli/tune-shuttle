namespace Airplay2.Receiver.Playout;

/// <summary>
/// Timing options of the <see cref="PlayoutBuffer"/> (SPEC-receiver.md R6, 5.3-5.4): the names of section 14
/// (<c>Lpipe</c>, <c>AnchorLead</c>, <c>LateThreshold</c>, <c>DriftThreshold</c>).
/// </summary>
public sealed record PlayoutOptions
{
    /// <summary>The output sample rate; every stream is decoded to it (SPEC-receiver.md R3).</summary>
    public const int SampleRate = 44100;

    /// <summary>Frames per realtime packet and per buffered ALAC block (SPEC-receiver.md R3).</summary>
    public const int FramesPerPacket = 352;

    /// <summary>The receiver-side lead the group player's receivers add (SPEC-audio.md 1.5; SPEC-receiver.md 5.4).</summary>
    public const int ReceiverLeadFrames = 11025;

    /// <summary>The default sender latency <c>L</c> in frames (SPEC-audio.md 1.5: 77 175 = 1.75 s).</summary>
    public const int DefaultLatencyFrames = 77175;

    /// <summary>The default prefill of the group player's ring (SPEC-receiver.md 5.4: 8 820 frames).</summary>
    public const int DefaultPrefillFrames = 8820;

    /// <summary>
    /// <c>Lpipe</c>, the output's write-to-audible delay in frames (SPEC-receiver.md 5.4): <c>LatencyFrames + 11025 +
    /// PrefillFrames</c>, about 2.2 s at the defaults. <see cref="LpipeFor"/> computes it for other settings.
    /// </summary>
    public int Lpipe { get; init; } = LpipeFor(DefaultLatencyFrames, DefaultPrefillFrames);

    /// <summary>The group player's prefill; the release thread never writes more than <c>PrefillFrames + 352</c> frames ahead of the pacer (SPEC-receiver.md 5.4).</summary>
    public int PrefillFrames { get; init; } = DefaultPrefillFrames;

    /// <summary>Without a PTP offset the first anchor maps to <c>now + AnchorLead</c> (SPEC-receiver.md R6, 5.3; <c>Bridge:Airplay:AnchorLeadMs</c>, default 300 ms).</summary>
    public TimeSpan AnchorLead { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>A frame is late, dropped and counted when <c>now &gt; release + LateThreshold</c> (SPEC-receiver.md R6: 200 ms).</summary>
    public TimeSpan LateThreshold { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Drift beyond this is corrected by dropping or repeating single frames (SPEC-receiver.md R6, 5.3: 20 ms).</summary>
    public TimeSpan DriftThreshold { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>How often the buffer compares written frames with elapsed time (SPEC-receiver.md 5.3: every 10 s).</summary>
    public TimeSpan DriftCheckInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A realtime stream silent for this long pauses the output (SPEC-receiver.md 5.5: 2 s).</summary>
    public TimeSpan SilencePause { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>A realtime packet whose RTP timestamp is more than this ahead of the next due frame is corrupt and dropped (SPEC-receiver.md 5.1: 2 s).</summary>
    public TimeSpan MaxTimestampAhead { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary><c>Lpipe</c> for a sender latency and prefill (SPEC-receiver.md 5.4): <c>latencyFrames + 11025 + prefillFrames</c>.</summary>
    public static int LpipeFor(int latencyFrames, int prefillFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(latencyFrames);
        ArgumentOutOfRangeException.ThrowIfNegative(prefillFrames);
        return latencyFrames + ReceiverLeadFrames + prefillFrames;
    }
}
