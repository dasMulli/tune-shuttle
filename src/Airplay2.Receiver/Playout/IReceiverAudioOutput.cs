using Airplay2.Sender.Session;

namespace Airplay2.Receiver.Playout;

/// <summary>
/// The library's only view of the outside world for audio (SPEC-receiver.md 5.5): the bridge implements it against the
/// output manager (8.3), tests with a recorder. The table of 5.5 says which sender action causes which call.
/// </summary>
public interface IReceiverAudioOutput
{
    /// <summary>Released frames (S16 interleaved stereo, 44.1 kHz) in play order; may block briefly for backpressure (SPEC-receiver.md 5.3, 5.5).</summary>
    ValueTask WriteAsync(ReadOnlyMemory<short> frames, CancellationToken cancellationToken);

    /// <summary><c>SETRATEANCHORTIME rate 0</c>, a realtime stream silent for 2 s, or an immediate <c>FLUSHBUFFERED</c>: the group player's pause (SPEC-receiver.md 5.5).</summary>
    Task PauseAsync(CancellationToken cancellationToken);

    /// <summary><c>SETRATEANCHORTIME rate 1</c>, or the first realtime frame released while paused (SPEC-receiver.md 5.1, 5.5).</summary>
    Task ResumeAsync(CancellationToken cancellationToken);

    /// <summary><c>FLUSH</c> or an immediate <c>FLUSHBUFFERED</c>: the receivers re-anchor on the next packet (SPEC-receiver.md 5.5).</summary>
    Task FlushAsync(CancellationToken cancellationToken);

    /// <summary><c>SET_PARAMETER volume</c> mapped to the master level G (SPEC-receiver.md R9, 5.5); the bridge supplies the source <c>airplay</c> (8.3).</summary>
    Task SetMasterLevelAsync(int level, CancellationToken cancellationToken);

    /// <summary>
    /// DMAP text, artwork and progress with one <c>rtptime</c> (SPEC-receiver.md 5.5): once all three arrived or 500 ms
    /// after the first, with <paramref name="elapsed"/> derived from the next frame the buffer will release.
    /// </summary>
    Task SetNowPlayingAsync(TrackMetadata metadata, ReadOnlyMemory<byte>? artwork, string? artworkContentType, TimeSpan elapsed, CancellationToken cancellationToken);

    /// <summary>The session ended; the output is no longer owned by the receiver (SPEC-receiver.md 5.5: pause first, then unowned).</summary>
    Task ReleaseAsync(CancellationToken cancellationToken);

    /// <summary>Frames queued in the output ahead of its pacer: the existing <c>GroupPlayer.QueuedFrames</c> lifted onto <c>IGroupPlayer</c> (SPEC-receiver.md 5.4, 8.6); 0 when no output is connected.</summary>
    int QueuedFrames { get; }
}
