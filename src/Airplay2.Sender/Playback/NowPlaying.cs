using Airplay2.Sender.Rtp;
using Airplay2.Sender.Session;

namespace Airplay2.Sender.Playback;

/// <summary>
/// The metadata rtptime bookkeeping of SPEC-audio.md 7 (SPEC-control.md 5.4, SPEC.md D12), reimplemented from the spec's
/// description (owntone's code is GPL and was not consulted): a track's <c>start</c> is the RTP timestamp of its first
/// sample; the progress body is <c>display/current/end</c> with <c>display = start - 15360</c> (startup, 0.35 s early) or
/// <c>start - 30720</c> (track switch, 0.70 s early, "so the display is ready"), <c>current = max(cur_pos, start)</c>
/// (never before the start) and <c>end = start + duration</c> (or <c>current</c> when the duration is unknown); text,
/// artwork and progress all carry <c>RTP-Info: rtptime=start</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>start</c> is derived from the <b>write</b> position: the sender sends a track's metadata as soon as its first packet
/// is queued, not when it plays (SPEC-audio.md 7), and the bridge reports the track position of the samples it writes next
/// (SPEC-audio.md 8.4: "rtptime = next_ts at the moment the first samples of the new track enter the ring"). So
/// <c>start = (timestamp the next written frame will get) - elapsed</c>; <c>cur_pos</c> is the frame at the loudspeakers
/// (<see cref="Timeline.FrameAtSpeaker"/>). All arithmetic is modulo 2^32 like the RTP timestamps.
/// </para>
/// <para>
/// When queued audio is dropped (pause, flush) the frames written afterwards continue the track where the producer was,
/// at an earlier timestamp: <see cref="Shift"/> moves <c>start</c> by the same amount so the mapping stays right.
/// "Startup" versus "track switch" is decided per receiver: its first track since it joined is a startup (a mid-track
/// join included), every later one a switch.
/// </para>
/// </remarks>
internal sealed class NowPlayingTracker
{
    /// <summary>How far before <c>start</c> the display value lies on a receiver's first track (0.35 s).</summary>
    public const uint StartupDisplayLead = 15360;

    /// <summary>How far before <c>start</c> the display value lies on a track switch (0.70 s).</summary>
    public const uint SwitchDisplayLead = 30720;

    /// <summary>The current track, or null.</summary>
    public NowPlayingTrack? Track { get; private set; }

    /// <summary>Records a new track whose sample at <paramref name="elapsed"/> gets timestamp <paramref name="writeTimestamp"/>.</summary>
    public NowPlayingTrack SetTrack(TrackMetadata metadata, ReadOnlyMemory<byte>? artwork, string? artworkContentType, uint writeTimestamp, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var elapsedFrames = elapsed <= TimeSpan.Zero ? 0 : ToFrames(elapsed);
        uint? duration = metadata.Duration is { } d && d > TimeSpan.Zero ? (uint)Math.Min(ToFrames(d), uint.MaxValue) : null;
        Track = new NowPlayingTrack(metadata, artwork, artworkContentType, unchecked(writeTimestamp - (uint)elapsedFrames), duration);
        return Track;
    }

    /// <summary>Moves the current track's start by <paramref name="frames"/> (negative: earlier).</summary>
    public void Shift(long frames)
    {
        if (Track is { } track && frames != 0)
        {
            Track = track with { Start = unchecked((uint)(track.Start + frames)) };
        }
    }

    /// <summary>Forgets the track.</summary>
    public void Clear() => Track = null;

    /// <summary>The progress triple for <paramref name="track"/> with the frame at the speakers now and a display lead.</summary>
    public static ProgressInfo Progress(NowPlayingTrack track, uint speakerFrame, uint displayLead)
    {
        ArgumentNullException.ThrowIfNull(track);
        var start = track.Start;
        var current = unchecked((int)(speakerFrame - start)) > 0 ? speakerFrame : start;
        var end = track.DurationFrames is { } duration ? unchecked(start + duration) : current;
        return new ProgressInfo(unchecked(start - displayLead), current, end, RtpTime: start);
    }

    /// <summary>Frames at 44.1 kHz, rounded to the nearest frame.</summary>
    public static long ToFrames(TimeSpan time) => (long)Math.Round(time.Ticks * (double)RealtimeStreamConstants.SampleRate / TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero);
}

/// <summary>A track as the receivers are told about it.</summary>
/// <param name="Metadata">Text metadata.</param>
/// <param name="Artwork">Image bytes, or null.</param>
/// <param name="ArtworkContentType">image/jpeg or image/png.</param>
/// <param name="Start">RTP timestamp of the track's first sample.</param>
/// <param name="DurationFrames">Length in frames, null when unknown.</param>
internal sealed record NowPlayingTrack(TrackMetadata Metadata, ReadOnlyMemory<byte>? Artwork, string? ArtworkContentType, uint Start, uint? DurationFrames);
