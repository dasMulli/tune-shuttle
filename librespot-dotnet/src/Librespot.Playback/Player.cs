// Port of playback/src/player.rs (public contract: Player facade, PlayerEvent, PlayerCommand, QueueTrack,
// SinkStatus, NormalisationData, dB helpers). The ~2.4k-line state machine (PlayerInternal, PlayerState,
// PlayerTrackLoader, normalisation, Subfile) is package `playback-player`.
using System.Threading.Channels;
using Librespot.Core;
using Librespot.Metadata.Audio;
using Librespot.Playback.AudioBackend;
using Librespot.Playback.Mixer;

namespace Librespot.Playback;

public enum SinkStatus
{
    Running,
    Closed,
    TemporarilyClosed,
}

/// <summary>Upstream <c>SinkEventCallback = Box&lt;dyn Fn(SinkStatus) + Send&gt;</c>.</summary>
public delegate void SinkEventCallback(SinkStatus status);

/// <summary>Represents a track in the queue with its URI and provider.</summary>
public sealed record QueueTrack(string Uri, string Provider);

/// <summary>
/// Events emitted by the player (upstream <c>enum PlayerEvent</c>); a closed record hierarchy. Subscribe with
/// <see cref="Player.GetPlayerEventChannel"/>.
/// </summary>
public abstract record PlayerEvent
{
    private PlayerEvent()
    {
    }

    /// <summary>Play request id changed.</summary>
    public sealed record PlayRequestIdChanged(ulong PlayRequestId) : PlayerEvent;

    /// <summary>Fired when the player is stopped (e.g. by issuing a "stop" command to the player).</summary>
    public sealed record Stopped(ulong PlayRequestId, SpotifyUri TrackId) : PlayerEvent;

    /// <summary>The player is delayed by loading a track.</summary>
    public sealed record Loading(ulong PlayRequestId, SpotifyUri TrackId, uint PositionMs) : PlayerEvent;

    /// <summary>The player is preloading a track.</summary>
    public sealed record Preloading(SpotifyUri TrackId) : PlayerEvent;

    /// <summary>
    /// The player is playing a track. Issued at the start of playback or whenever the position must be
    /// communicated because it is out of sync: start of a track, un-pausing, after a seek, after a buffer-underrun.
    /// </summary>
    public sealed record Playing(ulong PlayRequestId, SpotifyUri TrackId, uint PositionMs) : PlayerEvent;

    /// <summary>The player entered a paused state.</summary>
    public sealed record Paused(ulong PlayRequestId, SpotifyUri TrackId, uint PositionMs) : PlayerEvent;

    /// <summary>The player thinks it's a good idea to issue a preload command for the next track now. Intended for spirc.</summary>
    public sealed record TimeToPreloadNextTrack(ulong PlayRequestId, SpotifyUri TrackId) : PlayerEvent;

    /// <summary>The player reached the end of a track. Intended for spirc, which responds by issuing another command.</summary>
    public sealed record EndOfTrack(ulong PlayRequestId, SpotifyUri TrackId) : PlayerEvent;

    /// <summary>The player was unable to load the requested track.</summary>
    public sealed record Unavailable(ulong PlayRequestId, SpotifyUri TrackId) : PlayerEvent;

    /// <summary>The mixer volume was set to a new level.</summary>
    public sealed record VolumeChanged(ushort Volume) : PlayerEvent;

    public sealed record PositionCorrection(ulong PlayRequestId, SpotifyUri TrackId, uint PositionMs) : PlayerEvent;

    /// <summary>Requires <see cref="PlayerConfig.PositionUpdateInterval"/>; sent periodically while playing.</summary>
    public sealed record PositionChanged(ulong PlayRequestId, SpotifyUri TrackId, uint PositionMs) : PlayerEvent;

    public sealed record Seeked(ulong PlayRequestId, SpotifyUri TrackId, uint PositionMs) : PlayerEvent;

    public sealed record TrackChanged(AudioItem AudioItem) : PlayerEvent;

    public sealed record SessionConnected(string ConnectionId, string UserName) : PlayerEvent;

    public sealed record SessionDisconnected(string ConnectionId, string UserName) : PlayerEvent;

    public sealed record SessionClientChanged(string ClientId, string ClientName, string ClientBrandName, string ClientModelName) : PlayerEvent;

    public sealed record ShuffleChanged(bool Shuffle) : PlayerEvent;

    public sealed record RepeatChanged(bool Context, bool Track) : PlayerEvent;

    public sealed record AutoPlayChanged(bool AutoPlay) : PlayerEvent;

    public sealed record FilterExplicitContentChanged(bool Filter) : PlayerEvent;

    /// <summary>Fired when the queue is set or context is loaded with its track list.</summary>
    public sealed record SetQueue(string ContextUri, QueueTrack? CurrentTrack, IReadOnlyList<QueueTrack> NextTracks, IReadOnlyList<QueueTrack> PrevTracks) : PlayerEvent;

    /// <summary>Upstream <c>get_play_request_id</c>.</summary>
    public ulong? GetPlayRequestId() => this switch
    {
        Loading e => e.PlayRequestId,
        Unavailable e => e.PlayRequestId,
        Playing e => e.PlayRequestId,
        TimeToPreloadNextTrack e => e.PlayRequestId,
        EndOfTrack e => e.PlayRequestId,
        Paused e => e.PlayRequestId,
        Stopped e => e.PlayRequestId,
        PositionCorrection e => e.PlayRequestId,
        PositionChanged e => e.PlayRequestId,
        Seeked e => e.PlayRequestId,
        _ => null,
    };
}

/// <summary>Upstream <c>PlayerEventChannel = mpsc::UnboundedReceiver&lt;PlayerEvent&gt;</c>.</summary>
public sealed class PlayerEventChannel
{
    internal PlayerEventChannel(ChannelReader<PlayerEvent> reader)
    {
        Reader = reader;
    }

    public ChannelReader<PlayerEvent> Reader { get; }

    public IAsyncEnumerable<PlayerEvent> ReadAllAsync(CancellationToken cancellationToken = default) => Reader.ReadAllAsync(cancellationToken);
}

/// <summary>Commands sent from the <see cref="Player"/> facade to the player thread (upstream <c>enum PlayerCommand</c>, private).</summary>
internal abstract record PlayerCommand
{
    private PlayerCommand()
    {
    }

    public sealed record Load(SpotifyUri TrackId, bool StartPlaying, uint PositionMs) : PlayerCommand;
    public sealed record Preload(SpotifyUri TrackId) : PlayerCommand;
    public sealed record Play : PlayerCommand;
    public sealed record Pause : PlayerCommand;
    public sealed record Stop : PlayerCommand;
    public sealed record Seek(uint PositionMs) : PlayerCommand;
    public sealed record SetSession(Session Session) : PlayerCommand;
    public sealed record AddEventSender(ChannelWriter<PlayerEvent> Sender) : PlayerCommand;
    public sealed record SetSinkEventCallback(SinkEventCallback? Callback) : PlayerCommand;
    public sealed record EmitVolumeChangedEvent(ushort Volume) : PlayerCommand;
    public sealed record SetAutoNormaliseAsAlbum(bool Setting) : PlayerCommand;
    public sealed record EmitSessionDisconnectedEvent(string ConnectionId, string UserName) : PlayerCommand;
    public sealed record EmitSessionConnectedEvent(string ConnectionId, string UserName) : PlayerCommand;
    public sealed record EmitSessionClientChangedEvent(string ClientId, string ClientName, string ClientBrandName, string ClientModelName) : PlayerCommand;
    public sealed record EmitFilterExplicitContentChangedEvent(bool Filter) : PlayerCommand;
    public sealed record EmitShuffleChangedEvent(bool Shuffle) : PlayerCommand;
    public sealed record EmitRepeatChangedEvent(bool Context, bool Track) : PlayerCommand;
    public sealed record EmitAutoPlayChangedEvent(bool AutoPlay) : PlayerCommand;
    public sealed record EmitSetQueueEvent(string ContextUri, QueueTrack? CurrentTrack, IReadOnlyList<QueueTrack> NextTracks, IReadOnlyList<QueueTrack> PrevTracks) : PlayerCommand;
}

/// <summary>Spotify-provided loudness data for a track/album (upstream <c>NormalisationData</c>).</summary>
public sealed record NormalisationData(double TrackGainDb, double TrackPeak, double AlbumGainDb, double AlbumPeak)
{
    public static NormalisationData Default => new(0.0, 1.0, 0.0, 1.0);

    /// <summary>Upstream <c>parse_from_ogg(file)</c>: reads the Spotify gain block at offset 144 of the Ogg file.</summary>
    public static NormalisationData ParseFromOgg(Stream file) => throw new NotImplementedException("TODO(port: playback-player)");

    /// <summary>Upstream <c>get_factor(config, data)</c>.</summary>
    public static double GetFactor(PlayerConfig config, NormalisationData data) => throw new NotImplementedException("TODO(port: playback-player)");
}

/// <summary>
/// The player: owns a dedicated thread running the playback state machine and exposes a command API.
/// Create with <see cref="Create"/>; dispose to stop the thread.
/// </summary>
public sealed class Player : IDisposable
{
    public const double DbVoltageRatio = 20.0;
    public const double PcmAt0Dbfs = 1.0;

    /// <summary>Spotify inserts a custom Ogg packet at the start with custom metadata values; the real stream starts here.</summary>
    internal const long SpotifyOggHeaderEnd = 0xa7;

    private readonly ChannelWriter<PlayerCommand> _commands;

    private Player(ChannelWriter<PlayerCommand> commands)
    {
        _commands = commands;
    }

    /// <summary>
    /// Upstream <c>Player::new(config, session, volume_getter, sink_builder)</c>: spawns the player thread.
    /// <paramref name="sinkBuilder"/> is invoked on that thread.
    /// </summary>
    public static Player Create(PlayerConfig config, Session session, IVolumeGetter volumeGetter, Func<ISink> sinkBuilder) =>
        throw new NotImplementedException("TODO(port: playback-player)");

    public static double DbToRatio(double db) => Math.Pow(10.0, db / DbVoltageRatio);

    public static double RatioToDb(double ratio) => Math.Log10(ratio) * DbVoltageRatio;

    public static double DurationToCoefficient(TimeSpan duration) =>
        Math.Exp(-1.0 / (duration.TotalSeconds * AudioConstants.SamplesPerSecond));

    public static TimeSpan CoefficientToDuration(double coefficient) =>
        TimeSpan.FromSeconds(-1.0 / Math.Log(coefficient) / AudioConstants.SamplesPerSecond);

    /// <summary>True once the player thread has stopped (upstream <c>is_invalid</c>).</summary>
    public bool IsInvalid => throw new NotImplementedException("TODO(port: playback-player)");

    private void Command(PlayerCommand cmd)
    {
        if (!_commands.TryWrite(cmd))
        {
            throw LibrespotException.Internal("Player thread has already stopped");
        }
    }

    public void Load(SpotifyUri trackId, bool startPlaying, uint positionMs) => Command(new PlayerCommand.Load(trackId, startPlaying, positionMs));
    public void Preload(SpotifyUri trackId) => Command(new PlayerCommand.Preload(trackId));
    public void Play() => Command(new PlayerCommand.Play());
    public void Pause() => Command(new PlayerCommand.Pause());
    public void Stop() => Command(new PlayerCommand.Stop());
    public void Seek(uint positionMs) => Command(new PlayerCommand.Seek(positionMs));
    public void SetSession(Session session) => Command(new PlayerCommand.SetSession(session));

    /// <summary>Registers a new event receiver; every receiver gets every event.</summary>
    public PlayerEventChannel GetPlayerEventChannel()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<PlayerEvent>(new UnboundedChannelOptions { SingleReader = true });
        Command(new PlayerCommand.AddEventSender(channel.Writer));
        return new PlayerEventChannel(channel.Reader);
    }

    /// <summary>Upstream <c>await_end_of_track</c>: completes on the next EndOfTrack or Stopped event.</summary>
    public async Task AwaitEndOfTrackAsync(CancellationToken cancellationToken = default)
    {
        var channel = GetPlayerEventChannel();
        await foreach (var e in channel.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (e is PlayerEvent.EndOfTrack or PlayerEvent.Stopped)
            {
                return;
            }
        }
    }

    public void SetSinkEventCallback(SinkEventCallback? callback) => Command(new PlayerCommand.SetSinkEventCallback(callback));
    public void EmitVolumeChangedEvent(ushort volume) => Command(new PlayerCommand.EmitVolumeChangedEvent(volume));
    public void SetAutoNormaliseAsAlbum(bool setting) => Command(new PlayerCommand.SetAutoNormaliseAsAlbum(setting));
    public void EmitFilterExplicitContentChangedEvent(bool filter) => Command(new PlayerCommand.EmitFilterExplicitContentChangedEvent(filter));
    public void EmitSessionConnectedEvent(string connectionId, string userName) => Command(new PlayerCommand.EmitSessionConnectedEvent(connectionId, userName));
    public void EmitSessionDisconnectedEvent(string connectionId, string userName) => Command(new PlayerCommand.EmitSessionDisconnectedEvent(connectionId, userName));
    public void EmitSessionClientChangedEvent(string clientId, string clientName, string clientBrandName, string clientModelName) => Command(new PlayerCommand.EmitSessionClientChangedEvent(clientId, clientName, clientBrandName, clientModelName));
    public void EmitShuffleChangedEvent(bool shuffle) => Command(new PlayerCommand.EmitShuffleChangedEvent(shuffle));
    public void EmitRepeatChangedEvent(bool context, bool track) => Command(new PlayerCommand.EmitRepeatChangedEvent(context, track));
    public void EmitAutoPlayChangedEvent(bool autoPlay) => Command(new PlayerCommand.EmitAutoPlayChangedEvent(autoPlay));
    public void EmitSetQueueEvent(string contextUri, QueueTrack? currentTrack, IReadOnlyList<QueueTrack> nextTracks, IReadOnlyList<QueueTrack> prevTracks) => Command(new PlayerCommand.EmitSetQueueEvent(contextUri, currentTrack, nextTracks, prevTracks));

    /// <summary>Upstream <c>Drop</c>: completes the command channel and joins the player thread.</summary>
    public void Dispose()
    {
        _commands.TryComplete();
        // TODO(port: playback-player): join the thread.
    }
}
