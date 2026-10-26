using Airplay2.Receiver.Playout;
using Airplay2.Receiver.Timing;
using Airplay2.Sender.Session;

namespace Airplay2.Receiver;

/// <summary>
/// What <see cref="AirplayReceiver.Events"/> reports to the host (SPEC-receiver.md 4.3-4.9, 5.3, 8.3): the bridge's
/// <c>AirplaySource</c> turns these into output claims, playback views and diagnostics. Raised in the order the sender's
/// requests were handled; the channel drops the oldest event when the host falls behind.
/// </summary>
public abstract record ReceiverEvent
{
    /// <summary>A sender completed a session SETUP on a connection (SPEC-receiver.md 4.3); no output is claimed yet (R8).</summary>
    /// <param name="Sender">The sender's name, model and address.</param>
    public sealed record SenderConnected(SenderInfo Sender) : ReceiverEvent;

    /// <summary>
    /// A stream SETUP completed (SPEC-receiver.md 4.6): the claim of R8. The host pauses the other source and routes the
    /// output to the receiver; a second sender's stream SETUP ends the first session (<see cref="SenderGone"/>) first.
    /// </summary>
    /// <param name="Sender">The sender.</param>
    /// <param name="StreamType">Realtime (96) or buffered (103).</param>
    /// <param name="Format">The decoder selected: <c>ALAC/44100/16/2</c> or <c>AAC-LC/44100/2</c> (R3).</param>
    public sealed record SenderStreamStarted(SenderInfo Sender, StreamType StreamType, string Format) : ReceiverEvent;

    /// <summary>A <c>SET_PARAMETER volume</c> arrived (SPEC-receiver.md 4.8, R9): the dB value mapped to a 0..100 level with <c>VolumeMapping.DbToLevel</c>.</summary>
    /// <param name="Level">The master level G to apply (0..100).</param>
    /// <param name="Db">The wire value in dB (<c>-144</c> is mute).</param>
    public sealed record SenderVolume(int Level, double Db) : ReceiverEvent;

    /// <summary>DMAP text metadata arrived (SPEC-receiver.md 4.8, R10).</summary>
    /// <param name="Item">The decoded <c>mlit</c> item.</param>
    /// <param name="RtpTime">The <c>RTP-Info rtptime</c> key the artwork and progress of the same track carry.</param>
    public sealed record SenderMetadata(DmapItem Item, uint RtpTime) : ReceiverEvent;

    /// <summary>Artwork arrived (SPEC-receiver.md 4.8; at most 1 MiB, larger images are dropped before this is raised).</summary>
    /// <param name="Bytes">The image bytes.</param>
    /// <param name="ContentType"><c>image/jpeg</c> or <c>image/png</c>.</param>
    /// <param name="RtpTime">The <c>RTP-Info rtptime</c> key.</param>
    public sealed record SenderArtwork(ReadOnlyMemory<byte> Bytes, string ContentType, uint RtpTime) : ReceiverEvent;

    /// <summary>A <c>progress: a/b/c</c> parameter arrived (SPEC-receiver.md 4.8, SPEC-control.md 5.4).</summary>
    /// <param name="Progress">Start, current and end RTP times with the <c>rtptime</c> key.</param>
    public sealed record SenderProgress(ProgressInfo Progress) : ReceiverEvent;

    /// <summary>The sender paused: <c>SETRATEANCHORTIME rate 0</c>, or a realtime stream silent for 2 s (SPEC-receiver.md 4.7, 5.5).</summary>
    public sealed record SenderPaused : ReceiverEvent;

    /// <summary>The sender resumed: <c>SETRATEANCHORTIME rate 1</c>, or the first realtime frame released while paused (SPEC-receiver.md 4.7, 5.1).</summary>
    public sealed record SenderResumed : ReceiverEvent;

    /// <summary>A flush: <c>FLUSH</c> (realtime) or <c>FLUSHBUFFERED</c> (SPEC-receiver.md 4.5, 4.7).</summary>
    /// <param name="Deferred">True for the deferred <c>FLUSHBUFFERED</c> form (a splice, the output never notices); false when the output was flushed.</param>
    public sealed record SenderFlush(bool Deferred) : ReceiverEvent;

    /// <summary>
    /// The session ended (SPEC-receiver.md 4.9): <c>TEARDOWN</c>, connection closed, liveness timeout (R16), eviction by a second
    /// sender or by the host (<see cref="AirplayReceiver.EndSessionAsync"/>), or the receiver stopped. The output has been released (5.5).
    /// </summary>
    /// <param name="Reason">Why, for the log and Diagnostics.</param>
    public sealed record SenderGone(string Reason) : ReceiverEvent;

    /// <summary>Periodic statistics while a stream is set up (SPEC-receiver.md 5.3, 6.2): the Diagnostics snapshot of 9.1.</summary>
    /// <param name="Playout">The playout buffer's snapshot.</param>
    /// <param name="Ptp">The PTP slave's snapshot, or null in anchor-only timing (R5).</param>
    public sealed record Statistics(PlayoutStatistics Playout, PtpSlaveStatistics? Ptp) : ReceiverEvent;
}
