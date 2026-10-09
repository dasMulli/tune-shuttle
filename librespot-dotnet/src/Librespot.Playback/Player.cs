// Port of playback/src/player.rs.
//
// Layout (all from player.rs): the public facade (Player, SinkStatus, SinkEventCallback, QueueTrack, PlayerEvent,
// PlayerEventChannel, NormalisationData, dB helpers), the private command enum (PlayerCommand), the state machine
// that runs on the dedicated "player" thread (PlayerInternal, PlayerState, PlayerPreload, PlayerLoadedTrackData),
// the track loader (PlayerTrackLoader) and the Subfile stream wrapper.
//
// Deviations from upstream, each also commented at the site:
//  * PlayerInternal is a synchronous loop on its own thread instead of a Future driven by block_on; when it is not
//    playing and no command arrived, it blocks until a command or a loader result is available (upstream:
//    Poll::Pending until woken by either).
//  * Loads run on the thread pool (upstream: one thread per load running block_on); their tasks are tracked and
//    awaited on shutdown like upstream's load_handles.
//  * Rust drops decoders (and with them the audio files) when a state is replaced; here they are disposed
//    explicitly at the same points (SetState / SetPreload / TrackLoader.Abandon).
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Librespot.Audio;
using Librespot.Audio.Fetch;
using Librespot.Core;
using Librespot.Core.Logging;
using Librespot.Metadata;
using Librespot.Metadata.Audio;
using Librespot.Playback.AudioBackend;
using Librespot.Playback.Decoder;
using Librespot.Playback.Mixer;
using Microsoft.Extensions.Logging;

namespace Librespot.Playback;

/// <summary>State of the audio sink as reported to the <see cref="SinkEventCallback"/>.</summary>
public enum SinkStatus
{
    /// <summary>The sink is open and playing.</summary>
    Running,

    /// <summary>The sink is closed.</summary>
    Closed,

    /// <summary>The sink is closed for a moment (e.g. between tracks without gapless playback).</summary>
    TemporarilyClosed,
}

/// <summary>Upstream <c>SinkEventCallback = Box&lt;dyn Fn(SinkStatus) + Send&gt;</c>.</summary>
/// <param name="status">The new sink status.</param>
public delegate void SinkEventCallback(SinkStatus status);

/// <summary>Represents a track in the queue with its URI and provider.</summary>
/// <param name="Uri">The track URI.</param>
/// <param name="Provider">The provider (e.g. <c>context</c>, <c>queue</c>, <c>autoplay</c>).</param>
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

    /// <summary>The playback position drifted (packets were skipped or playback lags) and is reported anew.</summary>
    public sealed record PositionCorrection(ulong PlayRequestId, SpotifyUri TrackId, uint PositionMs) : PlayerEvent;

    /// <summary>
    /// Requires <see cref="PlayerConfig.PositionUpdateInterval"/> to be set. Once set this event will be sent
    /// periodically while playing the track to inform about the current playback position.
    /// </summary>
    public sealed record PositionChanged(ulong PlayRequestId, SpotifyUri TrackId, uint PositionMs) : PlayerEvent;

    /// <summary>The player seeked to a new position.</summary>
    public sealed record Seeked(ulong PlayRequestId, SpotifyUri TrackId, uint PositionMs) : PlayerEvent;

    /// <summary>A new track was loaded; carries its metadata.</summary>
    public sealed record TrackChanged(AudioItem AudioItem) : PlayerEvent;

    /// <summary>The session connected.</summary>
    public sealed record SessionConnected(string ConnectionId, string UserName) : PlayerEvent;

    /// <summary>The session disconnected.</summary>
    public sealed record SessionDisconnected(string ConnectionId, string UserName) : PlayerEvent;

    /// <summary>The controlling client changed.</summary>
    public sealed record SessionClientChanged(string ClientId, string ClientName, string ClientBrandName, string ClientModelName) : PlayerEvent;

    /// <summary>Shuffle was switched on or off.</summary>
    public sealed record ShuffleChanged(bool Shuffle) : PlayerEvent;

    /// <summary>Repeat (context / track) changed.</summary>
    public sealed record RepeatChanged(bool Context, bool Track) : PlayerEvent;

    /// <summary>Autoplay was switched on or off.</summary>
    public sealed record AutoPlayChanged(bool AutoPlay) : PlayerEvent;

    /// <summary>The explicit content filter setting changed.</summary>
    public sealed record FilterExplicitContentChanged(bool Filter) : PlayerEvent;

    /// <summary>Fired when the queue is set or context is loaded with its track list.</summary>
    public sealed record SetQueue(string ContextUri, QueueTrack? CurrentTrack, IReadOnlyList<QueueTrack> NextTracks, IReadOnlyList<QueueTrack> PrevTracks) : PlayerEvent;

    /// <summary>Upstream <c>get_play_request_id</c>.</summary>
    /// <remarks>Like upstream, <see cref="PositionChanged"/> is not included.</remarks>
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
        Seeked e => e.PlayRequestId,
        _ => null,
    };
}

/// <summary>Upstream <c>PlayerEventChannel = mpsc::UnboundedReceiver&lt;PlayerEvent&gt;</c>.</summary>
/// <remarks>
/// Upstream drops the sender when the receiver is dropped; a .NET channel cannot observe that, so dispose the
/// channel when you stop reading it and the player stops sending to it.
/// </remarks>
public sealed class PlayerEventChannel : IDisposable
{
    private readonly ChannelWriter<PlayerEvent> _writer;

    internal PlayerEventChannel(ChannelReader<PlayerEvent> reader, ChannelWriter<PlayerEvent> writer)
    {
        Reader = reader;
        _writer = writer;
    }

    /// <summary>The events; completes when the player shuts down.</summary>
    public ChannelReader<PlayerEvent> Reader { get; }

    /// <summary>Reads all events until the player shuts down or <paramref name="cancellationToken"/> fires.</summary>
    public IAsyncEnumerable<PlayerEvent> ReadAllAsync(CancellationToken cancellationToken = default) => Reader.ReadAllAsync(cancellationToken);

    /// <summary>Unsubscribes: the player drops this channel the next time it sends an event (upstream: dropping the receiver).</summary>
    public void Dispose() => _writer.TryComplete();
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

    /// <summary>Upstream <c>impl fmt::Debug for PlayerCommand</c> (used by the <c>command=</c> debug log).</summary>
    public static string Describe(PlayerCommand command) => command switch
    {
        Load l => $"Load({l.TrackId}, {Bool(l.StartPlaying)}, {l.PositionMs})",
        Preload p => $"Preload({p.TrackId})",
        Play => "Play",
        Pause => "Pause",
        Stop => "Stop",
        Seek s => $"Seek({s.PositionMs})",
        SetSession => "SetSession",
        AddEventSender => "AddEventSender",
        SetSinkEventCallback => "SetSinkEventCallback",
        EmitVolumeChangedEvent v => $"EmitVolumeChangedEvent({v.Volume})",
        SetAutoNormaliseAsAlbum a => $"SetAutoNormaliseAsAlbum({Bool(a.Setting)})",
        EmitFilterExplicitContentChangedEvent f => $"EmitFilterExplicitContentChangedEvent({Bool(f.Filter)})",
        EmitSessionConnectedEvent c => $"EmitSessionConnectedEvent(\"{c.ConnectionId}\", \"{c.UserName}\")",
        EmitSessionDisconnectedEvent d => $"EmitSessionDisconnectedEvent(\"{d.ConnectionId}\", \"{d.UserName}\")",
        EmitSessionClientChangedEvent c =>
            $"EmitSessionClientChangedEvent(\"{c.ClientId}\", \"{c.ClientName}\", \"{c.ClientBrandName}\", \"{c.ClientModelName}\")",
        EmitShuffleChangedEvent s => $"EmitShuffleChangedEvent({Bool(s.Shuffle)})",
        EmitRepeatChangedEvent r => $"EmitRepeatChangedEvent({Bool(r.Context)}, {Bool(r.Track)})",
        EmitAutoPlayChangedEvent a => $"EmitAutoPlayChangedEvent({Bool(a.AutoPlay)})",
        EmitSetQueueEvent q => $"EmitSetQueueEvent(\"{q.ContextUri}\", {q.NextTracks.Count}, {q.PrevTracks.Count})",
        _ => command.GetType().Name,
    };

    private static string Bool(bool value) => value ? "true" : "false";
}

/// <summary>Spotify-provided loudness data for a track/album (upstream <c>NormalisationData</c>).</summary>
/// <remarks>
/// Spotify provides these as <c>f32</c>, but audio metadata can contain up to <c>f64</c>. Also, this negates the
/// need for casting during sample processing.
/// </remarks>
/// <param name="TrackGainDb">ReplayGain track gain in dB.</param>
/// <param name="TrackPeak">Track peak amplitude (1.0 = 0 dBFS).</param>
/// <param name="AlbumGainDb">ReplayGain album gain in dB.</param>
/// <param name="AlbumPeak">Album peak amplitude (1.0 = 0 dBFS).</param>
public sealed record NormalisationData(double TrackGainDb, double TrackPeak, double AlbumGainDb, double AlbumPeak)
{
    private const long SpotifyNormalizationHeaderStartOffset = 144;
    private const int NormalisationDataSize = 16;

    /// <summary>No gain, peak at 0 dBFS (upstream <c>Default</c>).</summary>
    public static NormalisationData Default => new(0.0, 1.0, 0.0, 1.0);

    /// <summary>Upstream <c>parse_from_ogg(file)</c>: reads the Spotify gain block at offset 144 of the Ogg file.</summary>
    /// <param name="file">The (decrypted) Spotify Ogg Vorbis file; it is left positioned after the block.</param>
    /// <returns>The track and album gain/peak values (four little-endian <c>f32</c>s).</returns>
    /// <exception cref="IOException">Reading failed or the file is shorter than 160 bytes.</exception>
    public static NormalisationData ParseFromOgg(Stream file)
    {
        var newpos = file.Seek(SpotifyNormalizationHeaderStartOffset, SeekOrigin.Begin);
        if (newpos != SpotifyNormalizationHeaderStartOffset)
        {
            PlayerLog.Log.LogError(
                "NormalisationData::parse_from_file seeking to {Offset} but position is now {NewPos}",
                SpotifyNormalizationHeaderStartOffset,
                newpos);

            PlayerLog.Log.LogError("Falling back to default (non-track and non-album) normalisation data.");

            return Default;
        }

        Span<byte> buf = stackalloc byte[NormalisationDataSize];

        file.ReadExactly(buf);

        var trackGainDb = (double)BinaryPrimitives.ReadSingleLittleEndian(buf[0..4]);
        var trackPeak = (double)BinaryPrimitives.ReadSingleLittleEndian(buf[4..8]);
        var albumGainDb = (double)BinaryPrimitives.ReadSingleLittleEndian(buf[8..12]);
        var albumPeak = (double)BinaryPrimitives.ReadSingleLittleEndian(buf[12..16]);

        return new NormalisationData(trackGainDb, trackPeak, albumGainDb, albumPeak);
    }

    /// <summary>Upstream <c>get_factor(config, data)</c>: the linear gain to apply for the configured normalisation.</summary>
    /// <param name="config">The player configuration (normalisation on/off, type, method, pregain, threshold).</param>
    /// <param name="data">The track's normalisation data.</param>
    /// <returns>1.0 when normalisation is off, otherwise the gain factor.</returns>
    public static double GetFactor(PlayerConfig config, NormalisationData data)
    {
        if (!config.Normalisation)
        {
            return 1.0;
        }

        var (gainDb, gainPeak) = config.NormalisationType == NormalisationType.Album
            ? (data.AlbumGainDb, data.AlbumPeak)
            : (data.TrackGainDb, data.TrackPeak);

        // As per the ReplayGain 1.0 & 2.0 (proposed) spec:
        // https://wiki.hydrogenaud.io/index.php?title=ReplayGain_1.0_specification#Clipping_prevention
        // https://wiki.hydrogenaud.io/index.php?title=ReplayGain_2.0_specification#Clipping_prevention
        double normalisationFactor;
        if (config.NormalisationMethod == NormalisationMethod.Basic)
        {
            // For Basic Normalisation, factor = min(ratio of (ReplayGain + PreGain), 1.0 / peak level).
            // https://wiki.hydrogenaud.io/index.php?title=ReplayGain_1.0_specification#Peak_amplitude
            // https://wiki.hydrogenaud.io/index.php?title=ReplayGain_2.0_specification#Peak_amplitude
            // We then limit that to 1.0 as not to exceed dBFS (0.0 dB).
            // (double.MinNumber has f64::min's NaN semantics.)
            var factor = double.MinNumber(
                Player.DbToRatio(gainDb + config.NormalisationPregainDb),
                Player.PcmAt0Dbfs / gainPeak);

            if (factor > Player.PcmAt0Dbfs)
            {
                PlayerLog.Log.LogInformation(
                    "Lowering gain by {Db:F2} dB for the duration of this track to avoid potentially exceeding dBFS.",
                    Player.RatioToDb(factor));

                normalisationFactor = Player.PcmAt0Dbfs;
            }
            else
            {
                normalisationFactor = factor;
            }
        }
        else
        {
            // For Dynamic Normalisation it's up to the player to decide,
            // factor = ratio of (ReplayGain + PreGain).
            // We then let the dynamic limiter handle gain reduction.
            var factor = Player.DbToRatio(gainDb + config.NormalisationPregainDb);
            var thresholdRatio = Player.DbToRatio(config.NormalisationThresholdDbfs);

            if (factor > Player.PcmAt0Dbfs)
            {
                var factorDb = gainDb + config.NormalisationPregainDb;
                var limitingDb = factorDb + Math.Abs(config.NormalisationThresholdDbfs);

                PlayerLog.Log.LogWarning(
                    "This track may exceed dBFS by {FactorDb:F2} dB and be subject to {LimitingDb:F2} dB of dynamic limiting at its peak.",
                    factorDb,
                    limitingDb);
            }
            else if (factor > thresholdRatio)
            {
                var limitingDb = gainDb + config.NormalisationPregainDb + Math.Abs(config.NormalisationThresholdDbfs);

                PlayerLog.Log.LogInformation(
                    "This track may be subject to {LimitingDb:F2} dB of dynamic limiting at its peak.",
                    limitingDb);
            }

            normalisationFactor = factor;
        }

        PlayerLog.Log.LogDebug("Normalisation Data: {Data}", data);
        PlayerLog.Log.LogDebug(
            "Calculated Normalisation Factor for {NormalisationType}: {Percent:F2}%",
            config.NormalisationType,
            normalisationFactor * 100.0);

        return normalisationFactor;
    }
}

/// <summary>The logger shared by every type ported from <c>player.rs</c> (one Rust module, one log target).</summary>
internal static class PlayerLog
{
    public static readonly ILogger Log = LibrespotLog.CreateLogger<Player>();

    /// <summary>Upstream <c>Display</c> of the error an operation returned.</summary>
    public static string Display(Exception e) => e is LibrespotException ? e.Message : LibrespotException.From(e).Message;

    /// <summary>
    /// Upstream <c>error!(..); exit(1)</c>: the player treats these states as unrecoverable and terminates the
    /// process, exactly like upstream.
    /// </summary>
    [DoesNotReturn]
    public static void Fatal(string message)
    {
        Log.LogError("{Message}", message);
        Environment.Exit(1);
        throw new UnreachableException(message);
    }
}

/// <summary>
/// The player: owns a dedicated thread running the playback state machine and exposes a command API.
/// Create with <see cref="Create(PlayerConfig, Session, IVolumeGetter, Func{ISink})"/>; dispose to stop the thread.
/// </summary>
public sealed class Player : IDisposable
{
    /// <summary>Upstream <c>DB_VOLTAGE_RATIO</c>.</summary>
    public const double DbVoltageRatio = 20.0;

    /// <summary>Upstream <c>PCM_AT_0DBFS</c>.</summary>
    public const double PcmAt0Dbfs = 1.0;

    /// <summary>
    /// Spotify inserts a custom Ogg packet at the start with custom metadata values, that you would otherwise expect
    /// in Vorbis comments. This packet isn't well-formed and players may balk at it; the real stream starts here.
    /// </summary>
    internal const long SpotifyOggHeaderEnd = 0xa7;

    /// <summary>Upstream <c>PRELOAD_NEXT_TRACK_BEFORE_END_DURATION_MS</c>.</summary>
    internal const uint PreloadNextTrackBeforeEndDurationMs = 30000;

    /// <summary>Upstream <c>PLAYER_COUNTER</c>.</summary>
    private static int s_playerCounter;

    private static ILogger Log => PlayerLog.Log;

    private readonly ChannelWriter<PlayerCommand> _commands;
    private readonly Thread _threadHandle;
    private int _disposed;

    private Player(ChannelWriter<PlayerCommand> commands, Thread threadHandle)
    {
        _commands = commands;
        _threadHandle = threadHandle;
    }

    /// <summary>
    /// Upstream <c>Player::new(config, session, volume_getter, sink_builder)</c>: spawns the player thread.
    /// <paramref name="sinkBuilder"/> is invoked on that thread.
    /// </summary>
    /// <param name="config">Player configuration.</param>
    /// <param name="session">The session used to load tracks (replace it later with <see cref="SetSession"/>).</param>
    /// <param name="volumeGetter">Source of the software volume attenuation.</param>
    /// <param name="sinkBuilder">Creates the audio sink; called once on the player thread.</param>
    public static Player Create(PlayerConfig config, Session session, IVolumeGetter volumeGetter, Func<ISink> sinkBuilder) =>
        Create(config, session, volumeGetter, sinkBuilder, static (s, c, l) => new PlayerTrackLoader(s, c, l), TimeProvider.System);

    /// <summary>Test seam of <see cref="Create(PlayerConfig, Session, IVolumeGetter, Func{ISink})"/>: injects the track loader and clock.</summary>
    internal static Player Create(
        PlayerConfig config,
        Session session,
        IVolumeGetter volumeGetter,
        Func<ISink> sinkBuilder,
        TrackLoaderFactory trackLoaderFactory,
        TimeProvider timeProvider)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<PlayerCommand>(new UnboundedChannelOptions { SingleReader = true });

        if (config.Normalisation)
        {
            Log.LogDebug("Normalisation Type: {NormalisationType}", config.NormalisationType);
            Log.LogDebug("Normalisation Pregain: {PregainDb:F1} dB", config.NormalisationPregainDb);
            Log.LogDebug("Normalisation Threshold: {ThresholdDbfs:F1} dBFS", config.NormalisationThresholdDbfs);
            Log.LogDebug("Normalisation Method: {NormalisationMethod}", config.NormalisationMethod);

            if (config.NormalisationMethod == NormalisationMethod.Dynamic)
            {
                // as_millis() has rounding errors (truncates)
                Log.LogDebug("Normalisation Attack: {AttackMs:F0} ms", CoefficientToDuration(config.NormalisationAttackCf).TotalSeconds * 1000.0);
                Log.LogDebug("Normalisation Release: {ReleaseMs:F0} ms", CoefficientToDuration(config.NormalisationReleaseCf).TotalSeconds * 1000.0);
                Log.LogDebug("Normalisation Knee: {KneeDb} dB", config.NormalisationKneeDb);
            }
        }

        var thread = new Thread(() =>
        {
            try
            {
                var playerId = Interlocked.Increment(ref s_playerCounter) - 1;
                Log.LogDebug("new Player [{PlayerId}]", playerId);

                var converter = new Converter(config.Ditherer);
                var normalisationKneeFactor = 1.0 / (8.0 * config.NormalisationKneeDb);

                // Upstream TODO: it would be neat if we could watch for added or modified files in the
                // specified directories, and dynamically update the lookup. Currently, a new player
                // must be created for any new local files to be playable.
                var localFileLookup = LocalFileLookup.Create(config.LocalFileDirectories);

                var playerInternal = new PlayerInternal(
                    session,
                    config,
                    channel.Reader,
                    sinkBuilder(),
                    volumeGetter,
                    converter,
                    normalisationKneeFactor,
                    playerId,
                    localFileLookup,
                    trackLoaderFactory,
                    timeProvider);

                playerInternal.Run();

                Log.LogDebug("PlayerInternal thread finished.");
            }
            catch (Exception e)
            {
                // Upstream: a panic ends the thread and is reported when the thread is joined.
                Log.LogError(e, "Player thread Error: {Error}", e.Message);
            }
            finally
            {
                // Upstream: the receiver is gone once the thread ends, so further commands fail.
                channel.Writer.TryComplete();
            }
        })
        {
            IsBackground = true,
            Name = "player",
        };

        thread.Start();

        return new Player(channel.Writer, thread);
    }

    /// <summary>Upstream <c>db_to_ratio</c>.</summary>
    public static double DbToRatio(double db) => Math.Pow(10.0, db / DbVoltageRatio);

    /// <summary>Upstream <c>ratio_to_db</c>.</summary>
    public static double RatioToDb(double ratio) => Math.Log10(ratio) * DbVoltageRatio;

    /// <summary>Upstream <c>duration_to_coefficient</c>: the one-pole smoothing coefficient for a time constant.</summary>
    public static double DurationToCoefficient(TimeSpan duration) =>
        Math.Exp(-1.0 / (duration.TotalSeconds * AudioConstants.SamplesPerSecond));

    /// <summary>Upstream <c>coefficient_to_duration</c>: the inverse of <see cref="DurationToCoefficient"/>.</summary>
    public static TimeSpan CoefficientToDuration(double coefficient) =>
        TimeSpan.FromSeconds(-1.0 / Math.Log(coefficient) / AudioConstants.SamplesPerSecond);

    /// <summary>True once the player thread has stopped (upstream <c>is_invalid</c>).</summary>
    public bool IsInvalid => !_threadHandle.IsAlive;

    private bool Command(PlayerCommand cmd)
    {
        if (!_commands.TryWrite(cmd))
        {
            Log.LogError("Player Commands Error: {Error}", "channel closed");
            return false;
        }

        return true;
    }

    /// <summary>Loads a track, optionally starting playback at <paramref name="positionMs"/>.</summary>
    public void Load(SpotifyUri trackId, bool startPlaying, uint positionMs) => Command(new PlayerCommand.Load(trackId, startPlaying, positionMs));

    /// <summary>Preloads a track so a later <see cref="Load"/> of it starts without delay.</summary>
    public void Preload(SpotifyUri trackId) => Command(new PlayerCommand.Preload(trackId));

    /// <summary>Resumes playback.</summary>
    public void Play() => Command(new PlayerCommand.Play());

    /// <summary>Pauses playback.</summary>
    public void Pause() => Command(new PlayerCommand.Pause());

    /// <summary>Stops playback.</summary>
    public void Stop() => Command(new PlayerCommand.Stop());

    /// <summary>Seeks the current track.</summary>
    public void Seek(uint positionMs) => Command(new PlayerCommand.Seek(positionMs));

    /// <summary>Replaces the session used for loading tracks (after a reconnect).</summary>
    public void SetSession(Session session) => Command(new PlayerCommand.SetSession(session));

    /// <summary>Registers a new event receiver; every receiver gets every event.</summary>
    public PlayerEventChannel GetPlayerEventChannel()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<PlayerEvent>(new UnboundedChannelOptions { SingleReader = true });
        if (!Command(new PlayerCommand.AddEventSender(channel.Writer)))
        {
            // Upstream: the command (and with it the sender) is dropped, so the receiver ends immediately.
            channel.Writer.TryComplete();
        }

        return new PlayerEventChannel(channel.Reader, channel.Writer);
    }

    /// <summary>Upstream <c>await_end_of_track</c>: completes on the next EndOfTrack or Stopped event.</summary>
    public async Task AwaitEndOfTrackAsync(CancellationToken cancellationToken = default)
    {
        using var channel = GetPlayerEventChannel();
        await foreach (var e in channel.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (e is PlayerEvent.EndOfTrack or PlayerEvent.Stopped)
            {
                return;
            }
        }
    }

    /// <summary>Sets (or clears) the callback invoked when the sink opens or closes.</summary>
    public void SetSinkEventCallback(SinkEventCallback? callback) => Command(new PlayerCommand.SetSinkEventCallback(callback));

    /// <summary>Emits <see cref="PlayerEvent.VolumeChanged"/>.</summary>
    public void EmitVolumeChangedEvent(ushort volume) => Command(new PlayerCommand.EmitVolumeChangedEvent(volume));

    /// <summary>With <see cref="NormalisationType.Auto"/>: normalise as album (true) or per track (false).</summary>
    public void SetAutoNormaliseAsAlbum(bool setting) => Command(new PlayerCommand.SetAutoNormaliseAsAlbum(setting));

    /// <summary>Emits <see cref="PlayerEvent.FilterExplicitContentChanged"/>; skips the current track when it is explicit and filtering is on.</summary>
    public void EmitFilterExplicitContentChangedEvent(bool filter) => Command(new PlayerCommand.EmitFilterExplicitContentChangedEvent(filter));

    /// <summary>Emits <see cref="PlayerEvent.SessionConnected"/>.</summary>
    public void EmitSessionConnectedEvent(string connectionId, string userName) => Command(new PlayerCommand.EmitSessionConnectedEvent(connectionId, userName));

    /// <summary>Emits <see cref="PlayerEvent.SessionDisconnected"/>.</summary>
    public void EmitSessionDisconnectedEvent(string connectionId, string userName) => Command(new PlayerCommand.EmitSessionDisconnectedEvent(connectionId, userName));

    /// <summary>Emits <see cref="PlayerEvent.SessionClientChanged"/>.</summary>
    public void EmitSessionClientChangedEvent(string clientId, string clientName, string clientBrandName, string clientModelName) => Command(new PlayerCommand.EmitSessionClientChangedEvent(clientId, clientName, clientBrandName, clientModelName));

    /// <summary>Emits <see cref="PlayerEvent.ShuffleChanged"/>.</summary>
    public void EmitShuffleChangedEvent(bool shuffle) => Command(new PlayerCommand.EmitShuffleChangedEvent(shuffle));

    /// <summary>Emits <see cref="PlayerEvent.RepeatChanged"/>.</summary>
    public void EmitRepeatChangedEvent(bool context, bool track) => Command(new PlayerCommand.EmitRepeatChangedEvent(context, track));

    /// <summary>Emits <see cref="PlayerEvent.AutoPlayChanged"/>.</summary>
    public void EmitAutoPlayChangedEvent(bool autoPlay) => Command(new PlayerCommand.EmitAutoPlayChangedEvent(autoPlay));

    /// <summary>Emits <see cref="PlayerEvent.SetQueue"/>.</summary>
    public void EmitSetQueueEvent(string contextUri, QueueTrack? currentTrack, IReadOnlyList<QueueTrack> nextTracks, IReadOnlyList<QueueTrack> prevTracks) => Command(new PlayerCommand.EmitSetQueueEvent(contextUri, currentTrack, nextTracks, prevTracks));

    /// <summary>Upstream <c>Drop</c>: completes the command channel and joins the player thread.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Log.LogDebug("Shutting down player thread ...");
        _commands.TryComplete();

        // Joining from the player thread itself (e.g. from a sink callback) would deadlock.
        if (Thread.CurrentThread != _threadHandle)
        {
            _threadHandle.Join();
        }
    }
}

/// <summary>A loaded track ready to play (upstream <c>PlayerLoadedTrackData</c>). Owns the decoder.</summary>
internal sealed class PlayerLoadedTrackData : IDisposable
{
    public required IAudioDecoder Decoder { get; init; }

    public required NormalisationData NormalisationData { get; init; }

    public required StreamLoaderController StreamLoaderController { get; init; }

    public required AudioItem AudioItem { get; init; }

    public required int BytesPerSecond { get; init; }

    public required uint DurationMs { get; init; }

    public required uint StreamPositionMs { get; set; }

    public required bool IsExplicit { get; init; }

    /// <summary>Upstream: dropping the data drops the decoder and with it the audio file.</summary>
    public void Dispose() => Decoder.Dispose();
}

/// <summary>
/// A pending track load (upstream <c>Pin&lt;Box&lt;dyn FusedFuture&lt;Output = Result&lt;PlayerLoadedTrackData, ()&gt;&gt;&gt;&gt;</c>).
/// A null result is upstream's <c>Err(())</c>.
/// </summary>
internal sealed class TrackLoader
{
    private readonly Task<PlayerLoadedTrackData?> _task;

    public TrackLoader(Task<PlayerLoadedTrackData?> task)
    {
        _task = task;
    }

    /// <summary>The underlying task, for waiting.</summary>
    public Task Task => _task;

    /// <summary>Upstream <c>FusedFuture::is_terminated</c>: true once the result was taken (or the load abandoned).</summary>
    public bool IsTerminated { get; private set; }

    /// <summary>Upstream <c>poll</c>: false while pending; otherwise terminates and hands out the result.</summary>
    public bool TryPoll(out PlayerLoadedTrackData? result)
    {
        if (IsTerminated || !_task.IsCompleted)
        {
            result = null;
            return false;
        }

        IsTerminated = true;
        result = _task.IsCompletedSuccessfully ? _task.Result : null;
        return true;
    }

    /// <summary>Upstream: dropping the loader future. The load runs to completion and its result is disposed.</summary>
    public void Abandon()
    {
        if (IsTerminated)
        {
            return;
        }

        IsTerminated = true;
        _ = _task.ContinueWith(
            static t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    t.Result?.Dispose();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

/// <summary>Upstream <c>enum PlayerPreload</c>.</summary>
internal abstract class PlayerPreload
{
    private PlayerPreload()
    {
    }

    public sealed class None : PlayerPreload
    {
        public static readonly None Instance = new();
    }

    public sealed class Loading(SpotifyUri trackId, TrackLoader loader) : PlayerPreload
    {
        public SpotifyUri TrackId { get; } = trackId;

        public TrackLoader Loader { get; } = loader;
    }

    public sealed class Ready(SpotifyUri trackId, PlayerLoadedTrackData loadedTrack) : PlayerPreload
    {
        public SpotifyUri TrackId { get; } = trackId;

        public PlayerLoadedTrackData LoadedTrack { get; } = loadedTrack;
    }
}

/// <summary>Upstream <c>enum PlayerState</c>. <see cref="Active"/> groups the <c>Paused | Playing</c> variants.</summary>
internal abstract class PlayerState
{
    private PlayerState()
    {
    }

    /// <summary>Upstream <c>is_playing</c>.</summary>
    public bool IsPlaying()
    {
        switch (this)
        {
            case Stopped or EndOfTrack or Paused or Loading:
                return false;
            case Playing:
                return true;
            default:
                PlayerLog.Fatal("PlayerState::is_playing in invalid state");
                return false;
        }
    }

    /// <summary>Upstream <c>is_stopped</c>.</summary>
    public bool IsStopped() => this is Stopped;

    /// <summary>Upstream <c>is_loading</c>.</summary>
    public bool IsLoading() => this is Loading;

    /// <summary>Upstream <c>decoder()</c>.</summary>
    public IAudioDecoder? GetDecoder()
    {
        switch (this)
        {
            case Stopped or EndOfTrack or Loading:
                return null;
            case Active active:
                return active.Decoder;
            default:
                PlayerLog.Fatal("PlayerState::decoder in invalid state");
                return null;
        }
    }

    public sealed class Stopped : PlayerState
    {
        public static readonly Stopped Instance = new();

        public override string ToString() => "Stopped";
    }

    public sealed class Loading(SpotifyUri trackId, ulong playRequestId, bool startPlayback, TrackLoader loader) : PlayerState
    {
        public SpotifyUri TrackId { get; } = trackId;

        public ulong PlayRequestId { get; } = playRequestId;

        public bool StartPlayback { get; set; } = startPlayback;

        public TrackLoader Loader { get; } = loader;

        public override string ToString() => $"Loading {{ track_id: {TrackId}, play_request_id: {PlayRequestId} }}";
    }

    /// <summary>The fields <c>Paused</c> and <c>Playing</c> share.</summary>
    public abstract class Active : PlayerState
    {
        private protected Active(
            SpotifyUri trackId,
            ulong playRequestId,
            IAudioDecoder decoder,
            AudioItem audioItem,
            NormalisationData normalisationData,
            double normalisationFactor,
            StreamLoaderController streamLoaderController,
            int bytesPerSecond,
            uint durationMs,
            uint streamPositionMs,
            bool suggestedToPreloadNextTrack,
            bool isExplicit)
        {
            TrackId = trackId;
            PlayRequestId = playRequestId;
            Decoder = decoder;
            AudioItem = audioItem;
            NormalisationData = normalisationData;
            NormalisationFactor = normalisationFactor;
            StreamLoaderController = streamLoaderController;
            BytesPerSecond = bytesPerSecond;
            DurationMs = durationMs;
            StreamPositionMs = streamPositionMs;
            SuggestedToPreloadNextTrack = suggestedToPreloadNextTrack;
            IsExplicit = isExplicit;
        }

        private protected Active(Active other)
            : this(
                other.TrackId,
                other.PlayRequestId,
                other.Decoder,
                other.AudioItem,
                other.NormalisationData,
                other.NormalisationFactor,
                other.StreamLoaderController,
                other.BytesPerSecond,
                other.DurationMs,
                other.StreamPositionMs,
                other.SuggestedToPreloadNextTrack,
                other.IsExplicit)
        {
        }

        public SpotifyUri TrackId { get; }

        public ulong PlayRequestId { get; }

        public IAudioDecoder Decoder { get; }

        public AudioItem AudioItem { get; }

        public NormalisationData NormalisationData { get; }

        public double NormalisationFactor { get; }

        public StreamLoaderController StreamLoaderController { get; }

        public int BytesPerSecond { get; }

        public uint DurationMs { get; }

        public uint StreamPositionMs { get; set; }

        public bool SuggestedToPreloadNextTrack { get; set; }

        public bool IsExplicit { get; }

        /// <summary>Moves the track's data into a <see cref="PlayerLoadedTrackData"/>.</summary>
        public PlayerLoadedTrackData ToLoadedTrack() => new()
        {
            Decoder = Decoder,
            NormalisationData = NormalisationData,
            StreamLoaderController = StreamLoaderController,
            AudioItem = AudioItem,
            BytesPerSecond = BytesPerSecond,
            DurationMs = DurationMs,
            StreamPositionMs = StreamPositionMs,
            IsExplicit = IsExplicit,
        };
    }

    public sealed class Paused : Active
    {
        public Paused(
            SpotifyUri trackId,
            ulong playRequestId,
            IAudioDecoder decoder,
            AudioItem audioItem,
            NormalisationData normalisationData,
            double normalisationFactor,
            StreamLoaderController streamLoaderController,
            int bytesPerSecond,
            uint durationMs,
            uint streamPositionMs,
            bool suggestedToPreloadNextTrack,
            bool isExplicit)
            : base(trackId, playRequestId, decoder, audioItem, normalisationData, normalisationFactor, streamLoaderController, bytesPerSecond, durationMs, streamPositionMs, suggestedToPreloadNextTrack, isExplicit)
        {
        }

        /// <summary>Upstream <c>playing_to_paused</c>.</summary>
        public Paused(Playing playing)
            : base(playing)
        {
        }

        public override string ToString() => $"Paused {{ track_id: {TrackId}, play_request_id: {PlayRequestId} }}";
    }

    public sealed class Playing : Active
    {
        public Playing(
            SpotifyUri trackId,
            ulong playRequestId,
            IAudioDecoder decoder,
            AudioItem audioItem,
            NormalisationData normalisationData,
            double normalisationFactor,
            StreamLoaderController streamLoaderController,
            int bytesPerSecond,
            uint durationMs,
            uint streamPositionMs,
            long? reportedNominalStartTime,
            bool suggestedToPreloadNextTrack,
            bool isExplicit)
            : base(trackId, playRequestId, decoder, audioItem, normalisationData, normalisationFactor, streamLoaderController, bytesPerSecond, durationMs, streamPositionMs, suggestedToPreloadNextTrack, isExplicit)
        {
            ReportedNominalStartTime = reportedNominalStartTime;
        }

        /// <summary>Upstream <c>paused_to_playing</c>.</summary>
        public Playing(Paused paused, long? reportedNominalStartTime)
            : base(paused)
        {
            ReportedNominalStartTime = reportedNominalStartTime;
        }

        /// <summary>Upstream <c>Option&lt;Instant&gt;</c>, as a <see cref="TimeProvider"/> timestamp.</summary>
        public long? ReportedNominalStartTime { get; set; }

        public override string ToString() => $"Playing {{ track_id: {TrackId}, play_request_id: {PlayRequestId} }}";
    }

    public sealed class EndOfTrack(SpotifyUri trackId, ulong playRequestId, PlayerLoadedTrackData loadedTrack) : PlayerState
    {
        public SpotifyUri TrackId { get; } = trackId;

        public ulong PlayRequestId { get; } = playRequestId;

        public PlayerLoadedTrackData LoadedTrack { get; } = loadedTrack;

        public override string ToString() => $"EndOfTrack {{ track_id: {TrackId}, play_request_id: {PlayRequestId} }}";
    }

    public sealed class Invalid : PlayerState
    {
        public static readonly Invalid Instance = new();

        public override string ToString() => "Invalid";
    }
}

/// <summary>Loads a track for the player (upstream <c>PlayerTrackLoader::load_track</c>); null means the load failed.</summary>
internal interface ITrackLoader
{
    Task<PlayerLoadedTrackData?> LoadTrackAsync(SpotifyUri trackUri, uint positionMs, CancellationToken cancellationToken = default);
}

/// <summary>Creates the loader for one load from the player's current session, config and local file lookup.</summary>
internal delegate ITrackLoader TrackLoaderFactory(Session session, PlayerConfig config, LocalFileLookup localFileLookup);

/// <summary>Upstream <c>PlayerTrackLoader</c>.</summary>
internal sealed class PlayerTrackLoader(Session session, PlayerConfig config, LocalFileLookup localFileLookup) : ITrackLoader
{
    private static ILogger Log => PlayerLog.Log;

    private readonly Session _session = session;
    private readonly PlayerConfig _config = config;
    private readonly LocalFileLookup _localFileLookup = localFileLookup;

    /// <summary>Upstream <c>find_available_alternative</c>.</summary>
    private async Task<AudioItem?> FindAvailableAlternativeAsync(AudioItem audioItem, CancellationToken cancellationToken)
    {
        if (audioItem.Availability is { } reason)
        {
            Log.LogError("Track is unavailable: {Reason}", reason.Describe());
            return null;
        }

        if (audioItem.Files.Count != 0)
        {
            return audioItem;
        }

        if (audioItem.Alternatives is { } alternatives)
        {
            // Upstream: FuturesUnordered over all alternatives, the first available one wins and the rest are dropped.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var pending = alternatives.Select(altId => AudioItem.GetFileAsync(_session, altId, cts.Token)).ToList();
            try
            {
                while (pending.Count > 0)
                {
                    var done = await Task.WhenAny(pending).ConfigureAwait(false);
                    pending.Remove(done);

                    if (done.IsCompletedSuccessfully)
                    {
                        if (done.Result.Availability is null)
                        {
                            return done.Result;
                        }
                    }
                    else
                    {
                        // filter_map(|x| x.ok()): failed lookups are skipped.
                        _ = done.Exception;
                    }
                }

                return null;
            }
            finally
            {
                await cts.CancelAsync().ConfigureAwait(false);
                foreach (var task in pending)
                {
                    _ = task.ContinueWith(
                        static t => _ = t.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
        }

        Log.LogError("Track should be available, but no alternatives found.");
        return null;
    }

    /// <summary>Upstream <c>stream_data_rate</c>: nominal bytes per second of a format.</summary>
    internal static int? StreamDataRate(AudioFileFormat format)
    {
        float? kbps = format switch
        {
            AudioFileFormat.OggVorbis96 => 12f,
            AudioFileFormat.OggVorbis160 => 20f,
            AudioFileFormat.OggVorbis320 => 40f,
            AudioFileFormat.Mp3256 => 32f,
            AudioFileFormat.Mp3320 => 40f,
            AudioFileFormat.Mp3160 => 20f,
            AudioFileFormat.Mp396 => 12f,
            AudioFileFormat.Mp3160Enc => 20f,
            AudioFileFormat.Aac24 => 3f,
            AudioFileFormat.Aac48 => 6f,
            AudioFileFormat.Aac160 => 20f,
            AudioFileFormat.Aac320 => 40f,
            AudioFileFormat.Mp4128 => 16f,
            AudioFileFormat.Other5 => 40f,
            AudioFileFormat.FlacFlac => 112f, // assume 900 kbit/s on average
            AudioFileFormat.XheAac12 => 1.5f,
            AudioFileFormat.XheAac16 => 2f,
            AudioFileFormat.XheAac24 => 3f,
            AudioFileFormat.FlacFlac24Bit => 3f,
            _ => null, // not reachable upstream (exhaustive match over a closed enum)
        };

        if (kbps is not { } k)
        {
            return null;
        }

        var dataRate = k * 1024f;
        return (int)MathF.Ceiling(dataRate);
    }

    /// <summary>Upstream <c>load_track</c>.</summary>
    public Task<PlayerLoadedTrackData?> LoadTrackAsync(SpotifyUri trackUri, uint positionMs, CancellationToken cancellationToken = default)
    {
        switch (trackUri)
        {
            case SpotifyUri.Track or SpotifyUri.Episode:
                return LoadRemoteTrackAsync(trackUri, positionMs, cancellationToken);
            case SpotifyUri.Local:
                return LoadLocalTrackAsync(trackUri, positionMs);
            default:
                Log.LogError("Cannot handle load of track with URI: <{TrackUri}>", trackUri);
                return Task.FromResult<PlayerLoadedTrackData?>(null);
        }
    }

    /// <summary>Upstream <c>formats</c> table of <c>load_remote_track</c>, in order of preference.</summary>
    internal static AudioFileFormat[] FormatsFor(Bitrate bitrate) => bitrate switch
    {
        Bitrate.Bitrate96 =>
        [
            AudioFileFormat.OggVorbis96,
            AudioFileFormat.Mp396,
            AudioFileFormat.OggVorbis160,
            AudioFileFormat.Mp3160,
            AudioFileFormat.Mp3256,
            AudioFileFormat.OggVorbis320,
            AudioFileFormat.Mp3320,
        ],
        Bitrate.Bitrate160 =>
        [
            AudioFileFormat.OggVorbis160,
            AudioFileFormat.Mp3160,
            AudioFileFormat.OggVorbis96,
            AudioFileFormat.Mp396,
            AudioFileFormat.Mp3256,
            AudioFileFormat.OggVorbis320,
            AudioFileFormat.Mp3320,
        ],
        _ =>
        [
            AudioFileFormat.OggVorbis320,
            AudioFileFormat.Mp3320,
            AudioFileFormat.Mp3256,
            AudioFileFormat.OggVorbis160,
            AudioFileFormat.Mp3160,
            AudioFileFormat.OggVorbis96,
            AudioFileFormat.Mp396,
        ],
    };

    /// <summary>Upstream <c>load_remote_track</c>.</summary>
    private async Task<PlayerLoadedTrackData?> LoadRemoteTrackAsync(SpotifyUri trackUri, uint positionMs, CancellationToken cancellationToken)
    {
        SpotifyId trackId;
        try
        {
            trackId = SpotifyId.From(trackUri);
        }
        catch (LibrespotException)
        {
            Log.LogWarning("<{TrackUri}> could not be converted to a base62 ID", trackUri);
            return null;
        }

        AudioItem audio;
        try
        {
            audio = await AudioItem.GetFileAsync(_session, trackUri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.LogError("Unable to load audio item: {Error}", PlayerLog.Display(e));
            return null;
        }

        var audioItem = await FindAvailableAlternativeAsync(audio, cancellationToken).ConfigureAwait(false);
        if (audioItem is null)
        {
            Log.LogWarning("spotify:track:<{TrackId}> is not available", trackId.ToBase62());
            return null;
        }

        Log.LogInformation("Loading <{Name}> with Spotify URI <{Uri}>", audioItem.Name, audioItem.Uri);

        // (Most) podcasts seem to support only 96 kbps Ogg Vorbis, so fall back to it
        var formats = FormatsFor(_config.Bitrate);

        AudioFileFormat? selectedFormat = null;
        var fileId = default(FileId);
        foreach (var candidate in formats)
        {
            if (audioItem.Files.TryGetValue(candidate, out var id))
            {
                selectedFormat = candidate;
                fileId = id;
                break;
            }
        }

        if (selectedFormat is not { } format)
        {
            Log.LogWarning("<{Name}> is not available in any supported format", audioItem.Name);
            return null;
        }

        if (StreamDataRate(format) is not { } bytesPerSecond)
        {
            return null;
        }

        // This is only a loop to be able to reload the file if an error occurred
        // while opening a cached file.
        while (true)
        {
            AudioFile encryptedFile;
            try
            {
                encryptedFile = await AudioFile.OpenAsync(_session, fileId, bytesPerSecond, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.LogError("Unable to load encrypted file: {Error}", PlayerLog.Display(e));
                return null;
            }

            var isCached = encryptedFile.IsCached;

            StreamLoaderController streamLoaderController;
            try
            {
                streamLoaderController = encryptedFile.GetStreamLoaderController();
            }
            catch (Exception)
            {
                // Upstream: `.ok()?` without logging.
                await encryptedFile.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            // Not all audio files are encrypted. If we can't get a key, try loading the track
            // without decryption. If the file was encrypted after all, the decoder will fail
            // parsing and bail out, so we should be safe from outputting ear-piercing noise.
            AudioKey? key;
            try
            {
                key = await _session.AudioKey.RequestAsync(trackId, fileId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.LogWarning("Unable to load key, continuing without decryption: {Error}", PlayerLog.Display(e));
                key = null;
            }

            var decryptedFile = new AudioDecrypt(key, encryptedFile);

            var isOggVorbis = AudioFiles.IsOggVorbis(format);
            long offset;
            NormalisationData? normalisationData;
            if (isOggVorbis)
            {
                // Spotify stores normalisation data in a custom Ogg packet instead of Vorbis comments.
                try
                {
                    normalisationData = NormalisationData.ParseFromOgg(decryptedFile);
                }
                catch (Exception e) when (e is IOException or NotSupportedException or LibrespotException)
                {
                    normalisationData = null;
                }

                offset = Player.SpotifyOggHeaderEnd;
            }
            else
            {
                offset = 0;
                normalisationData = null;
            }

            Subfile audioFile;
            try
            {
                audioFile = new Subfile(decryptedFile, offset, streamLoaderController.Length);
            }
            catch (Exception e)
            {
                Log.LogError("PlayerTrackLoader::load_track error opening subfile: {Error}", PlayerLog.Display(e));
                await decryptedFile.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            IAudioDecoder? decoder = null;
            Exception? decoderError = null;
            try
            {
                // Passthrough: PassthroughDecoder::new(audio_file, format); otherwise the decoding decoder with the
                // format's MIME type as hint.
                decoder = AudioDecoders.Create(audioFile, format, _config.Passthrough);
            }
            catch (Exception e)
            {
                decoderError = e;
            }

            // For formats other that Vorbis, we'll try getting normalisation data from
            // ReplayGain metadata fields, if present (upstream: inside the `SymphoniaDecoder::new(..).map(..)`
            // closure, so only for a successfully opened, non-passthrough decoder).
            if (decoder is not null && !_config.Passthrough && normalisationData is null)
            {
                normalisationData = decoder.GetNormalisationData();
            }

            if (normalisationData is null)
            {
                Log.LogWarning("Unable to get normalisation data, continuing with defaults.");
                normalisationData = NormalisationData.Default;
            }

            if (decoder is null)
            {
                // Close the file before it is removed from the cache (Windows cannot delete open files).
                await audioFile.DisposeAsync().ConfigureAwait(false);

                var message = decoderError is null ? "unknown error" : PlayerLog.Display(decoderError);
                if (isCached)
                {
                    Log.LogWarning("Unable to read cached audio file: {Error}. Trying to download it.", message);

                    if (_session.Cache is { } cache)
                    {
                        try
                        {
                            cache.RemoveFile(fileId);
                        }
                        catch (Exception)
                        {
                            Log.LogError("Error removing file from cache");
                            return null;
                        }
                    }
                    else
                    {
                        Log.LogError("If the audio file is cached, a cache should exist");
                        return null;
                    }

                    // Just try it again
                    continue;
                }

                Log.LogError("Unable to read audio file: {Error}", message);
                return null;
            }

            var durationMs = audioItem.DurationMs;
            // Don't try to seek past the track's duration.
            // If the position is invalid just start from
            // the beginning of the track.
            if (positionMs > durationMs)
            {
                Log.LogWarning(
                    "Invalid start position of {PositionMs} ms exceeds track's duration of {DurationMs} ms, starting track from the beginning",
                    positionMs,
                    durationMs);
                positionMs = 0;
            }

            // Ensure the starting position. Even when we want to play from the beginning,
            // the cursor may have been moved by parsing normalisation data. This may not
            // matter for playback (but won't hurt either), but may be useful for the
            // passthrough decoder.
            uint streamPositionMs;
            try
            {
                streamPositionMs = decoder.Seek(positionMs);
            }
            catch (Exception e)
            {
                Log.LogError(
                    "PlayerTrackLoader::load_track error seeking to starting position {PositionMs}: {Error}",
                    positionMs,
                    PlayerLog.Display(e));
                decoder.Dispose();
                return null;
            }

            // Ensure streaming mode now that we are ready to play from the requested position.
            streamLoaderController.SetStreamMode();

            var isExplicit = audioItem.IsExplicit;

            Log.LogInformation("<{Name}> ({DurationMs} ms) loaded", audioItem.Name, durationMs);

            return new PlayerLoadedTrackData
            {
                Decoder = decoder,
                NormalisationData = normalisationData,
                StreamLoaderController = streamLoaderController,
                AudioItem = audioItem,
                BytesPerSecond = bytesPerSecond,
                DurationMs = durationMs,
                StreamPositionMs = streamPositionMs,
                IsExplicit = isExplicit,
            };
        }
    }

    /// <summary>Upstream <c>load_local_track</c>.</summary>
    private Task<PlayerLoadedTrackData?> LoadLocalTrackAsync(SpotifyUri trackUri, uint positionMs)
    {
        Log.LogInformation("Loading local file with Spotify URI <{TrackUri}>", trackUri);

        if (trackUri is not SpotifyUri.Local local)
        {
            Log.LogError("Unable to determine track duration for local file: not a local file URI");
            return Task.FromResult<PlayerLoadedTrackData?>(null);
        }

        var duration = local.Duration;

        var path = _localFileLookup.Get(trackUri);
        if (path is null)
        {
            Log.LogError("Unable to find file path for local file <{TrackUri}>", trackUri);
            return Task.FromResult<PlayerLoadedTrackData?>(null);
        }

        FileStream src;
        try
        {
            src = File.OpenRead(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Log.LogError("Failed to open local file: {Error}", PlayerLog.Display(e));
            return Task.FromResult<PlayerLoadedTrackData?>(null);
        }

        // Upstream `path.extension()`: the part after the last dot of the file name, if any.
        var extension = Path.GetExtension(path);
        var hint = new DecoderHint(Extension: extension.Length > 1 ? extension[1..] : null);

        IAudioDecoder decoder;
        try
        {
            decoder = AudioDecoders.Create(src, hint);
        }
        catch (Exception e)
        {
            src.Dispose();
            Log.LogError("Error decoding local file: {Error}", PlayerLog.Display(e));
            return Task.FromResult<PlayerLoadedTrackData?>(null);
        }

        try
        {
            var normalisationData = decoder.GetNormalisationData();
            if (normalisationData is null)
            {
                Log.LogWarning("Unable to get normalisation data, continuing with defaults.");
                normalisationData = NormalisationData.Default;
            }

            var localFileMetadata = decoder.GetLocalFileMetadata() ?? new LocalFileMetadata();

            uint streamPositionMs;
            try
            {
                streamPositionMs = decoder.Seek(positionMs);
            }
            catch (Exception e)
            {
                Log.LogError(
                    "PlayerTrackLoader::load_local_track error seeking to starting position {PositionMs}: {Error}",
                    positionMs,
                    PlayerLog.Display(e));
                decoder.Dispose();
                return Task.FromResult<PlayerLoadedTrackData?>(null);
            }

            long fileSize;
            try
            {
                fileSize = new FileInfo(path).Length;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                decoder.Dispose();
                return Task.FromResult<PlayerLoadedTrackData?>(null);
            }

            // Upstream `file_size / duration.as_secs()`: integer division by whole seconds (panics on 0 seconds;
            // here the DivideByZeroException fails the load the same way).
            var durationSecs = duration.Ticks / TimeSpan.TicksPerSecond;
            var bytesPerSecond = (int)(fileSize / durationSecs);

            var streamLoaderController = StreamLoaderController.FromLocalFile(fileSize);

            var name = localFileMetadata.Name ?? string.Empty;

            Log.LogInformation("Loaded <{Name}> from path <{Path}>", name, path);

            var durationMs = unchecked((uint)(duration.Ticks / TimeSpan.TicksPerMillisecond));

            return Task.FromResult<PlayerLoadedTrackData?>(new PlayerLoadedTrackData
            {
                Decoder = decoder,
                NormalisationData = normalisationData,
                StreamLoaderController = streamLoaderController,
                BytesPerSecond = bytesPerSecond,
                DurationMs = durationMs,
                StreamPositionMs = streamPositionMs,
                IsExplicit = false,
                AudioItem = new AudioItem
                {
                    DurationMs = durationMs,
                    Uri = trackUri.ToUri(),
                    TrackId = trackUri,
                    Files = new AudioFiles(),
                    Name = name,
                    // We can't get a CoverImage.URL for the track image, applications will have to parse the file
                    // metadata themselves using unique_fields.path
                    Covers = [],
                    Language = localFileMetadata.Language is { } language ? [language] : [],
                    IsExplicit = false,
                    Availability = null,
                    Alternatives = null,
                    UniqueFields = new UniqueFields.Local(
                        localFileMetadata.Artists,
                        localFileMetadata.Album,
                        localFileMetadata.AlbumArtists,
                        localFileMetadata.Number,
                        localFileMetadata.DiscNumber,
                        path),
                },
            });
        }
        catch
        {
            decoder.Dispose();
            throw;
        }
    }
}

/// <summary>
/// Upstream <c>PlayerInternal</c>: the playback state machine. <see cref="Run"/> is the body of the player thread.
/// </summary>
internal sealed class PlayerInternal
{
    // Upstream f64::MIN_POSITIVE (the smallest positive *normal* double; double.Epsilon is subnormal).
    private const double F64MinPositive = 2.2250738585072014E-308;

    private static ILogger Log => PlayerLog.Log;

    private Session _session;
    private readonly PlayerConfig _config;
    private readonly ChannelReader<PlayerCommand> _commands;
    private readonly HashSet<Task> _loadHandles = [];

    private PlayerState _state = PlayerState.Stopped.Instance;
    private PlayerPreload _preload = PlayerPreload.None.Instance;
    private Task<bool>? _commandWait;
    private readonly ISink _sink;
    private SinkStatus _sinkStatus = SinkStatus.Closed;
    private SinkEventCallback? _sinkEventCallback;
    private readonly IVolumeGetter _volumeGetter;
    private readonly List<ChannelWriter<PlayerEvent>> _eventSenders = [];
    private readonly Converter _converter;

    private readonly double[] _normalisationIntegrators = new double[2];
    private readonly double[] _normalisationPeaks = new double[2];
    private int _normalisationChannel;
    private readonly double _normalisationKneeFactor;

    private bool _autoNormaliseAsAlbum;

    private readonly int _playerId;
    private SeqGenerator<ulong> _playRequestIdGenerator = new(0);
    private long _lastProgressUpdate;

    private readonly LocalFileLookup _localFileLookup;
    private readonly TrackLoaderFactory _trackLoaderFactory;
    private readonly TimeProvider _time;

    public PlayerInternal(
        Session session,
        PlayerConfig config,
        ChannelReader<PlayerCommand> commands,
        ISink sink,
        IVolumeGetter volumeGetter,
        Converter converter,
        double normalisationKneeFactor,
        int playerId,
        LocalFileLookup localFileLookup,
        TrackLoaderFactory trackLoaderFactory,
        TimeProvider timeProvider)
    {
        _session = session;
        _config = config;
        _commands = commands;
        _sink = sink;
        _volumeGetter = volumeGetter;
        _converter = converter;
        _normalisationKneeFactor = normalisationKneeFactor;
        _playerId = playerId;
        _localFileLookup = localFileLookup;
        _trackLoaderFactory = trackLoaderFactory;
        _time = timeProvider;
        _lastProgressUpdate = timeProvider.GetTimestamp();
    }

    /// <summary>
    /// Upstream <c>impl Future for PlayerInternal</c>, run with <c>block_on</c> on the player thread. Returns when
    /// the command channel is closed and drained, then releases everything (upstream <c>Drop</c>).
    /// </summary>
    public void Run()
    {
        try
        {
            RunLoop();
        }
        finally
        {
            Drop();
        }
    }

    private void RunLoop()
    {
        var passthrough = _config.Passthrough;

        while (true)
        {
            var allFuturesCompletedOrNotReady = true;

            // process commands that were sent to us
            if (_commands.TryRead(out var cmd))
            {
                allFuturesCompletedOrNotReady = false;
                _commandWait = null;

                try
                {
                    HandleCommand(cmd);
                }
                catch (Exception e)
                {
                    Log.LogError("Error handling command: {Error}", PlayerLog.Display(e));
                }
            }
            else if (_commands.Completion.IsCompleted)
            {
                return; // client has disconnected - shut down.
            }

            // Handle loading of a new track to play
            if (_state is PlayerState.Loading loading)
            {
                // The loader may be terminated if we are trying to load the same track
                // as before, and that track failed to open before.
                var trackId = loading.TrackId;

                if (loading.Loader.TryPoll(out var loadedTrack))
                {
                    if (loadedTrack is not null)
                    {
                        StartPlayback(trackId, loading.PlayRequestId, loadedTrack, loading.StartPlayback);
                        if (_state is PlayerState.Loading)
                        {
                            PlayerLog.Fatal("The state wasn't changed by start_playback()");
                        }
                    }
                    else
                    {
                        // Upstream logs the `()` error value of the failed loader.
                        Log.LogError("Skipping to next track, unable to load track <{TrackId}>: ()", trackId);
                        SendEvent(new PlayerEvent.Unavailable(loading.PlayRequestId, trackId));
                    }
                }
            }

            // handle pending preload requests.
            if (_preload is PlayerPreload.Loading preloading && preloading.Loader.TryPoll(out var preloadedTrack))
            {
                var trackId = preloading.TrackId;
                if (preloadedTrack is not null)
                {
                    SendEvent(new PlayerEvent.Preloading(trackId));
                    _preload = new PlayerPreload.Ready(trackId, preloadedTrack);
                }
                else
                {
                    Log.LogDebug("Unable to preload {TrackId}", trackId);
                    _preload = PlayerPreload.None.Instance;
                    // Let Spirc know that the track was unavailable.
                    if (_state is PlayerState.Active active)
                    {
                        SendEvent(new PlayerEvent.Unavailable(active.PlayRequestId, trackId));
                    }
                }
            }

            if (_state.IsPlaying())
            {
                EnsureSinkRunning();

                if (_state is PlayerState.Playing playing)
                {
                    PlayNextPacket(playing, passthrough);
                }
                else
                {
                    PlayerLog.Fatal("PlayerInternal poll: Invalid PlayerState");
                }
            }

            if (_state is PlayerState.Active current)
            {
                if (!current.SuggestedToPreloadNextTrack
                    && ((long)current.DurationMs - current.StreamPositionMs) < Player.PreloadNextTrackBeforeEndDurationMs
                    && current.StreamLoaderController.RangeToEndAvailable())
                {
                    current.SuggestedToPreloadNextTrack = true;
                    SendEvent(new PlayerEvent.TimeToPreloadNextTrack(current.PlayRequestId, current.TrackId));
                }
            }

            if (!_state.IsPlaying() && allFuturesCompletedOrNotReady)
            {
                // Upstream returns Poll::Pending and is woken by a command or a loader. This thread does nothing
                // else, so it blocks until one of them is ready.
                WaitForWork();
            }
        }
    }

    /// <summary>Blocks the player thread until a command arrives or a pending loader completes.</summary>
    private void WaitForWork()
    {
        // Keep one pending command wait across idle iterations instead of registering a new one each time.
        _commandWait = _commandWait is { IsCompleted: false } ? _commandWait : _commands.WaitToReadAsync().AsTask();
        var waits = new List<Task>(3) { _commandWait };

        if (_state is PlayerState.Loading { Loader.IsTerminated: false } loading)
        {
            waits.Add(loading.Loader.Task);
        }

        if (_preload is PlayerPreload.Loading preloading)
        {
            waits.Add(preloading.Loader.Task);
        }

        Task.WaitAny([.. waits]);
    }

    /// <summary>The <c>Playing</c> branch of upstream's poll loop: decode, report position, hand the packet on.</summary>
    private void PlayNextPacket(PlayerState.Playing playing, bool passthrough)
    {
        var trackId = playing.TrackId;
        var playRequestId = playing.PlayRequestId;

        (AudioPacketPosition Position, AudioPacket Packet)? result;
        try
        {
            result = playing.Decoder.NextPacket();
        }
        catch (Exception e)
        {
            Log.LogError(
                "Skipping to next track, unable to get next packet for track <{TrackId}>: {Error}",
                trackId,
                PlayerLog.Display(e));
            SendEvent(new PlayerEvent.EndOfTrack(playRequestId, trackId));
            return;
        }

        if (result is var (packetPosition, packet))
        {
            var newStreamPositionMs = packetPosition.PositionMs;
            var expectedPositionMs = playing.StreamPositionMs;
            playing.StreamPositionMs = newStreamPositionMs;

            if (!passthrough)
            {
                if (packet is AudioPacket.Samples)
                {
                    var newStreamPosition = TimeSpan.FromMilliseconds(newStreamPositionMs);

                    var now = _time.GetTimestamp();

                    // Only notify if we're skipped some packets *or* we are behind.
                    // If we're ahead it's probably due to a buffer of the backend
                    // and we're actually in time.
                    bool notifyAboutPosition;
                    if (playing.ReportedNominalStartTime is not { } reportedNominalStartTime)
                    {
                        notifyAboutPosition = true;
                    }
                    else
                    {
                        var notify = false;

                        if (packetPosition.Skipped)
                        {
                            var expected = TimeSpan.FromMilliseconds(expectedPositionMs);
                            if (newStreamPosition >= expected)
                            {
                                var ahead = newStreamPosition - expected;
                                notify |= ahead >= TimeSpan.FromSeconds(1);
                            }
                        }

                        if (now >= reportedNominalStartTime)
                        {
                            var lag = _time.GetElapsedTime(reportedNominalStartTime, now);
                            if (lag >= newStreamPosition)
                            {
                                notify |= lag - newStreamPosition >= TimeSpan.FromSeconds(1);
                            }
                        }

                        notifyAboutPosition = notify;
                    }

                    if (notifyAboutPosition)
                    {
                        playing.ReportedNominalStartTime = CheckedSub(now, newStreamPosition);
                        SendEvent(new PlayerEvent.PositionCorrection(playRequestId, trackId, newStreamPositionMs));
                    }

                    if (_config.PositionUpdateInterval is { } interval)
                    {
                        // Instant::duration_since saturates at zero.
                        var lastProgressUpdateSinceMs = now >= _lastProgressUpdate
                            ? _time.GetElapsedTime(_lastProgressUpdate, now)
                            : TimeSpan.Zero;

                        if (lastProgressUpdateSinceMs > interval)
                        {
                            _lastProgressUpdate = now;
                            SendEvent(new PlayerEvent.PositionChanged(playRequestId, trackId, newStreamPositionMs));
                        }
                    }
                }
                else
                {
                    // Upstream `packet.samples()` returned AudioPacketError::Raw.
                    Log.LogError(
                        "Skipping to next track, unable to decode samples for track <{TrackId}>: {Error}",
                        trackId,
                        "Decoder Raw Error: Can't return Raw on Samples");
                    SendEvent(new PlayerEvent.EndOfTrack(playRequestId, trackId));
                }
            }
        }

        HandlePacket(result, playing.NormalisationFactor);
    }

    /// <summary>Upstream <c>Instant::checked_sub</c> on a <see cref="TimeProvider"/> timestamp.</summary>
    private long? CheckedSub(long timestamp, TimeSpan duration)
    {
        var ticks = (long)(duration.TotalSeconds * _time.TimestampFrequency);
        return timestamp >= ticks ? timestamp - ticks : null;
    }

    private void EnsureSinkRunning()
    {
        if (_sinkStatus != SinkStatus.Running)
        {
            Log.LogTrace("== Starting sink ==");
            _sinkEventCallback?.Invoke(SinkStatus.Running);

            try
            {
                _sink.Start();
                _sinkStatus = SinkStatus.Running;
            }
            catch (Exception e)
            {
                Log.LogError("{Error}", PlayerLog.Display(e));
                HandlePause();
            }
        }
    }

    private void EnsureSinkStopped(bool temporarily)
    {
        switch (_sinkStatus)
        {
            case SinkStatus.Running:
                Log.LogTrace("== Stopping sink ==");
                try
                {
                    _sink.Stop();
                }
                catch (Exception e)
                {
                    PlayerLog.Fatal(PlayerLog.Display(e));
                }

                _sinkStatus = temporarily ? SinkStatus.TemporarilyClosed : SinkStatus.Closed;
                _sinkEventCallback?.Invoke(_sinkStatus);
                break;

            case SinkStatus.TemporarilyClosed:
                if (!temporarily)
                {
                    _sinkStatus = SinkStatus.Closed;
                    _sinkEventCallback?.Invoke(SinkStatus.Closed);
                }

                break;

            case SinkStatus.Closed:
                break;
        }
    }

    private static bool TryGetTrack(PlayerState state, [NotNullWhen(true)] out SpotifyUri? trackId, out ulong playRequestId)
    {
        switch (state)
        {
            case PlayerState.Active active:
                trackId = active.TrackId;
                playRequestId = active.PlayRequestId;
                return true;
            case PlayerState.EndOfTrack endOfTrack:
                trackId = endOfTrack.TrackId;
                playRequestId = endOfTrack.PlayRequestId;
                return true;
            case PlayerState.Loading loading:
                trackId = loading.TrackId;
                playRequestId = loading.PlayRequestId;
                return true;
            default:
                trackId = null;
                playRequestId = 0;
                return false;
        }
    }

    private void HandlePlayerStop()
    {
        if (TryGetTrack(_state, out var trackId, out var playRequestId))
        {
            EnsureSinkStopped(false);
            SendEvent(new PlayerEvent.Stopped(playRequestId, trackId));
            SetState(PlayerState.Stopped.Instance);
        }
        else if (_state is PlayerState.Invalid)
        {
            PlayerLog.Fatal("PlayerInternal::handle_player_stop in invalid state");
        }
    }

    private void HandlePlay()
    {
        switch (_state)
        {
            case PlayerState.Paused paused:
            {
                var trackId = paused.TrackId;
                var playRequestId = paused.PlayRequestId;
                var streamPositionMs = paused.StreamPositionMs;

                PausedToPlaying();
                SendEvent(new PlayerEvent.Playing(playRequestId, trackId, streamPositionMs));
                EnsureSinkRunning();
                break;
            }

            case PlayerState.Loading loading:
                loading.StartPlayback = true;
                break;

            default:
                Log.LogError("Player::play called from invalid state: {State}", _state);
                break;
        }
    }

    private void HandlePause()
    {
        switch (_state)
        {
            case PlayerState.Paused:
                EnsureSinkStopped(false);
                break;

            case PlayerState.Playing playing:
            {
                var trackId = playing.TrackId;
                var playRequestId = playing.PlayRequestId;
                var streamPositionMs = playing.StreamPositionMs;

                PlayingToPaused();

                EnsureSinkStopped(false);
                SendEvent(new PlayerEvent.Paused(playRequestId, trackId, streamPositionMs));
                break;
            }

            case PlayerState.Loading loading:
                loading.StartPlayback = false;
                break;

            default:
                Log.LogError("Player::pause called from invalid state: {State}", _state);
                break;
        }
    }

    private void HandlePacket((AudioPacketPosition Position, AudioPacket Packet)? packet, double normalisationFactor)
    {
        if (packet is var (_, audioPacket))
        {
            if (!audioPacket.IsEmpty)
            {
                if (audioPacket is AudioPacket.Samples samples)
                {
                    ApplyVolumeAndNormalisation(samples.Data, normalisationFactor);
                }

                try
                {
                    _sink.Write(audioPacket, _converter);
                }
                catch (Exception e)
                {
                    Log.LogError("{Error}", PlayerLog.Display(e));
                    HandlePause();
                }
            }
        }
        else
        {
            PlayingToEndOfTrack();
            if (_state is PlayerState.EndOfTrack endOfTrack)
            {
                SendEvent(new PlayerEvent.EndOfTrack(endOfTrack.PlayRequestId, endOfTrack.TrackId));
            }
            else
            {
                PlayerLog.Fatal("PlayerInternal handle_packet: Invalid PlayerState");
            }
        }
    }

    /// <summary>The sample processing of upstream <c>handle_packet</c>: normalisation, limiter and volume, in place.</summary>
    internal void ApplyVolumeAndNormalisation(Span<double> data, double normalisationFactor)
    {
        // Get the volume for the packet. In the case of hardware volume control
        // this will always be 1.0 (no change).
        var volume = _volumeGetter.AttenuationFactor();

        // For the basic normalisation method, a normalisation factor of 1.0
        // indicates that there is nothing to normalise (all samples should pass
        // unaltered). For the dynamic method, there may still be peaks that we
        // want to shave off.
        //
        // No matter the case we apply volume attenuation last if there is any.
        if (!_config.Normalisation)
        {
            if (volume < 1.0)
            {
                for (var i = 0; i < data.Length; i++)
                {
                    data[i] *= volume;
                }
            }
        }
        else if (_config.NormalisationMethod == NormalisationMethod.Dynamic)
        {
            // zero-cost shorthands
            var thresholdDb = _config.NormalisationThresholdDbfs;
            var kneeDb = _config.NormalisationKneeDb;
            var attackCf = _config.NormalisationAttackCf;
            var releaseCf = _config.NormalisationReleaseCf;

            for (var i = 0; i < data.Length; i++)
            {
                ref var sample = ref data[i];

                // Feedforward limiter in the log domain
                // After: Giannoulis, D., Massberg, M., & Reiss, J.D. (2012).
                // Digital Dynamic Range Compressor Design—A Tutorial and
                // Analysis. Journal of The Audio Engineering Society, 60,
                // 399-408.

                // This implementation assumes audio is stereo.

                // step 0: apply gain stage
                sample *= normalisationFactor;

                // step 1-4: half-wave rectification and conversion into dB, and
                // gain computer with soft knee and subtractor

                // Add slight DC offset. Some samples are silence, which is
                // -inf dB and gets the limiter stuck. Adding a small
                // positive offset prevents this.
                sample += F64MinPositive;

                var biasDb = Player.RatioToDb(Math.Abs(sample)) - thresholdDb;
                var kneeBoundaryDb = biasDb * 2.0;
                double limiterDb;
                if (kneeBoundaryDb < -kneeDb)
                {
                    limiterDb = 0.0;
                }
                else if (Math.Abs(kneeBoundaryDb) <= kneeDb)
                {
                    var term = kneeBoundaryDb + kneeDb;
                    limiterDb = term * term * _normalisationKneeFactor;
                }
                else
                {
                    limiterDb = biasDb;
                }

                // track left/right channel
                var channel = _normalisationChannel;
                _normalisationChannel ^= 1;

                // step 5: smooth, decoupled peak detector for each channel
                // Use direct references to reduce repeated array indexing
                ref var integrator = ref _normalisationIntegrators[channel];
                ref var peak = ref _normalisationPeaks[channel];

                // (double.MaxNumber has f64::max's NaN semantics.)
                integrator = double.MaxNumber(limiterDb, (releaseCf * integrator) + ((1.0 - releaseCf) * limiterDb));
                peak = (attackCf * peak) + ((1.0 - attackCf) * integrator);

                // steps 6-8: conversion into level and multiplication into gain
                // stage. Find maximum peak across both channels to couple the
                // gain and maintain stereo imaging.
                var maxPeak = double.MaxNumber(_normalisationPeaks[0], _normalisationPeaks[1]);
                sample *= Player.DbToRatio(-maxPeak) * volume;
            }
        }
        else
        {
            if (normalisationFactor < 1.0 || volume < 1.0)
            {
                for (var i = 0; i < data.Length; i++)
                {
                    data[i] *= normalisationFactor * volume;
                }
            }
        }
    }

    /// <summary>Upstream <c>playing_to_end_of_track</c>.</summary>
    private void PlayingToEndOfTrack()
    {
        var newState = TakeState();
        if (newState is PlayerState.Playing playing)
        {
            _state = new PlayerState.EndOfTrack(playing.TrackId, playing.PlayRequestId, playing.ToLoadedTrack());
        }
        else
        {
            PlayerLog.Fatal($"Called playing_to_end_of_track in non-playing state: {newState}");
        }
    }

    /// <summary>Upstream <c>paused_to_playing</c>.</summary>
    private void PausedToPlaying()
    {
        var newState = TakeState();
        if (newState is PlayerState.Paused paused)
        {
            _state = new PlayerState.Playing(
                paused,
                CheckedSub(_time.GetTimestamp(), TimeSpan.FromMilliseconds(paused.StreamPositionMs)));
        }
        else
        {
            PlayerLog.Fatal($"PlayerState::paused_to_playing in invalid state: {newState}");
        }
    }

    /// <summary>Upstream <c>playing_to_paused</c>.</summary>
    private void PlayingToPaused()
    {
        var newState = TakeState();
        if (newState is PlayerState.Playing playing)
        {
            _state = new PlayerState.Paused(playing);
        }
        else
        {
            PlayerLog.Fatal($"PlayerState::playing_to_paused in invalid state: {newState}");
        }
    }

    /// <summary>Upstream <c>mem::replace(&amp;mut self.state, Invalid)</c>: moves the state out without releasing it.</summary>
    private PlayerState TakeState()
    {
        var old = _state;
        _state = PlayerState.Invalid.Instance;
        return old;
    }

    /// <summary>Upstream <c>self.state = next</c>: the old state is dropped (decoder disposed, loader abandoned).</summary>
    private void SetState(PlayerState next)
    {
        var old = _state;
        _state = next;
        switch (old)
        {
            case PlayerState.Loading loading:
                loading.Loader.Abandon();
                break;
            case PlayerState.Active active:
                active.Decoder.Dispose();
                break;
            case PlayerState.EndOfTrack endOfTrack:
                endOfTrack.LoadedTrack.Dispose();
                break;
        }
    }

    /// <summary>Upstream <c>self.preload = next</c>: the old preload is dropped.</summary>
    private void SetPreload(PlayerPreload next)
    {
        var old = _preload;
        _preload = next;
        switch (old)
        {
            case PlayerPreload.Loading loading:
                loading.Loader.Abandon();
                break;
            case PlayerPreload.Ready ready:
                ready.LoadedTrack.Dispose();
                break;
        }
    }

    private void StartPlayback(SpotifyUri trackId, ulong playRequestId, PlayerLoadedTrackData loadedTrack, bool startPlayback)
    {
        var audioItem = loadedTrack.AudioItem;

        SendEvent(new PlayerEvent.TrackChanged(audioItem));

        var positionMs = loadedTrack.StreamPositionMs;

        var config = _config;
        if (config.NormalisationType == NormalisationType.Auto)
        {
            config = config with
            {
                NormalisationType = _autoNormaliseAsAlbum ? NormalisationType.Album : NormalisationType.Track,
            };
        }

        var normalisationFactor = NormalisationData.GetFactor(config, loadedTrack.NormalisationData);

        if (startPlayback)
        {
            EnsureSinkRunning();
            SendEvent(new PlayerEvent.Playing(playRequestId, trackId, positionMs));

            SetState(new PlayerState.Playing(
                trackId,
                playRequestId,
                loadedTrack.Decoder,
                loadedTrack.AudioItem,
                loadedTrack.NormalisationData,
                normalisationFactor,
                loadedTrack.StreamLoaderController,
                loadedTrack.BytesPerSecond,
                loadedTrack.DurationMs,
                loadedTrack.StreamPositionMs,
                CheckedSub(_time.GetTimestamp(), TimeSpan.FromMilliseconds(positionMs)),
                suggestedToPreloadNextTrack: false,
                loadedTrack.IsExplicit));
        }
        else
        {
            EnsureSinkStopped(false);

            SetState(new PlayerState.Paused(
                trackId,
                playRequestId,
                loadedTrack.Decoder,
                loadedTrack.AudioItem,
                loadedTrack.NormalisationData,
                normalisationFactor,
                loadedTrack.StreamLoaderController,
                loadedTrack.BytesPerSecond,
                loadedTrack.DurationMs,
                loadedTrack.StreamPositionMs,
                suggestedToPreloadNextTrack: false,
                loadedTrack.IsExplicit));

            SendEvent(new PlayerEvent.Paused(playRequestId, trackId, positionMs));
        }
    }

    private void HandleCommandLoad(SpotifyUri trackId, ulong? playRequestIdOption, bool play, uint positionMs)
    {
        // Upstream `unwrap_or(self.play_request_id_generator.get())` evaluates its argument eagerly, so the
        // generator advances even when an id is passed in.
        var generatedPlayRequestId = _playRequestIdGenerator.Get();
        var playRequestId = playRequestIdOption ?? generatedPlayRequestId;

        SendEvent(new PlayerEvent.PlayRequestIdChanged(playRequestId));

        if (!_config.Gapless)
        {
            EnsureSinkStopped(play);
        }

        if (_state is PlayerState.Invalid)
        {
            throw LibrespotException.Internal($"Player::handle_command_load called from invalid state: {_state}");
        }

        // Now we check at different positions whether we already have a pre-loaded version
        // of this track somewhere. If so, use it and return.

        // Check if there's a matching loaded track in the EndOfTrack player state.
        // This is the case if we're repeating the same track again.
        if (_state is PlayerState.EndOfTrack previous && previous.TrackId == trackId)
        {
            var loadedTrack = ((PlayerState.EndOfTrack)TakeState()).LoadedTrack;

            if (positionMs != loadedTrack.StreamPositionMs)
            {
                // This may be blocking. As upstream, an error here leaves the state Invalid.
                try
                {
                    loadedTrack.StreamPositionMs = loadedTrack.Decoder.Seek(positionMs);
                }
                catch
                {
                    // Upstream: the track moved out of the state is dropped when `?` returns.
                    loadedTrack.Dispose();
                    throw;
                }
            }

            SetPreload(PlayerPreload.None.Instance);
            StartPlayback(trackId, playRequestId, loadedTrack, play);
            if (_state is PlayerState.Invalid)
            {
                throw LibrespotException.Internal(
                    $"PlayerInternal::handle_command_load repeating the same track: start_playback() did not transition to valid player state: {_state}");
            }

            return;
        }

        // Check if we are already playing the track. If so, just do a seek and update our info.
        if (_state is PlayerState.Active active && active.TrackId == trackId)
        {
            // we can use the current decoder. Ensure it's at the correct position.
            if (positionMs != active.StreamPositionMs)
            {
                // This may be blocking.
                active.StreamPositionMs = active.Decoder.Seek(positionMs);
            }

            // Move the info from the current state into a PlayerLoadedTrackData so we can use
            // the usual code path to start playback.
            var oldState = (PlayerState.Active)TakeState();
            var loadedTrack = oldState.ToLoadedTrack();

            SetPreload(PlayerPreload.None.Instance);
            StartPlayback(trackId, playRequestId, loadedTrack, play);

            if (_state is PlayerState.Invalid)
            {
                throw LibrespotException.Internal(
                    $"PlayerInternal::handle_command_load already playing this track: start_playback() did not transition to valid player state: {_state}");
            }

            return;
        }

        // Check if the requested track has been preloaded already. If so use the preloaded data.
        if (_preload is PlayerPreload.Ready ready && ready.TrackId == trackId)
        {
            _preload = PlayerPreload.None.Instance;
            var loadedTrack = ready.LoadedTrack;

            if (positionMs != loadedTrack.StreamPositionMs)
            {
                try
                {
                    // This may be blocking
                    loadedTrack.StreamPositionMs = loadedTrack.Decoder.Seek(positionMs);
                }
                catch
                {
                    // Upstream: the taken preload is dropped when `?` returns.
                    loadedTrack.Dispose();
                    throw;
                }
            }

            StartPlayback(ready.TrackId, playRequestId, loadedTrack, play);
            return;
        }

        SendEvent(new PlayerEvent.Loading(playRequestId, trackId, positionMs));

        // Try to extract a pending loader from the preloading mechanism
        TrackLoader? loader = null;
        if (_preload is PlayerPreload.Loading preloading && preloading.TrackId == trackId && positionMs == 0)
        {
            _preload = PlayerPreload.None.Instance;
            loader = preloading.Loader;
        }

        SetPreload(PlayerPreload.None.Instance);

        // If we don't have a loader yet, create one from scratch.
        loader ??= LoadTrack(trackId, positionMs);

        // Set ourselves to a loading state.
        SetState(new PlayerState.Loading(trackId, playRequestId, play, loader));
    }

    private void HandleCommandPreload(SpotifyUri trackId)
    {
        Log.LogDebug("Preloading track");
        var preloadTrack = true;

        // check whether the track is already loaded somewhere or being loaded.
        var currentlyLoading = _preload switch
        {
            PlayerPreload.Loading loading => loading.TrackId,
            PlayerPreload.Ready ready => ready.TrackId,
            _ => null,
        };

        if (currentlyLoading is not null)
        {
            if (currentlyLoading == trackId)
            {
                // we're already preloading the requested track.
                preloadTrack = false;
            }
            else
            {
                // we're preloading something else - cancel it.
                SetPreload(PlayerPreload.None.Instance);
            }
        }

        var currentTrackId = _state switch
        {
            PlayerState.Active active => active.TrackId,
            PlayerState.EndOfTrack endOfTrack => endOfTrack.TrackId,
            _ => null,
        };

        if (currentTrackId is not null && currentTrackId == trackId)
        {
            // we already have the requested track loaded.
            preloadTrack = false;
        }

        // schedule the preload of the current track if desired.
        if (preloadTrack)
        {
            var loader = LoadTrack(trackId, 0);
            SetPreload(new PlayerPreload.Loading(trackId, loader));
        }
    }

    private void HandleCommandSeek(uint positionMs)
    {
        // When we are still loading, the user may immediately ask to
        // seek to another position yet the decoder won't be ready for
        // that. In this case just restart the loading process but
        // with the requested position.
        if (_state is PlayerState.Loading loading)
        {
            HandleCommandLoad(loading.TrackId, loading.PlayRequestId, loading.StartPlayback, positionMs);
            return;
        }

        if (_state.GetDecoder() is { } decoder)
        {
            try
            {
                var newPositionMs = decoder.Seek(positionMs);
                if (_state is PlayerState.Active active)
                {
                    active.StreamPositionMs = newPositionMs;

                    SendEvent(new PlayerEvent.Seeked(active.PlayRequestId, active.TrackId, newPositionMs));
                }
            }
            catch (Exception e)
            {
                Log.LogError("PlayerInternal::handle_command_seek error: {Error}", PlayerLog.Display(e));
            }
        }
        else
        {
            Log.LogError("Player::seek called from invalid state: {State}", _state);
        }

        // ensure we have a bit of a buffer of downloaded data
        PreloadDataBeforePlayback();

        if (_state is PlayerState.Playing playing)
        {
            playing.ReportedNominalStartTime = CheckedSub(_time.GetTimestamp(), TimeSpan.FromMilliseconds(positionMs));
        }
    }

    private void HandleCommand(PlayerCommand cmd)
    {
        Log.LogDebug("command={Command}", PlayerCommand.Describe(cmd));
        switch (cmd)
        {
            case PlayerCommand.Load load:
                HandleCommandLoad(load.TrackId, null, load.StartPlaying, load.PositionMs);
                break;

            case PlayerCommand.Preload preload:
                HandleCommandPreload(preload.TrackId);
                break;

            case PlayerCommand.Seek seek:
                HandleCommandSeek(seek.PositionMs);
                break;

            case PlayerCommand.Play:
                HandlePlay();
                break;

            case PlayerCommand.Pause:
                HandlePause();
                break;

            case PlayerCommand.Stop:
                HandlePlayerStop();
                break;

            case PlayerCommand.SetSession setSession:
                _session = setSession.Session;
                break;

            case PlayerCommand.AddEventSender addEventSender:
                _eventSenders.Add(addEventSender.Sender);
                break;

            case PlayerCommand.SetSinkEventCallback setSinkEventCallback:
                _sinkEventCallback = setSinkEventCallback.Callback;
                break;

            case PlayerCommand.EmitVolumeChangedEvent e:
                SendEvent(new PlayerEvent.VolumeChanged(e.Volume));
                break;

            case PlayerCommand.EmitRepeatChangedEvent e:
                SendEvent(new PlayerEvent.RepeatChanged(e.Context, e.Track));
                break;

            case PlayerCommand.EmitShuffleChangedEvent e:
                SendEvent(new PlayerEvent.ShuffleChanged(e.Shuffle));
                break;

            case PlayerCommand.EmitAutoPlayChangedEvent e:
                SendEvent(new PlayerEvent.AutoPlayChanged(e.AutoPlay));
                break;

            case PlayerCommand.EmitSessionClientChangedEvent e:
                SendEvent(new PlayerEvent.SessionClientChanged(e.ClientId, e.ClientName, e.ClientBrandName, e.ClientModelName));
                break;

            case PlayerCommand.EmitSessionConnectedEvent e:
                SendEvent(new PlayerEvent.SessionConnected(e.ConnectionId, e.UserName));
                break;

            case PlayerCommand.EmitSessionDisconnectedEvent e:
                SendEvent(new PlayerEvent.SessionDisconnected(e.ConnectionId, e.UserName));
                break;

            case PlayerCommand.SetAutoNormaliseAsAlbum e:
                _autoNormaliseAsAlbum = e.Setting;
                break;

            case PlayerCommand.EmitSetQueueEvent e:
                SendEvent(new PlayerEvent.SetQueue(e.ContextUri, e.CurrentTrack, e.NextTracks, e.PrevTracks));
                break;

            case PlayerCommand.EmitFilterExplicitContentChangedEvent e:
                SendEvent(new PlayerEvent.FilterExplicitContentChanged(e.Filter));

                if (e.Filter && _state is PlayerState.Active active && active.IsExplicit)
                {
                    Log.LogWarning("Currently loaded track is explicit, which client setting forbids -- skipping to next track.");
                    SendEvent(new PlayerEvent.EndOfTrack(active.PlayRequestId, active.TrackId));
                }

                break;
        }
    }

    private void SendEvent(PlayerEvent playerEvent) =>
        _eventSenders.RemoveAll(sender => !sender.TryWrite(playerEvent));

    /// <summary>
    /// Upstream <c>load_track</c>: starts loading in the background and returns the pending loader. Upstream spawns a
    /// thread per load (decoder seeks block); here the load runs on the thread pool and its task is tracked like
    /// upstream's <c>load_handles</c>.
    /// </summary>
    private TrackLoader LoadTrack(SpotifyUri spotifyUri, uint positionMs)
    {
        var loader = _trackLoaderFactory(_session, _config, _localFileLookup);

        var task = Task.Run(async () =>
        {
            try
            {
                return await loader.LoadTrackAsync(spotifyUri, positionMs).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // Upstream: a panic in the loader thread drops the result sender, which the player sees as Err(()).
                Log.LogError(e, "Track loader for <{TrackUri}> failed: {Error}", spotifyUri, PlayerLog.Display(e));
                return null;
            }
        });

        lock (_loadHandles)
        {
            _loadHandles.Add(task);
        }

        _ = task.ContinueWith(
            t =>
            {
                lock (_loadHandles)
                {
                    _loadHandles.Remove(t);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return new TrackLoader(task);
    }

    private void PreloadDataBeforePlayback()
    {
        if (_state is PlayerState.Playing playing)
        {
            var readAheadDuringPlayback = AudioFetchParams.Get().ReadAheadDuringPlayback;
            // Request our read ahead range
            var requestDataLength = (long)((float)readAheadDuringPlayback.TotalSeconds * playing.BytesPerSecond);

            // Request the part we want to wait for blocking. This effectively means we wait for the previous request to partially complete.
            var waitForDataLength = (long)((float)readAheadDuringPlayback.TotalSeconds * playing.BytesPerSecond);

            playing.StreamLoaderController.FetchNextAndWait(requestDataLength, waitForDataLength);
        }
    }

    /// <summary>Upstream <c>impl Drop for PlayerInternal</c> plus the drop of its fields.</summary>
    private void Drop()
    {
        Log.LogDebug("drop PlayerInternal[{PlayerId}]", _playerId);

        Task[] handles;
        lock (_loadHandles)
        {
            // waiting for the tasks while holding the lock would result in a deadlock
            handles = [.. _loadHandles];
            _loadHandles.Clear();
        }

        try
        {
            Task.WaitAll(handles);
        }
        catch (AggregateException)
        {
            // Upstream ignores join errors (`let _ = handle.join()`).
        }

        // Field drops: the state and preload (decoders, pending loads), the event senders and the sink.
        SetState(PlayerState.Stopped.Instance);
        SetPreload(PlayerPreload.None.Instance);

        foreach (var sender in _eventSenders)
        {
            sender.TryComplete();
        }

        _eventSenders.Clear();
        _sink.Dispose();
    }
}

/// <summary>
/// Upstream <c>Subfile</c>: a view of a stream that starts at <c>offset</c> (used to skip Spotify's custom Ogg
/// header). Like upstream, reads are not bounded and <see cref="Length"/> is the full length passed in
/// (symphonia's <c>byte_len</c>).
/// </summary>
internal sealed class Subfile : Stream
{
    private readonly Stream _stream;
    private readonly long _offset;
    private readonly long _length;

    /// <summary>Wraps <paramref name="stream"/> and seeks it to <paramref name="offset"/>.</summary>
    /// <exception cref="IOException">Seeking failed.</exception>
    public Subfile(Stream stream, long offset, long length)
    {
        stream.Seek(offset, SeekOrigin.Begin);

        _stream = stream;
        _offset = offset;
        _length = length;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    /// <summary>Upstream <c>MediaSource::byte_len</c>.</summary>
    public override long Length => _length;

    public override long Position
    {
        get => _stream.Position - _offset;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) => _stream.Read(buffer);

    public override long Seek(long offset, SeekOrigin origin)
    {
        long newpos;
        switch (origin)
        {
            case SeekOrigin.Begin:
                newpos = _stream.Seek(offset + _offset, SeekOrigin.Begin);
                break;
            case SeekOrigin.End:
                if (_length - offset < _offset)
                {
                    throw new IOException("newpos would be < self.offset");
                }

                newpos = _stream.Seek(offset, SeekOrigin.End);
                break;
            default:
                newpos = _stream.Seek(offset, origin);
                break;
        }

        return newpos - _offset;
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stream.Dispose();
        }

        base.Dispose(disposing);
    }
}
