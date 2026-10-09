// Port of connect/src/model.rs.
using Spotify.Player.Proto;

namespace Librespot.Connect;

/// <summary>Request for loading playback (upstream <c>LoadRequest</c>).</summary>
public sealed record LoadRequest
{
    internal PlayContext Context { get; }
    public LoadRequestOptions Options { get; }

    private LoadRequest(PlayContext context, LoadRequestOptions options)
    {
        Context = context;
        Options = options;
    }

    /// <summary>
    /// Create a load request from a <c>context_uri</c> (see <c>SpClient.GetContextAsync</c> for supported uris).
    /// Equivalent to Web API <c>/me/player/play</c> with <c>context_uri</c>.
    /// </summary>
    public static LoadRequest FromContextUri(string contextUri, LoadRequestOptions options) => new(new PlayContext.Uri(contextUri), options);

    /// <summary>Create a load request from a set of tracks. Equivalent to Web API <c>/me/player/play</c> with <c>uris</c>.</summary>
    public static LoadRequest FromTracks(IReadOnlyList<string> tracks, LoadRequestOptions options) => new(new PlayContext.Tracks(tracks), options);
}

internal abstract record PlayContext
{
    private PlayContext()
    {
    }

    public sealed record Uri(string Value) : PlayContext;

    public sealed record Tracks(IReadOnlyList<string> Uris) : PlayContext;
}

/// <summary>The parameters for creating a load request.</summary>
public sealed record LoadRequestOptions
{
    /// <summary>Whether the given tracks should immediately start playing, or just be initially loaded.</summary>
    public bool StartPlaying { get; init; }

    /// <summary>Start the playback at a specific point of the track (ms). Values beyond the duration start at the beginning.</summary>
    public uint SeekTo { get; init; }

    /// <summary>Options that decide how the context starts playing.</summary>
    public LoadContextOptions? ContextOptions { get; init; }

    /// <summary>
    /// Decides the starting position in the given context. If the item does not exist or is out of range,
    /// playback starts at the beginning. If null and shuffle is on, a random track is played, otherwise the first.
    /// </summary>
    public PlayingTrack? PlayingTrack { get; init; }
}

/// <summary>The options which decide how the playback is started (mutually exclusive variants).</summary>
public abstract record LoadContextOptions
{
    private LoadContextOptions()
    {
    }

    /// <summary>Starts the context with options.</summary>
    public sealed record WithOptions(Options Options) : LoadContextOptions;

    /// <summary>Starts the playback as the autoplay variant of the context (like finishing a context and continuing with similar tracks).</summary>
    public sealed record Autoplay : LoadContextOptions;
}

/// <summary>The available options that indicate how to start the context.</summary>
public sealed record Options
{
    public bool Shuffle { get; init; }
    public bool Repeat { get; init; }
    public bool RepeatTrack { get; init; }

    /// <summary>Upstream <c>From&lt;ContextPlayerOptionOverrides&gt;</c>.</summary>
    public static Options From(ContextPlayerOptionOverrides value) => new()
    {
        Shuffle = value.HasShufflingContext && value.ShufflingContext,
        Repeat = value.HasRepeatingContext && value.RepeatingContext,
        RepeatTrack = value.HasRepeatingTrack && value.RepeatingTrack,
    };
}

/// <summary>An item that represents a track to play.</summary>
public abstract record PlayingTrack
{
    private PlayingTrack()
    {
    }

    /// <summary>The track at a given index.</summary>
    public sealed record Index(uint Value) : PlayingTrack;

    /// <summary>The uri of a track.</summary>
    public sealed record Uri(string Value) : PlayingTrack;

    /// <summary>An internal Spotify identifier (not the id in the uri); used by official clients for sorting/skipping.</summary>
    public sealed record Uid(string Value) : PlayingTrack;
}

/// <summary>
/// The playback status as tracked by spirc (upstream <c>SpircPlayStatus</c>, model.rs). Added by package
/// <c>connect-state</c> because <c>ConnectState.SetStatus</c> consumes it.
/// </summary>
internal abstract record SpircPlayStatus
{
    private SpircPlayStatus()
    {
    }

    /// <summary>Nothing is loaded.</summary>
    public sealed record Stopped : SpircPlayStatus;

    /// <summary>A track is loading and will start playing at <paramref name="PositionMs"/>.</summary>
    public sealed record LoadingPlay(uint PositionMs) : SpircPlayStatus;

    /// <summary>A track is loading and will be paused at <paramref name="PositionMs"/>.</summary>
    public sealed record LoadingPause(uint PositionMs) : SpircPlayStatus;

    /// <summary>Playing since <paramref name="NominalStartTime"/> (ms timestamp of position 0).</summary>
    public sealed record Playing(long NominalStartTime, bool PreloadingOfNextTrackTriggered) : SpircPlayStatus;

    /// <summary>Paused at <paramref name="PositionMs"/>.</summary>
    public sealed record Paused(uint PositionMs, bool PreloadingOfNextTrackTriggered) : SpircPlayStatus;
}
