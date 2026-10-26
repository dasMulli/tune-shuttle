using System.Threading.Channels;
using Airplay2.Sender.Session;

namespace Bridge.Service.Spotify;

/// <summary>
/// What the Spotify side needs from the AirPlay output (SPEC-audio.md 8.4): flush, now-playing, the Spotify volume as
/// the master level G, the sink's frame counter, and back the other way the receivers' buttons and level changes made in
/// the web app. Implemented by <see cref="Output.OutputManager"/>; tests stub it.
/// </summary>
public interface IPlaybackOutput
{
    /// <summary>Frames librespot has written to the sink so far (to map an event to a write position).</summary>
    long FramesWritten { get; }

    /// <summary>Receiver buttons forwarded from the event channel (<c>play</c>, <c>paus</c>, <c>nitm</c>, <c>pitm</c>).</summary>
    ChannelReader<string> RemoteCommands { get; }

    /// <summary>Master level changes made outside Spotify (web app, receiver buttons) that the Spotify app should show.</summary>
    ChannelReader<int> SpotifyVolumeUpdates { get; }

    /// <summary>Discards the queued audio of every receiver (seek, manual skip) and re-anchors on the next write.</summary>
    Task FlushAsync(CancellationToken cancellationToken);

    /// <summary>Sends text, artwork and progress for the track whose sample at <see cref="NowPlaying.Elapsed"/> is written next.</summary>
    Task SetNowPlayingAsync(NowPlaying nowPlaying, CancellationToken cancellationToken);

    /// <summary>
    /// A source's flush (SPEC-receiver.md 8.2): applied only while <paramref name="source"/> may command the output. The
    /// default forwards to <see cref="FlushAsync(CancellationToken)"/> (stubs without a source switch).
    /// </summary>
    Task FlushAsync(Output.AudioSource source, CancellationToken cancellationToken) => FlushAsync(cancellationToken);

    /// <summary>
    /// A source's now-playing (SPEC-receiver.md 8.2): applied only while <paramref name="source"/> may command the output.
    /// The default forwards to <see cref="SetNowPlayingAsync(NowPlaying, CancellationToken)"/> (stubs without a source switch).
    /// </summary>
    Task SetNowPlayingAsync(Output.AudioSource source, NowPlaying nowPlaying, CancellationToken cancellationToken) => SetNowPlayingAsync(nowPlaying, cancellationToken);

    /// <summary>
    /// A source's volume moved: applies it as G of the output (a single receiver's r_i) with the level's source tag,
    /// <see cref="Output.OutputManager.SourceSpotify"/> or <see cref="Output.OutputManager.SourceAirplay"/> (SPEC-receiver.md R9, 8.6).
    /// </summary>
    Task SetSourceLevelAsync(int level, string source, CancellationToken cancellationToken);

    /// <summary>G of the current output (a single receiver's r_i), or null without an output.</summary>
    Task<int?> CurrentLevelAsync(CancellationToken cancellationToken);
}

/// <summary>A now-playing update for the receivers (SPEC-audio.md 7).</summary>
/// <param name="Metadata">Title, artist, album, duration.</param>
/// <param name="Artwork">Cover image bytes, or null.</param>
/// <param name="ArtworkContentType"><c>image/jpeg</c> or <c>image/png</c>.</param>
/// <param name="Elapsed">Track position of the next sample written to the sink.</param>
public sealed record NowPlaying(TrackMetadata Metadata, ReadOnlyMemory<byte>? Artwork, string? ArtworkContentType, TimeSpan Elapsed);
