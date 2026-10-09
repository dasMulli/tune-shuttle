namespace Airplay2.Sender.Streams;

/// <summary>
/// A stream transport whose anchors carry a fixed lead <c>L</c>: the packet due at <c>T</c> plays at <c>T + L</c>
/// (SPEC-audio.md 1.5 for the realtime syncs, 4.3 for SETRATEANCHORTIME; SPEC.md D1). A group player must use the same
/// <c>L</c> for its "frame at the speaker", so it reads it here instead of testing for a transport type.
/// </summary>
/// <remarks>
/// Implemented by <see cref="BufferedStreamTransport"/>. <see cref="RealtimeStreamTransport"/> already has a public
/// <c>LatencyFrames</c> and can implement it by declaration alone.
/// </remarks>
public interface IStreamLatency
{
    /// <summary><c>L</c> in frames at the stream's sample rate.</summary>
    int LatencyFrames { get; }
}
