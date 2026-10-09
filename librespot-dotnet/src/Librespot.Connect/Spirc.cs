// Port of connect/src/spirc.rs.
//
// Spirc is the handle (every method sends a SpircCommand to the task), SpircTask is the connect event loop.
// Upstream's `tokio::select!` over the dealer streams, the request handler, the command channel, the player events,
// the two debounce timers and the context resolver becomes one loop that awaits Task.WhenAny over the pending reads
// of every enabled branch (see SpircTask.RunAsync and SpircTask.RunIterationAsync).
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Librespot.Core;
using Librespot.Core.Dealer;
using Librespot.Core.Logging;
using Librespot.Playback;
using Librespot.Playback.Mixer;
using Microsoft.Extensions.Logging;
using Cluster = Spotify.Connectstate.Cluster;
using ClusterUpdate = Spotify.Connectstate.ClusterUpdate;
using Context = Spotify.Player.Proto.Context;
using ContextPage = Spotify.Player.Proto.ContextPage;
using LogoutCommand = Spotify.Connectstate.LogoutCommand;
using PlaylistModificationInfo = Spotify.Playlist4.Proto.PlaylistModificationInfo;
using ProvidedTrack = Spotify.Connectstate.ProvidedTrack;
using SessionUpdate = Socialconnect.SessionUpdate;
using SetVolumeCommand = Spotify.Connectstate.SetVolumeCommand;
using TransferState = Spotify.Player.Proto.Transfer.TransferState;
using UserAttributesMutation = Spotify.UserAttributes.Proto.UserAttributesMutation;
using UserAttributesUpdate = Spotify.ExplicitContent.Proto.UserAttributesUpdate;

namespace Librespot.Connect;

/// <summary>Upstream <c>SpircError</c>.</summary>
public sealed class SpircException : LibrespotException
{
    /// <summary>The error variants.</summary>
    public enum Reason
    {
        /// <summary>Upstream <c>NoData</c>: the response payload was empty (kind Unavailable).</summary>
        NoData,
        /// <summary>Not raised by the v0.8 spirc (kept for compatibility of the contract).</summary>
        UnknownContentType,
        /// <summary>Not raised by the v0.8 spirc (kept for compatibility of the contract).</summary>
        NotAllowedContext,
        /// <summary>Upstream <c>FailedDealerSetup</c> (kind Aborted).</summary>
        FailedDealerSetup,
        /// <summary>Upstream <c>InvalidUri(String)</c> (kind Aborted).</summary>
        InvalidUri,
        /// <summary>Not raised by the v0.8 spirc (kept for compatibility of the contract).</summary>
        TransferFailed,
        /// <summary>Upstream <c>NoUri(&amp;'static str)</c> (kind Unavailable).</summary>
        NoUri,
        /// <summary>Upstream <c>UnknownEndpoint(serde_json::Value)</c> (kind Unimplemented).</summary>
        UnknownEndpoint,
    }

    /// <summary>Which variant this is.</summary>
    public Reason Cause { get; }

    /// <summary>Creates the exception; prefer the static factories, which reproduce upstream's texts and kinds.</summary>
    public SpircException(Reason cause, ErrorKind kind, string detail, Exception? inner = null) : base(kind, detail, inner)
    {
        Cause = cause;
    }

    /// <summary>Upstream <c>NoData</c>: "response payload empty".</summary>
    public static SpircException NoData() => new(Reason.NoData, ErrorKind.Unavailable, "response payload empty");

    /// <summary>Upstream <c>NoUri(what)</c>: "{what} had no uri".</summary>
    public static SpircException NoUri(string what) => new(Reason.NoUri, ErrorKind.Unavailable, $"{what} had no uri");

    /// <summary>Upstream <c>InvalidUri(uri)</c>: "message pushed for another URI" (the uri is not part of the text).</summary>
    public static SpircException InvalidUri(string uri) =>
        new(Reason.InvalidUri, ErrorKind.Aborted, "message pushed for another URI") { Uri = uri };

    /// <summary>Upstream <c>FailedDealerSetup</c>: "failed to put connect state for new device".</summary>
    public static SpircException FailedDealerSetup() =>
        new(Reason.FailedDealerSetup, ErrorKind.Aborted, "failed to put connect state for new device");

    /// <summary>
    /// Upstream <c>UnknownEndpoint(value)</c>: "unknown endpoint: {value:#?}". The JSON value is rendered indented
    /// (upstream prints serde's pretty <c>Debug</c> of the value).
    /// </summary>
    public static SpircException UnknownEndpoint(JsonElement value) =>
        new(Reason.UnknownEndpoint, ErrorKind.Unimplemented, $"unknown endpoint: {RenderJson(value)}");

    /// <summary>The uri carried by <see cref="Reason.InvalidUri"/>, otherwise null.</summary>
    public string? Uri { get; private init; }

    private static string RenderJson(JsonElement value) =>
        value.ValueKind == JsonValueKind.Undefined
            ? "Null"
            : JsonSerializer.Serialize(value, IndentedJson);

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
}

/// <summary>Commands sent from the <see cref="Spirc"/> handle to the <see cref="SpircTask"/> (upstream <c>SpircCommand</c>).</summary>
internal abstract record SpircCommand
{
    private SpircCommand()
    {
    }

    public sealed record Play : SpircCommand;

    public sealed record PlayPause : SpircCommand;

    public sealed record Pause : SpircCommand;

    public sealed record Prev : SpircCommand;

    public sealed record Next : SpircCommand;

    public sealed record VolumeUp : SpircCommand;

    public sealed record VolumeDown : SpircCommand;

    public sealed record Shutdown : SpircCommand;

    public sealed record Shuffle(bool Value) : SpircCommand;

    public sealed record Repeat(bool Value) : SpircCommand;

    public sealed record RepeatTrack(bool Value) : SpircCommand;

    public sealed record Disconnect(bool PauseFirst) : SpircCommand;

    public sealed record SetPosition(uint PositionMs) : SpircCommand;

    public sealed record SetVolume(ushort Volume) : SpircCommand;

    public sealed record Activate : SpircCommand;

    public sealed record Transfer(TransferRequest? Request) : SpircCommand;

    public sealed record Load(LoadRequest Request) : SpircCommand;

    public sealed record AddToQueue(SpotifyUri Uri) : SpircCommand;

    public sealed record ClearQueue : SpircCommand;

    /// <summary>Rust <c>{:?}</c> of the command, for the trace/warn logs.</summary>
    public string ToDebugString() => this switch
    {
        Shuffle s => $"Shuffle({Bool(s.Value)})",
        Repeat r => $"Repeat({Bool(r.Value)})",
        RepeatTrack r => $"RepeatTrack({Bool(r.Value)})",
        Disconnect d => $"Disconnect {{ pause: {Bool(d.PauseFirst)} }}",
        SetPosition p => $"SetPosition({p.PositionMs.ToString(CultureInfo.InvariantCulture)})",
        SetVolume v => $"SetVolume({v.Volume.ToString(CultureInfo.InvariantCulture)})",
        Transfer t => $"Transfer({(t.Request is null ? "None" : $"Some({t.Request})")})",
        Load l => $"Load({l.Request})",
        AddToQueue a => $"AddToQueue({a.Uri.ToUri()})",
        _ => GetType().Name,
    };

    private static string Bool(bool value) => value ? "true" : "false";
}

/// <summary>
/// The subset of the <see cref="Playback.Player"/> facade that spirc calls. Production code wraps the real player
/// (<see cref="PlayerFacade"/>); tests substitute a recorder, since the player itself is owned by another package.
/// </summary>
internal interface ISpircPlayer
{
    void Load(SpotifyUri trackId, bool startPlaying, uint positionMs);

    void Preload(SpotifyUri trackId);

    void Play();

    void Pause();

    void Stop();

    void Seek(uint positionMs);

    void EmitVolumeChangedEvent(ushort volume);

    void EmitFilterExplicitContentChangedEvent(bool filter);

    void EmitSessionConnectedEvent(string connectionId, string userName);

    void EmitSessionDisconnectedEvent(string connectionId, string userName);

    void EmitSessionClientChangedEvent(string clientId, string clientName, string clientBrandName, string clientModelName);

    void EmitShuffleChangedEvent(bool shuffle);

    void EmitRepeatChangedEvent(bool context, bool track);

    void EmitAutoPlayChangedEvent(bool autoPlay);

    void EmitSetQueueEvent(string contextUri, QueueTrack? currentTrack, IReadOnlyList<QueueTrack> nextTracks, IReadOnlyList<QueueTrack> prevTracks);
}

/// <summary>
/// Adapts the public <see cref="Playback.Player"/> facade to the <see cref="ISpircPlayer"/> test seam; every member
/// forwards directly (the facade itself logs "Player Commands Error" when the player thread is gone, like upstream).
/// </summary>
internal sealed class PlayerFacade(Player player) : ISpircPlayer
{
    public void Load(SpotifyUri trackId, bool startPlaying, uint positionMs) => player.Load(trackId, startPlaying, positionMs);

    public void Preload(SpotifyUri trackId) => player.Preload(trackId);

    public void Play() => player.Play();

    public void Pause() => player.Pause();

    public void Stop() => player.Stop();

    public void Seek(uint positionMs) => player.Seek(positionMs);

    public void EmitVolumeChangedEvent(ushort volume) => player.EmitVolumeChangedEvent(volume);

    public void EmitFilterExplicitContentChangedEvent(bool filter) => player.EmitFilterExplicitContentChangedEvent(filter);

    public void EmitSessionConnectedEvent(string connectionId, string userName) =>
        player.EmitSessionConnectedEvent(connectionId, userName);

    public void EmitSessionDisconnectedEvent(string connectionId, string userName) =>
        player.EmitSessionDisconnectedEvent(connectionId, userName);

    public void EmitSessionClientChangedEvent(string clientId, string clientName, string clientBrandName, string clientModelName) =>
        player.EmitSessionClientChangedEvent(clientId, clientName, clientBrandName, clientModelName);

    public void EmitShuffleChangedEvent(bool shuffle) => player.EmitShuffleChangedEvent(shuffle);

    public void EmitRepeatChangedEvent(bool context, bool track) => player.EmitRepeatChangedEvent(context, track);

    public void EmitAutoPlayChangedEvent(bool autoPlay) => player.EmitAutoPlayChangedEvent(autoPlay);

    public void EmitSetQueueEvent(string contextUri, QueueTrack? currentTrack, IReadOnlyList<QueueTrack> nextTracks, IReadOnlyList<QueueTrack> prevTracks) =>
        player.EmitSetQueueEvent(contextUri, currentTrack, nextTracks, prevTracks);
}

/// <summary>
/// The dealer subscriptions of a <see cref="SpircTask"/> (upstream's <c>BoxedStreamResult</c>/<c>BoxedStream</c>
/// fields). The enumerators are created as soon as the subscriptions are, so that disposing them unsubscribes even
/// when the task never runs.
/// </summary>
internal sealed class SpircStreams(
    IAsyncEnumerator<Result<string>> connectionIdUpdate,
    IAsyncEnumerator<Result<ClusterUpdate>> connectStateUpdate,
    IAsyncEnumerator<Result<SetVolumeCommand>> connectStateVolumeUpdate,
    IAsyncEnumerator<Result<LogoutCommand>> connectStateLogoutRequest,
    IAsyncEnumerator<Result<PlaylistModificationInfo>> playlistUpdate,
    IAsyncEnumerator<Result<FallbackWrapper<SessionUpdate>>> sessionUpdate,
    ChannelReader<RequestReply> connectStateCommand,
    IAsyncEnumerator<Result<UserAttributesUpdate>> userAttributesUpdate,
    IAsyncEnumerator<Result<UserAttributesMutation>> userAttributesMutation) : IAsyncDisposable
{
    public IAsyncEnumerator<Result<string>> ConnectionIdUpdate { get; } = connectionIdUpdate;

    public IAsyncEnumerator<Result<ClusterUpdate>> ConnectStateUpdate { get; } = connectStateUpdate;

    public IAsyncEnumerator<Result<SetVolumeCommand>> ConnectStateVolumeUpdate { get; } = connectStateVolumeUpdate;

    public IAsyncEnumerator<Result<LogoutCommand>> ConnectStateLogoutRequest { get; } = connectStateLogoutRequest;

    public IAsyncEnumerator<Result<PlaylistModificationInfo>> PlaylistUpdate { get; } = playlistUpdate;

    public IAsyncEnumerator<Result<FallbackWrapper<SessionUpdate>>> SessionUpdate { get; } = sessionUpdate;

    public ChannelReader<RequestReply> ConnectStateCommand { get; } = connectStateCommand;

    public IAsyncEnumerator<Result<UserAttributesUpdate>> UserAttributesUpdate { get; } = userAttributesUpdate;

    public IAsyncEnumerator<Result<UserAttributesMutation>> UserAttributesMutation { get; } = userAttributesMutation;

    /// <summary>Disposes every enumerator (upstream: the streams are dropped). Never throws.</summary>
    public async ValueTask DisposeAsync()
    {
        IAsyncDisposable[] all =
        [
            ConnectionIdUpdate, ConnectStateUpdate, ConnectStateVolumeUpdate, ConnectStateLogoutRequest, PlaylistUpdate,
            SessionUpdate, UserAttributesUpdate, UserAttributesMutation,
        ];

        foreach (var disposable in all)
        {
            try
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // dropping a stream cannot fail upstream
            }
        }
    }
}

/// <summary>The Spotify Connect handle; every method sends a command to the running <see cref="SpircTask"/>.</summary>
public sealed class Spirc
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<Spirc>();

    private static int _spircCounter = -1;

    private readonly ChannelWriter<SpircCommand> _commands;

    internal Spirc(ChannelWriter<SpircCommand> commands)
    {
        _commands = commands;
    }

    /// <summary>
    /// Initializes a new spotify connect device (upstream <c>Spirc::new(config, session, credentials, player, mixer)</c>).
    /// </summary>
    /// <remarks>
    /// The returned tuple consists of a handle to the <see cref="Spirc"/> that can control the local connect device
    /// when active, and the task of the spirc event loop that processes the whole connect device logic (upstream
    /// returns a future; the C# task is already running and completes when the loop ends).
    /// </remarks>
    /// <param name="config">The connect device configuration.</param>
    /// <param name="session">The session; it is connected with <paramref name="credentials"/> after the dealer listeners are registered.</param>
    /// <param name="credentials">The credentials used to connect the session.</param>
    /// <param name="player">The player that plays the tracks.</param>
    /// <param name="mixer">The mixer that applies the volume.</param>
    /// <param name="cancellationToken">Cancels the setup (client token, connection, access token), not the returned task.</param>
    /// <param name="loopCancellation">
    /// Stops the returned task's event loop, also before the connect connection is established (when the
    /// <see cref="Shutdown"/> command is not yet processed); the cleanup after the loop still runs. Port addition:
    /// upstream callers drop the returned future instead.
    /// </param>
    /// <exception cref="LibrespotException">Registering the listeners, connecting or authenticating failed.</exception>
    public static async Task<(Spirc Spirc, Task SpircTask)> CreateAsync(
        ConnectConfig config,
        Session session,
        Credentials credentials,
        Player player,
        IMixer mixer,
        CancellationToken cancellationToken = default,
        CancellationToken loopCancellation = default)
    {
        var spircId = Interlocked.Increment(ref _spircCounter);
        Log.LogDebug("new Spirc[{SpircId}]", spircId);

        var emitSetQueueEvents = config.EmitSetQueueEvents;
        var connectState = ConnectState.New(config, session);

        var streams = await SubscribeAsync(session.Dealer).ConfigureAwait(false);
        var commands = System.Threading.Channels.Channel.CreateUnbounded<SpircCommand>(new UnboundedChannelOptions { SingleReader = true });
        SpircTask task;
        try
        {
            // pre-acquire client_token, preventing multiple request while running
            _ = await session.SpClient.ClientTokenAsync(cancellationToken).ConfigureAwait(false);

            // Connect *after* all message listeners are registered
            await session.ConnectAsync(credentials, true, cancellationToken).ConfigureAwait(false);

            // pre-acquire access_token (we need to be authenticated to retrieve a token)
            _ = await session.Login5.AuthTokenAsync(cancellationToken).ConfigureAwait(false);

            var playerEvents = player.GetPlayerEventChannel();

            task = new SpircTask(
                spircId,
                session,
                new PlayerFacade(player),
                mixer,
                connectState,
                emitSetQueueEvents,
                streams,
                commands,
                playerEvents.Reader);
        }
        catch
        {
            await streams.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var spirc = new Spirc(commands.Writer);

        task.ApplyInitialVolume();

        return (spirc, Task.Run(() => task.RunAsync(loopCancellation), CancellationToken.None));
    }

    /// <summary>Upstream <c>extract_connection_id</c>: the <c>Spotify-Connection-Id</c> header of the pusher message.</summary>
    /// <exception cref="SpircException">The header is missing (<c>InvalidUri</c>).</exception>
    internal static string ExtractConnectionId(Message msg) =>
        msg.Headers.TryGetValue("Spotify-Connection-Id", out var connectionId)
            ? connectionId
            : throw SpircException.InvalidUri(msg.Uri);

    /// <summary>Registers every dealer listener and the request handler exactly as upstream <c>Spirc::new</c>.</summary>
    private static async Task<SpircStreams> SubscribeAsync(DealerManager dealer)
    {
        var created = new List<IAsyncDisposable>();

        IAsyncEnumerator<Result<T>> Listen<T>(string uri, Func<Message, T> parse)
        {
            var enumerator = dealer.ListenFor(uri, parse).GetAsyncEnumerator();
            created.Add(enumerator);
            return enumerator;
        }

        try
        {
            var connectionIdUpdate = Listen("hm://pusher/v1/connections/", ExtractConnectionId);
            var connectStateUpdate = Listen("hm://connect-state/v1/cluster", Message.FromRaw<ClusterUpdate>);
            var connectStateVolumeUpdate = Listen("hm://connect-state/v1/connect/volume", Message.FromRaw<SetVolumeCommand>);
            var connectStateLogoutRequest = Listen("hm://connect-state/v1/connect/logout", Message.FromRaw<LogoutCommand>);
            var playlistUpdate = Listen("hm://playlist/v2/playlist/", Message.FromRaw<PlaylistModificationInfo>);
            var sessionUpdate = Listen("social-connect/v2/session_update", Message.TryFromJson<SessionUpdate>);
            var userAttributesUpdate = Listen("spotify:user:attributes:update", Message.FromRaw<UserAttributesUpdate>);
            // can be trigger by toggling autoplay in a desktop client
            var userAttributesMutation = Listen("spotify:user:attributes:mutated", Message.FromRaw<UserAttributesMutation>);
            var connectStateCommand = dealer.HandleFor("hm://connect-state/v1/player/command");

            return new SpircStreams(
                connectionIdUpdate,
                connectStateUpdate,
                connectStateVolumeUpdate,
                connectStateLogoutRequest,
                playlistUpdate,
                sessionUpdate,
                connectStateCommand,
                userAttributesUpdate,
                userAttributesMutation);
        }
        catch
        {
            foreach (var disposable in created)
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private void Send(SpircCommand command)
    {
        if (!_commands.TryWrite(command))
        {
            // upstream: `SendError` -> Error { kind: Internal, "channel closed" }
            throw LibrespotException.Internal("channel closed");
        }
    }

    /// <summary>
    /// Safely shuts the spirc down: pauses the playback, disconnects the connect device and brings the task returned
    /// by <see cref="CreateAsync"/> to an end.
    /// </summary>
    /// <exception cref="LibrespotException">The spirc task has already stopped.</exception>
    public void Shutdown() => Send(new SpircCommand.Shutdown());

    /// <summary>Resumes the playback. Does nothing if we are not the active device, or it isn't paused.</summary>
    /// <exception cref="LibrespotException">The spirc task has already stopped.</exception>
    public void Play() => Send(new SpircCommand.Play());

    /// <summary>Resumes or pauses the playback. Does nothing if we are not the active device.</summary>
    /// <exception cref="LibrespotException">The spirc task has already stopped.</exception>
    public void PlayPause() => Send(new SpircCommand.PlayPause());

    /// <summary>Pauses the playback. Does nothing if we are not the active device, or if it isn't playing.</summary>
    /// <exception cref="LibrespotException">The spirc task has already stopped.</exception>
    public void Pause() => Send(new SpircCommand.Pause());

    /// <summary>
    /// Seeks to the beginning or skips to the previous track: seeks to the beginning when the current track position
    /// is greater than 3 seconds. Does nothing if we are not the active device.
    /// </summary>
    /// <exception cref="LibrespotException">The spirc task has already stopped.</exception>
    public void Prev() => Send(new SpircCommand.Prev());

    /// <summary>Skips to the next track. Does nothing if we are not the active device.</summary>
    /// <exception cref="LibrespotException">The spirc task has already stopped.</exception>
    public void Next() => Send(new SpircCommand.Next());

    /// <summary>Increases the volume by the configured steps of <see cref="ConnectConfig"/>. Does nothing if we are not the active device.</summary>
    /// <exception cref="LibrespotException">The spirc task has already stopped.</exception>
    public void VolumeUp() => Send(new SpircCommand.VolumeUp());

    /// <summary>Decreases the volume by the configured steps of <see cref="ConnectConfig"/>. Does nothing if we are not the active device.</summary>
    /// <exception cref="LibrespotException">The spirc task has already stopped.</exception>
    public void VolumeDown() => Send(new SpircCommand.VolumeDown());

    /// <summary>
    /// Shuffles the playback according to the value: if true shuffles/reshuffles the playback, otherwise does
    /// nothing (if not shuffled) or unshuffles the playback while resuming at the position of the current track.
    /// Does nothing if we are not the active device.
    /// </summary>
    /// <exception cref="LibrespotException">The spirc task has already stopped.</exception>
    public void Shuffle(bool shuffle) => Send(new SpircCommand.Shuffle(shuffle));

    /// <summary>Repeats the playback context according to the value. Does nothing if we are not the active device.</summary>
    /// <exception cref="LibrespotException">The spirc task has already stopped.</exception>
    public void Repeat(bool repeat) => Send(new SpircCommand.Repeat(repeat));

    /// <summary>
    /// Repeats the current track if true. Does nothing if we are not the active device. Skipping to the next track
    /// disables the repeating.
    /// </summary>
    /// <exception cref="LibrespotException">The spirc task has already stopped.</exception>
    public void RepeatTrack(bool repeat) => Send(new SpircCommand.RepeatTrack(repeat));

    /// <summary>Updates the volume to the given value. Does nothing if we are not the active device.</summary>
    /// <exception cref="LibrespotException">The spirc task has already stopped.</exception>
    public void SetVolume(ushort volume) => Send(new SpircCommand.SetVolume(volume));

    /// <summary>
    /// Updates the position to the given value. Does nothing if we are not the active device. If the value is
    /// greater than the track duration, the update is ignored.
    /// </summary>
    /// <exception cref="LibrespotException">The spirc task has already stopped.</exception>
    public void SetPositionMs(uint positionMs) => Send(new SpircCommand.SetPosition(positionMs));

    /// <summary>
    /// Loads a new context and replaces the current one. Does nothing if we are not the active device. Does not
    /// overwrite the queue.
    /// </summary>
    /// <exception cref="LibrespotException">The spirc task has already stopped.</exception>
    public void Load(LoadRequest command) => Send(new SpircCommand.Load(command));

    /// <summary>
    /// Adds a track, episode, album or playlist to the queue. Does nothing if we are not the active device. For
    /// albums and playlists, all tracks/episodes are resolved and added to the queue.
    /// </summary>
    /// <exception cref="LibrespotException">
    /// The uri is not a track, episode, album or playlist (kind InvalidArgument), or the spirc task has already stopped.
    /// </exception>
    public void AddToQueue(SpotifyUri uri)
    {
        if (uri is not (SpotifyUri.Track or SpotifyUri.Episode or SpotifyUri.Album or SpotifyUri.Playlist))
        {
            throw LibrespotException.InvalidArgument("uri");
        }

        Send(new SpircCommand.AddToQueue(uri));
    }

    /// <summary>
    /// Removes all queued tracks from the next tracks. Does nothing if we are not the active device. Only tracks
    /// added to the queue (via <see cref="AddToQueue"/> or a connect client) are removed; the current track, even if
    /// it was queued, and the tracks of the context are kept; the next tracks are filled up from the context again.
    /// </summary>
    /// <exception cref="LibrespotException">The spirc task has already stopped.</exception>
    public void ClearQueue() => Send(new SpircCommand.ClearQueue());

    /// <summary>Disconnects the current device and pauses the playback according to the value. Does nothing if we are not the active device.</summary>
    /// <exception cref="LibrespotException">The spirc task has already stopped.</exception>
    public void Disconnect(bool pause) => Send(new SpircCommand.Disconnect(pause));

    /// <summary>
    /// Acquires the control as active connect device. Does not <see cref="Transfer"/> the playback. Does nothing if
    /// we are already the active device.
    /// </summary>
    /// <exception cref="LibrespotException">The spirc task has already stopped.</exception>
    public void Activate() => Send(new SpircCommand.Activate());

    /// <summary>Acquires the control as active connect device over the transfer flow. Does nothing if we are already the active device.</summary>
    /// <exception cref="LibrespotException">The spirc task has already stopped.</exception>
    public void Transfer(TransferRequest? transferRequest) => Send(new SpircCommand.Transfer(transferRequest));
}

/// <summary>The connect event loop and all command handlers (upstream <c>SpircTask</c>).</summary>
internal sealed class SpircTask
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<SpircTask>();

    internal const int ContextFetchThreshold = 2;

    // delay to update volume after a certain amount of time, instead on each update request
    internal static readonly TimeSpan VolumeUpdateDelay = TimeSpan.FromMilliseconds(500);

    // to reduce updates to remote, we group some request by waiting for a set amount of time
    internal static readonly TimeSpan UpdateStateDelay = TimeSpan.FromMilliseconds(200);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly ISpircPlayer _player;
    private readonly IMixer _mixer;

    /// <summary>the state management object</summary>
    private readonly ConnectState _connectState;

    private readonly SpircStreams _streams;
    private readonly Channel<SpircCommand> _commandChannel;
    private readonly ChannelReader<SpircCommand> _commands;
    private ChannelReader<PlayerEvent>? _playerEvents;

    private readonly ContextResolver _contextResolver;
    private readonly bool _emitSetQueueEvents;
    private readonly Session _session;
    private readonly int _spircId;

    private bool _connectEstablished;
    private ulong? _playRequestId;
    private SpircPlayStatus _playStatus = new SpircPlayStatus.Stopped();
    private bool _shutdown;

    /// <summary>is set when transferring, and used after resolving the contexts to finish the transfer</summary>
    private TransferState? _transferState;

    /// <summary>
    /// when set to true, it will update the volume after <see cref="VolumeUpdateDelay"/>, when no other future
    /// resolves, otherwise resets the delay
    /// </summary>
    private bool _updateVolume;

    /// <summary>
    /// when set to true, it will update the state after <see cref="UpdateStateDelay"/>, when no other future
    /// resolves, otherwise resets the delay
    /// </summary>
    private bool _updateState;

    internal SpircTask(
        int spircId,
        Session session,
        ISpircPlayer player,
        IMixer mixer,
        ConnectState connectState,
        bool emitSetQueueEvents,
        SpircStreams streams,
        Channel<SpircCommand> commands,
        ChannelReader<PlayerEvent>? playerEvents)
    {
        _spircId = spircId;
        _session = session;
        _player = player;
        _mixer = mixer;
        _connectState = connectState;
        _emitSetQueueEvents = emitSetQueueEvents;
        _streams = streams;
        _commandChannel = commands;
        _commands = commands.Reader;
        _playerEvents = playerEvents;
        _contextResolver = new ContextResolver(session);
    }

    // ---- state accessors for tests -------------------------------------------------------------------------------

    internal ConnectState ConnectState => _connectState;

    internal SpircPlayStatus PlayStatus
    {
        get => _playStatus;
        set => _playStatus = value;
    }

    internal bool UpdateState => _updateState;

    internal bool UpdateVolume => _updateVolume;

    internal bool IsShutdown => _shutdown;

    internal ulong? PlayRequestId => _playRequestId;

    internal ContextResolver ContextResolver => _contextResolver;

    // ---- setup ----------------------------------------------------------------------------------------------------

    /// <summary>The part of upstream <c>Spirc::new</c> that applies the configured initial volume to the mixer.</summary>
    internal void ApplyInitialVolume()
    {
        var initialVolume = _connectState.DeviceInfo.Volume;
        _connectState.SetVolume(0);

        if (initialVolume <= ushort.MaxValue)
        {
            SetVolume((ushort)initialVolume);
            // we don't want to update the volume initially,
            // we just want to set the mixer to the correct volume
            _updateVolume = false;
        }
        else
        {
            Log.LogError("failed to update initial volume: {Why}", "out of range integral type conversion attempted");
        }
    }

    // ---- the event loop -------------------------------------------------------------------------------------------

    /// <summary>One enabled <c>select!</c> branch: the pending read and what to do once it completed.</summary>
    /// <param name="Pending">Completes when the branch is ready.</param>
    /// <param name="Run">Handles the branch; returns false to leave the loop (upstream <c>break</c>).</param>
    private sealed record Branch(Task Pending, Func<Task<bool>> Run);

    /// <summary>A dealer stream with at most one outstanding <c>MoveNextAsync</c> (upstream <c>stream.next()</c>).</summary>
    internal sealed class StreamSource<T>(string name, IAsyncEnumerator<Result<T>> enumerator)
    {
        private Task<bool>? _pending;

        public string Name => name;

        public Task<bool> Next() => _pending ??= enumerator.MoveNextAsync().AsTask();

        public Result<T> Take()
        {
            _pending = null;
            return enumerator.Current;
        }

        public void Reset() => _pending = null;
    }

    /// <summary>A channel with at most one outstanding <c>WaitToReadAsync</c> (upstream <c>rx.recv()</c>).</summary>
    internal sealed class ChannelSource<T>(ChannelReader<T> reader)
    {
        private Task<bool>? _pending;

        public Task<bool> Wait() => _pending ??= reader.WaitToReadAsync().AsTask();

        /// <summary>Upstream <c>recv()</c> after the wait completed: the item, or false when the channel is closed.</summary>
        public bool TryTake([MaybeNullWhen(false)] out T item)
        {
            _pending = null;
            return reader.TryRead(out item);
        }
    }

    /// <summary>Upstream <c>SpircTask::run</c>.</summary>
    /// <param name="cancellationToken">
    /// Ends the event loop like upstream's <c>Shutdown</c> command does, also before the connect connection is
    /// established (upstream callers get the same by dropping the future); the cleanup after the loop (disconnect,
    /// connect state deletion, dealer close) still runs.
    /// </param>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await RunLoopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await _streams.DisposeAsync().ConfigureAwait(false);
            // upstream: impl Drop for SpircTask
            Log.LogDebug("drop Spirc[{SpircId}]", _spircId);
        }
    }

    /// <summary>The pending reads of every <c>select!</c> branch, kept across the iterations of the loop.</summary>
    internal sealed class LoopSources(SpircStreams streams, ChannelReader<SpircCommand> commands, ChannelReader<PlayerEvent>? playerEvents)
    {
        public StreamSource<string> ConnectionIdUpdate { get; } = new("connection_id_update", streams.ConnectionIdUpdate);

        public StreamSource<ClusterUpdate> ClusterUpdate { get; } = new("cluster_update", streams.ConnectStateUpdate);

        public ChannelSource<RequestReply> Request { get; } = new(streams.ConnectStateCommand);

        public StreamSource<SetVolumeCommand> VolumeUpdate { get; } = new("volume_update", streams.ConnectStateVolumeUpdate);

        public StreamSource<LogoutCommand> LogoutRequest { get; } = new("logout_request", streams.ConnectStateLogoutRequest);

        public StreamSource<PlaylistModificationInfo> PlaylistUpdate { get; } = new("playlist_update", streams.PlaylistUpdate);

        public StreamSource<UserAttributesUpdate> UserAttributesUpdate { get; } = new("user_attributes_update", streams.UserAttributesUpdate);

        public StreamSource<UserAttributesMutation> UserAttributesMutation { get; } = new("user_attributes_mutation", streams.UserAttributesMutation);

        public StreamSource<FallbackWrapper<SessionUpdate>> SessionUpdate { get; } = new("session_update", streams.SessionUpdate);

        /// <summary>Null once the command channel is closed (see the deviation in <see cref="RunIterationAsync"/>).</summary>
        public ChannelSource<SpircCommand>? Commands { get; set; } = new(commands);

        /// <summary>Null when there is no player event channel or once it is closed.</summary>
        public ChannelSource<PlayerEvent>? PlayerEvents { get; set; } = playerEvents is null ? null : new(playerEvents);
    }

    /// <summary>The sources over this task's streams and channels, for <see cref="RunIterationAsync"/>.</summary>
    internal LoopSources CreateLoopSources() => new(_streams, _commands, _playerEvents);

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _session.Dealer.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // upstream: the future was dropped while the dealer was starting
            return;
        }
        catch (Exception e)
        {
            Log.LogError("starting dealer failed: {Why}", Why(e));
            return;
        }

        var sources = CreateLoopSources();
        while (!_session.IsInvalid && !_shutdown && !cancellationToken.IsCancellationRequested)
        {
            if (!await RunIterationAsync(sources, cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }

        if (!_shutdown && _connectState.IsActive)
        {
            Log.LogWarning("unexpected shutdown");
            try
            {
                await HandleDisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.LogError("error during disconnecting: {Why}", Why(e));
            }
        }

        // this should clear the active session id, leaving an empty state
        try
        {
            await _session.SpClient.DeleteConnectStateRequestAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.LogError("error during connect state deletion: {Why}", Why(e));
        }

        await _session.Dealer.CloseAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// One iteration of upstream's <c>select!</c>: builds the enabled branches in upstream's order, waits until one
    /// is ready, drops the others and runs the selected one. Does not start the dealer.
    /// </summary>
    /// <returns>False to leave the loop (upstream <c>break</c>).</returns>
    internal async Task<bool> RunIterationAsync(LoopSources sources, CancellationToken cancellationToken = default)
    {
        var connectionIdUpdate = sources.ConnectionIdUpdate;
        var clusterUpdate = sources.ClusterUpdate;
        var request = sources.Request;
        var volumeUpdate = sources.VolumeUpdate;
        var logoutRequest = sources.LogoutRequest;
        var playlistUpdate = sources.PlaylistUpdate;
        var userAttributesUpdate = sources.UserAttributesUpdate;
        var userAttributesMutation = sources.UserAttributesMutation;
        var sessionUpdate = sources.SessionUpdate;

        // when state and volume update have a higher priority than context resolving
        // because of that the context resolving has to wait, so that the other tasks can finish
        var allowContextResolving = !_updateState && !_updateVolume;

        // drops the timers and the context request of this iteration; deliberately not linked to the loop
        // cancellation, which would complete them (cancelled) together with the cancellation branch and let them win
        using var iteration = new CancellationTokenSource();
        // the cancellation branch: the loop cancellation, also cancelled with the iteration so that the delay never
        // outlives it
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, iteration.Token);
        var branches = new List<Branch>(15);

        // startup of the dealer requires a connection_id, which is retrieved at the very beginning
        branches.Add(StreamBranch(connectionIdUpdate, "connection_id", async connectionId =>
        {
            try
            {
                await HandleConnectionIdUpdateAsync(connectionId).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.LogError("failed handling connection id update: {Why}", Why(e));
                return false;
            }

            return true;
        }));

        // main dealer update of any remote device updates
        branches.Add(StreamBranch(clusterUpdate, "cluster_update", async update =>
        {
            try
            {
                await HandleClusterUpdateAsync(update).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.LogError("could not dispatch connect state update: {Why}", Why(e));
            }

            return true;
        }));

        // main dealer request handling (dealer expects an answer)
        branches.Add(new Branch(request.Wait(), async () =>
        {
            if (!await request.Wait().ConfigureAwait(false) || !request.TryTake(out var requestReply))
            {
                Log.LogError("{Name} selected, but none received", "request");
                return false;
            }

            try
            {
                await HandleConnectStateRequestAsync(requestReply).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.LogError("couldn't handle connect state command: {Why}", Why(e));
            }

            return true;
        }));

        // volume request handling is send separately (it's more like a fire forget)
        branches.Add(StreamBranch(volumeUpdate, "volume_update", update =>
        {
            if (update.Volume is >= 0 and <= ushort.MaxValue)
            {
                SetVolume((ushort)update.Volume);
            }
            else
            {
                Log.LogError(
                    "can't update volume, failed to parse i32 to u16: {Why}",
                    "out of range integral type conversion attempted");
            }

            return Task.FromResult(true);
        }));

        branches.Add(StreamResultBranch(logoutRequest, logout =>
        {
            // upstream logs the whole `Result` ({logout_request:#?}), the error included
            Log.LogError(
                "received logout request, currently not supported: {LogoutRequest}",
                logout.Error is { } error ? $"Err({error.Message})" : $"Ok({logout.Value})");
            // todo: call logout handling
            return Task.FromResult(true);
        }));

        branches.Add(StreamBranch(playlistUpdate, "playlist_update", update =>
        {
            try
            {
                HandlePlaylistModification(update);
            }
            catch (Exception e)
            {
                Log.LogError("failed to handle playlist modification: {Why}", Why(e));
            }

            return Task.FromResult(true);
        }));

        branches.Add(StreamBranch(userAttributesUpdate, "attributes", attributes =>
        {
            HandleUserAttributesUpdate(attributes);
            return Task.FromResult(true);
        }));

        branches.Add(StreamBranch(userAttributesMutation, "attributes", attributes =>
        {
            HandleUserAttributesMutation(attributes);
            return Task.FromResult(true);
        }));

        branches.Add(StreamBranch(sessionUpdate, "session_update", update =>
        {
            HandleSessionUpdate(update);
            return Task.FromResult(true);
        }));

        if (sources.Commands is { } commands && _connectEstablished)
        {
            branches.Add(new Branch(commands.Wait(), async () =>
            {
                // Deviation: upstream's `recv()` yields None forever once the channel is closed (it only closes
                // on shutdown, which also ends the loop); the port stops polling a closed channel instead.
                if (!await commands.Wait().ConfigureAwait(false) || !commands.TryTake(out var cmd))
                {
                    sources.Commands = null;
                    return true;
                }

                try
                {
                    await HandleCommandAsync(cmd).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    Log.LogDebug("could not dispatch command: {Why}", Why(e));
                }

                return true;
            }));
        }

        if (sources.PlayerEvents is { } playerEvents)
        {
            branches.Add(new Branch(playerEvents.Wait(), async () =>
            {
                // Deviation: a closed player event channel (player stopped) is no longer polled, see above.
                if (!await playerEvents.Wait().ConfigureAwait(false) || !playerEvents.TryTake(out var playerEvent))
                {
                    sources.PlayerEvents = null;
                    return true;
                }

                try
                {
                    HandlePlayerEvent(playerEvent);
                }
                catch (Exception e)
                {
                    Log.LogError("could not dispatch player event: {Why}", Why(e));
                }

                return true;
            }));
        }

        if (_updateState)
        {
            branches.Add(new Branch(Task.Delay(UpdateStateDelay, iteration.Token), async () =>
            {
                _updateState = false;

                try
                {
                    await NotifyAsync().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    Log.LogError("state update: {Why}", Why(e));
                }

                return true;
            }));
        }

        if (_updateVolume)
        {
            branches.Add(new Branch(Task.Delay(VolumeUpdateDelay, iteration.Token), async () =>
            {
                _updateVolume = false;

                Log.LogInformation(
                    "delayed volume update for all devices: volume is now {Volume}",
                    _connectState.DeviceInfo.Volume);
                try
                {
                    await _connectState.NotifyVolumeChangedAsync(_session).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    Log.LogError("error updating connect state for volume update: {Why}", Why(e));
                }

                // for some reason the web-player does need two separate updates, so that the
                // position of the current track is retained, other clients also send a state
                // update before they send the volume update
                try
                {
                    await NotifyAsync().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    Log.LogError("error updating connect state for volume update: {Why}", Why(e));
                }

                return true;
            }));
        }

        // context resolver handling, the idea/reason behind it the following:
        //
        // when we request a context that has multiple pages (for example an artist)
        // resolving all pages at once can take around ~1-30sec, when we resolve
        // everything at once that would block our main loop for that time
        //
        // to circumvent this behavior, we request each context separately here and
        // finish after we received our last item of a type
        Task<(Context? Context, LibrespotException? Error)>? nextContext = null;
        if (allowContextResolving && _contextResolver.HasNext)
        {
            nextContext = GetNextContextAsync(
                () =>
                    // Sending local file URIs to this endpoint results in a Bad Request status.
                    // It's likely appropriate to filter them out anyway; Spotify's backend
                    // has no knowledge about these tracks and so can't do anything with them.
                    _connectState.RecentTrackUris()
                        .Where(t => !t.StartsWith("spotify:local", StringComparison.Ordinal))
                        .ToList(),
                iteration.Token);

            var pendingContext = nextContext;
            branches.Add(new Branch(pendingContext, async () =>
            {
                var updateState = HandleNextContext(await pendingContext.ConfigureAwait(false));
                if (updateState)
                {
                    try
                    {
                        await NotifyAsync().ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        Log.LogError("update after context resolving failed: {Why}", Why(e));
                    }
                }

                return true;
            }));
        }

        // Port addition: upstream callers stop the task by dropping its future, the C# task is stopped through the
        // loop cancellation instead (the last branch, so that ready dealer messages and commands still win)
        branches.Add(new Branch(Task.Delay(Timeout.Infinite, stop.Token), () => Task.FromResult(false)));

        await Task.WhenAny(branches.Select(b => b.Pending)).ConfigureAwait(false);

        // upstream's select! picks one ready branch; the port takes the first ready one in branch order
        var selected = branches.First(b => b.Pending.IsCompleted);

        // the futures of the branches that were not selected are dropped: the timers restart in the next
        // iteration and an in-flight context request is cancelled (the dealer and channel reads stay pending)
        iteration.Cancel();
        if (nextContext is not null && !ReferenceEquals(selected.Pending, nextContext))
        {
            ObserveDropped(nextContext);
        }

        return await selected.Run().ConfigureAwait(false);
    }

    /// <summary>
    /// The <c>unwrap!</c> macro of upstream's <c>run</c> (match form): a stream that ended logs "… selected, but
    /// none received" and leaves the loop; a parse error logs "could not parse …" and continues.
    /// </summary>
    private static Branch StreamBranch<T>(StreamSource<T> source, string okName, Func<T, Task<bool>> onItem) =>
        StreamResultBranch(source, result =>
        {
            if (result.Error is { } error)
            {
                Log.LogError("could not parse {Name}: {Why}", okName, error.Message);
                return Task.FromResult(true);
            }

            return onItem(result.Value!);
        });

    /// <summary>
    /// The <c>unwrap!</c> macro of upstream's <c>run</c> (plain form): a stream that ended logs "… selected, but
    /// none received" and leaves the loop; otherwise the item (a parse result) is handed over as is.
    /// </summary>
    private static Branch StreamResultBranch<T>(StreamSource<T> source, Func<Result<T>, Task<bool>> onItem) =>
        new(source.Next(), async () =>
        {
            bool hasItem;
            try
            {
                hasItem = await source.Next().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                source.Reset();
                Log.LogError("{Name} selected, but none received: {Why}", source.Name, Why(e));
                return false;
            }

            if (!hasItem)
            {
                source.Reset();
                Log.LogError("{Name} selected, but none received", source.Name);
                return false;
            }

            return await onItem(source.Take()).ConfigureAwait(false);
        });

    /// <summary>Observes the outcome of a dropped context request so its cancellation or failure is never unobserved.</summary>
    private static void ObserveDropped(Task task) =>
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// <c>context_resolver.get_next_context(..)</c> as a result value (upstream <c>Result&lt;Context, Error&gt;</c>).
    /// Only a cancellation through <paramref name="cancellationToken"/> is thrown.
    /// </summary>
    private async Task<(Context? Context, LibrespotException? Error)> GetNextContextAsync(
        Func<List<string>> recentTrackUri,
        CancellationToken cancellationToken)
    {
        try
        {
            var context = await _contextResolver.GetNextContextAsync(recentTrackUri, cancellationToken).ConfigureAwait(false);
            return (context, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return (null, LibrespotException.From(e));
        }
    }

    // ---- context resolving ----------------------------------------------------------------------------------------

    /// <summary>Upstream <c>handle_next_context</c>: returns whether the state should be sent.</summary>
    internal bool HandleNextContext((Context? Context, LibrespotException? Error) nextContext)
    {
        if (nextContext.Error is { } why)
        {
            _contextResolver.MarkNextUnavailable();
            _contextResolver.RemoveUsedAndInvalid();
            Log.LogError("{Why}", why.Message);
            return false;
        }

        var context = nextContext.Context!;

        Log.LogDebug("handling next context {Uri}", RustDebug.Option(context.HasUri ? context.Uri : null));

        try
        {
            if (_contextResolver.ApplyNextContext(_connectState, context) is { } remaining)
            {
                _contextResolver.AddList(remaining);
            }
        }
        catch (Exception e)
        {
            Log.LogError("{Why}", Why(e));
        }

        var updateState = false;
        if (_contextResolver.TryFinish(_connectState, ref _transferState))
        {
            AddAutoplayResolvingWhenRequired();
            updateState = true;
        }

        // Fire set queue event if context was successfully loaded
        if (updateState)
        {
            EmitSetQueueEvent();
        }

        _contextResolver.RemoveUsedAndInvalid();
        return updateState;
    }

    /// <summary>Emit set queue event via PlayerEvent.</summary>
    private void EmitSetQueueEvent()
    {
        if (!_emitSetQueueEvents)
        {
            return;
        }

        var statePlayer = _connectState.Player;

        var currentTrack = statePlayer.Track is { } t ? new QueueTrack(t.Uri, t.Provider) : null;
        var nextTracks = statePlayer.NextTracks.Select(n => new QueueTrack(n.Uri, n.Provider)).ToList();
        var prevTracks = statePlayer.PrevTracks.Select(p => new QueueTrack(p.Uri, p.Provider)).ToList();

        var contextUri = _connectState.ContextUri;

        _player.EmitSetQueueEvent(contextUri, currentTrack, nextTracks, prevTracks);
    }

    // todo: is the time_delta still necessary?
    private long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000 * _session.TimeDelta;

    // ---- commands -------------------------------------------------------------------------------------------------

    /// <summary>Upstream <c>handle_command</c>.</summary>
    internal async Task HandleCommandAsync(SpircCommand cmd)
    {
        Log.LogTrace("Received SpircCommand::{Command}", cmd.ToDebugString());
        switch (cmd)
        {
            case SpircCommand.Shutdown:
                Log.LogTrace("Received SpircCommand::Shutdown");
                HandlePause();
                await HandleDisconnectAsync().ConfigureAwait(false);
                _shutdown = true;
                _commandChannel.Writer.TryComplete();
                break;
            case SpircCommand.Transfer transfer when !_connectState.IsActive:
            {
                var deviceId = _session.DeviceId;
                await _session.SpClient.TransferAsync(deviceId, deviceId, transfer.Request).ConfigureAwait(false);
                return;
            }
            case SpircCommand.Activate when !_connectState.IsActive:
                Log.LogTrace("Received SpircCommand::{Command}", cmd.ToDebugString());
                HandleActivate();
                await NotifyAsync().ConfigureAwait(false);
                return;
            case SpircCommand.Transfer or SpircCommand.Activate:
                Log.LogWarning("SpircCommand::{Command} will be ignored while already active", cmd.ToDebugString());
                break;
            case SpircCommand when !_connectState.IsActive:
                Log.LogWarning("SpircCommand::{Command} will be ignored while Not Active", cmd.ToDebugString());
                break;
            case SpircCommand.Disconnect disconnect:
                if (disconnect.PauseFirst)
                {
                    HandlePause();
                }

                await HandleDisconnectAsync().ConfigureAwait(false);
                return;
            case SpircCommand.Play:
                HandlePlay();
                break;
            case SpircCommand.PlayPause:
                HandlePlayPause();
                break;
            case SpircCommand.Pause:
                HandlePause();
                break;
            case SpircCommand.Prev:
                HandlePrev();
                break;
            case SpircCommand.Next:
                HandleNext(null);
                break;
            case SpircCommand.VolumeUp:
                HandleVolumeUp();
                break;
            case SpircCommand.VolumeDown:
                HandleVolumeDown();
                break;
            case SpircCommand.Shuffle shuffle:
                HandleShuffle(shuffle.Value);
                break;
            case SpircCommand.Repeat repeat:
                HandleRepeatContext(repeat.Value);
                break;
            case SpircCommand.RepeatTrack repeat:
                HandleRepeatTrack(repeat.Value);
                break;
            case SpircCommand.SetPosition position:
                HandleSeek(position.PositionMs);
                break;
            case SpircCommand.SetVolume volume:
                SetVolume(volume.Volume);
                break;
            case SpircCommand.Load load:
                await HandleLoadAsync(load.Request, null, null).ConfigureAwait(false);
                break;
            case SpircCommand.AddToQueue addToQueue:
                await HandleAddToQueueAsync(addToQueue.Uri).ConfigureAwait(false);
                break;
            case SpircCommand.ClearQueue:
                _connectState.ClearQueue();
                EmitSetQueueEvent();
                break;
        }

        await NotifyAsync().ConfigureAwait(false);
    }

    // ---- player events --------------------------------------------------------------------------------------------

    /// <summary>Upstream <c>handle_player_event</c>.</summary>
    internal void HandlePlayerEvent(PlayerEvent playerEvent)
    {
        if (playerEvent is PlayerEvent.TrackChanged trackChanged)
        {
            _connectState.UpdateDuration(trackChanged.AudioItem.DurationMs);
            _updateState = true;
            return;
        }

        // update play_request_id
        if (playerEvent is PlayerEvent.PlayRequestIdChanged changed)
        {
            _playRequestId = changed.PlayRequestId;
            return;
        }

        var isCurrentTrack = playerEvent.GetPlayRequestId() is { } eventId
            && _playRequestId is { } currentId
            && eventId == currentId;

        // we only process events if the play_request_id matches. If it doesn't, it is
        // an event that belongs to a previous track and only arrives now due to a race
        // condition. In this case we have updated the state already and don't want to
        // mess with it.
        if (!isCurrentTrack)
        {
            return;
        }

        switch (playerEvent)
        {
            case PlayerEvent.EndOfTrack:
            {
                var nextTrack = _connectState.RepeatTrack ? _connectState.CurrentTrack(t => t.Uri) : null;

                HandleNext(nextTrack);
                break;
            }
            case PlayerEvent.Loading:
                switch (_playStatus)
                {
                    case SpircPlayStatus.LoadingPlay loadingPlay:
                        _connectState.UpdatePosition(loadingPlay.PositionMs, NowMs());
                        Log.LogTrace("==> LoadingPlay");
                        break;
                    case SpircPlayStatus.LoadingPause loadingPause:
                        _connectState.UpdatePosition(loadingPause.PositionMs, NowMs());
                        Log.LogTrace("==> LoadingPause");
                        break;
                    default:
                        _connectState.UpdatePosition(0, NowMs());
                        Log.LogTrace("==> Loading");
                        break;
                }

                break;
            case PlayerEvent.Seeked seeked:
                Log.LogTrace("==> Seeked");
                _connectState.UpdatePosition(seeked.PositionMs, NowMs());
                break;
            case PlayerEvent.Playing or PlayerEvent.PositionCorrection:
            {
                var positionMs = playerEvent switch
                {
                    PlayerEvent.Playing playing => playing.PositionMs,
                    PlayerEvent.PositionCorrection correction => correction.PositionMs,
                    _ => 0u,
                };

                Log.LogTrace("==> Playing");
                var newNominalStartTime = NowMs() - positionMs;
                switch (_playStatus)
                {
                    case SpircPlayStatus.Playing playingStatus:
                        if (Math.Abs(playingStatus.NominalStartTime - newNominalStartTime) > 100)
                        {
                            _playStatus = playingStatus with { NominalStartTime = newNominalStartTime };
                            _connectState.UpdatePosition(positionMs, NowMs());
                        }
                        else
                        {
                            return;
                        }

                        break;
                    case SpircPlayStatus.LoadingPlay or SpircPlayStatus.LoadingPause:
                        _connectState.UpdatePosition(positionMs, NowMs());
                        _playStatus = new SpircPlayStatus.Playing(newNominalStartTime, false);
                        break;
                    default:
                        return;
                }

                break;
            }
            case PlayerEvent.Paused paused:
                Log.LogTrace("==> Paused");
                switch (_playStatus)
                {
                    case SpircPlayStatus.Paused or SpircPlayStatus.Playing:
                        _connectState.UpdatePosition(paused.PositionMs, NowMs());
                        _playStatus = new SpircPlayStatus.Paused(paused.PositionMs, false);
                        break;
                    case SpircPlayStatus.LoadingPlay or SpircPlayStatus.LoadingPause:
                        _connectState.UpdatePosition(paused.PositionMs, NowMs());
                        _playStatus = new SpircPlayStatus.Paused(paused.PositionMs, false);
                        break;
                    default:
                        return;
                }

                break;
            case PlayerEvent.Stopped:
                Log.LogTrace("==> Stopped");
                if (_playStatus is SpircPlayStatus.Stopped)
                {
                    return;
                }

                _playStatus = new SpircPlayStatus.Stopped();
                break;
            case PlayerEvent.TimeToPreloadNextTrack:
                HandlePreloadNextTrack();
                return;
            case PlayerEvent.Unavailable unavailable:
                HandleUnavailable(unavailable.TrackId);
                if (_connectState.CurrentTrack(t => t.Uri) == unavailable.TrackId.ToUri())
                {
                    HandleNext(null);
                }

                break;
            default:
                return;
        }

        _updateState = true;
    }

    // ---- dealer messages ------------------------------------------------------------------------------------------

    /// <summary>Upstream <c>handle_connection_id_update</c>.</summary>
    private async Task HandleConnectionIdUpdateAsync(string connectionId)
    {
        Log.LogTrace("Received connection ID update: \"{ConnectionId}\"", connectionId);
        _session.SetConnectionId(connectionId);

        Cluster? cluster;
        try
        {
            var res = await _connectState.NotifyNewDeviceAppearedAsync(_session).ConfigureAwait(false);
            try
            {
                cluster = Cluster.Parser.ParseFrom(res);
            }
            catch (Google.Protobuf.InvalidProtocolBufferException)
            {
                cluster = null;
            }
        }
        catch (Exception e)
        {
            Log.LogError("{Why}", Why(e));
            cluster = null;
        }

        if (cluster is null)
        {
            throw SpircException.FailedDealerSetup();
        }

        Log.LogDebug(
            "successfully put connect state for {DeviceId} with connection-id {ConnectionId}",
            _session.DeviceId,
            connectionId);

        _connectEstablished = true;

        var clusterSessionId = cluster.PlayerState?.SessionId ?? "";
        var sameSession = clusterSessionId == _session.SessionId || clusterSessionId.Length == 0;
        if (cluster.ActiveDeviceId.Length != 0 || !sameSession)
        {
            Log.LogInformation(
                "active device is <{ActiveDeviceId}> with session <{SessionId}>",
                cluster.ActiveDeviceId,
                clusterSessionId);
            return;
        }
        else if (cluster.TransferData.IsEmpty)
        {
            Log.LogDebug("got empty transfer state, do nothing");
            return;
        }
        else
        {
            Log.LogInformation("trying to take over control automatically, session_id: {SessionId}", clusterSessionId);
        }

        TransferState transferState;
        try
        {
            transferState = TransferState.Parser.ParseFrom(cluster.TransferData);
        }
        catch (Google.Protobuf.InvalidProtocolBufferException e)
        {
            Log.LogError("failed to take over control: {Why}", e.Message);
            return;
        }

        HandleTransfer(transferState);
    }

    /// <summary>Upstream <c>handle_user_attributes_update</c>.</summary>
    private void HandleUserAttributesUpdate(UserAttributesUpdate update)
    {
        Log.LogTrace("Received attributes update: {Update}", update);
        var attributes = new UserAttributes(update.Pairs);
        _session.SetUserAttributes(attributes);
    }

    /// <summary>Upstream <c>handle_user_attributes_mutation</c>.</summary>
    internal void HandleUserAttributesMutation(UserAttributesMutation mutation)
    {
        foreach (var attribute in mutation.Fields)
        {
            var key = attribute.Name;

            if (key == "autoplay" && _session.Config.Autoplay is not null)
            {
                Log.LogTrace("Autoplay override active. Ignoring mutation.");
                continue;
            }

            if (_session.UserData.Attributes.TryGetValue(key, out var oldValue))
            {
                var newValue = oldValue switch
                {
                    "0" => "1",
                    "1" => "0",
                    _ => oldValue,
                };
                _session.SetUserAttribute(key, newValue);

                Log.LogTrace(
                    "Received attribute mutation, {Key} was {OldValue} is now {NewValue}",
                    key,
                    oldValue,
                    newValue);

                if (key == "filter-explicit-content" && newValue == "1")
                {
                    _player.EmitFilterExplicitContentChangedEvent(newValue == "1");
                }

                if (key == "autoplay" && oldValue != newValue)
                {
                    _player.EmitAutoPlayChangedEvent(newValue == "1");

                    AddAutoplayResolvingWhenRequired();
                }
            }
            else
            {
                Log.LogTrace("Received attribute mutation for {Key} but key was not found!", key);
            }
        }
    }

    /// <summary>Upstream <c>handle_cluster_update</c>.</summary>
    private async Task HandleClusterUpdateAsync(ClusterUpdate clusterUpdate)
    {
        var reason = clusterUpdate.UpdateReason;

        var deviceIds = string.Join(", ", clusterUpdate.DevicesThatChanged);
        Log.LogDebug(
            "cluster update: {Reason} from {DeviceIds}, active device: {ActiveDeviceId}",
            reason,
            deviceIds,
            clusterUpdate.Cluster?.ActiveDeviceId ?? "");

        if (clusterUpdate.Cluster is { } cluster)
        {
            var becameInactive = _connectState.IsActive && cluster.ActiveDeviceId != _session.DeviceId;
            if (becameInactive)
            {
                Log.LogInformation("device became inactive");
                await HandleDisconnectAsync().ConfigureAwait(false);
                HandleStop();
            }
            else if (_connectState.IsActive)
            {
                // fixme: workaround fix, because of missing information why it behaves like it does
                //  background: when another device sends a connect-state update, some player's position de-syncs
                //  tried: providing session_id, playback_id, track-metadata "track_player"
                _updateState = true;
            }
        }
        else if (_connectState.IsActive)
        {
            await _connectState.BecameInactiveAsync(_session).ConfigureAwait(false);
        }
    }

    /// <summary>Upstream <c>handle_connect_state_request</c>: handles the request and answers the dealer.</summary>
    internal async Task HandleConnectStateRequestAsync(RequestReply requestReply)
    {
        var (request, sender) = requestReply;
        _connectState.SetLastCommand(request);

        Log.LogDebug(
            "handling: '{Command}' from {SentByDeviceId}",
            request.Command.ToDisplayString(),
            request.SentByDeviceId);

        Reply response;
        try
        {
            await HandleRequestAsync(request).ConfigureAwait(false);
            response = Reply.Success;
        }
        catch (Exception e)
        {
            Log.LogError("failed to handle request: {Why}", Why(e));
            response = Reply.Failure;
        }

        if (!sender.TryWrite(response))
        {
            throw LibrespotException.Internal("channel closed");
        }
    }

    /// <summary>Upstream <c>handle_request</c>.</summary>
    private async Task HandleRequestAsync(Request request)
    {
        switch (request.Command)
        {
            // errors and unknown commands
            case Command.Transfer { Value.Data: null }:
                Log.LogWarning("transfer endpoint didn't contain any data to transfer");
                throw SpircException.NoData();
            case Command.Unknown unknown:
                throw SpircException.UnknownEndpoint(unknown.Value);

            // implicit update of the connect_state
            case Command.UpdateContext updateContext:
            {
                var context = updateContext.Value.Context;
                if (context.HasUri && context.Uri != _connectState.ContextUri)
                {
                    Log.LogDebug(
                        "ignoring context update for <{Uri}>, because it isn't the current context <{ContextUri}>",
                        RustDebug.Option(context.Uri),
                        _connectState.ContextUri);
                }
                else
                {
                    _contextResolver.Add(ResolveContext.FromContext(context, ContextType.Default, ContextAction.Replace));
                }

                return;
            }

            // modification and update of the connect_state
            case Command.Transfer transfer:
                HandleTransfer(transfer.Value.Data!);
                await NotifyAsync().ConfigureAwait(false);
                return;
            case Command.Play play:
            {
                if (!_connectState.IsActive)
                {
                    HandleActivate();
                }

                var (loadRequest, page, fallbackIndex) = LoadRequestFromPlay(play.Value);

                await HandleLoadAsync(loadRequest, page, fallbackIndex).ConfigureAwait(false);

                _connectState.SetOrigin(play.Value.PlayOrigin);
                break;
            }
            case Command.Pause:
                HandlePause();
                break;
            case Command.SeekTo seekTo:
                // for some reason the position is stored in value, not in position
                Log.LogTrace("seek to {SeekTo}", seekTo.Value);
                HandleSeek(seekTo.Value.Value);
                break;
            case Command.SetShufflingContext shuffle:
                HandleShuffle(shuffle.Value.Value);
                break;
            case Command.SetRepeatingContext repeatContext:
                HandleRepeatContext(repeatContext.Value.Value);
                break;
            case Command.SetRepeatingTrack repeatTrack:
                HandleRepeatTrack(repeatTrack.Value.Value);
                break;
            case Command.AddToQueue addToQueue:
                _connectState.AddToQueue(addToQueue.Value.Track, true);
                EmitSetQueueEvent();
                break;
            case Command.SetQueue setQueue:
                _connectState.HandleSetQueue(setQueue.Value);
                EmitSetQueueEvent();
                break;
            case Command.SetOptions setOptions:
            {
                if (setOptions.Value.RepeatingContext is { } repeatContext)
                {
                    HandleRepeatContext(repeatContext);
                }

                if (setOptions.Value.RepeatingTrack is { } repeatTrack)
                {
                    HandleRepeatTrack(repeatTrack);
                }

                if (setOptions.Value.ShufflingContext is { } shuffle)
                {
                    HandleShuffle(shuffle);
                }

                break;
            }
            case Command.SkipNext skipNext:
                HandleNext(skipNext.Value.Track?.Uri);
                break;
            case Command.SkipPrev:
                HandlePrev();
                break;
            case Command.Resume when _playStatus is SpircPlayStatus.Stopped:
                LoadTrack(true, 0);
                break;
            case Command.Resume:
                HandlePlay();
                break;
        }

        _updateState = true;
    }

    /// <summary>
    /// The conversion of a <c>play</c> command into a <see cref="LoadRequest"/> done inline by upstream
    /// <c>handle_request</c>: also pops the last page of the command's context (merged into the loaded context by
    /// <see cref="HandleLoadAsync"/>) and returns the skip-to index used when the track cannot be found.
    /// </summary>
    /// <exception cref="SpircException">The context has neither a uri nor pages (<c>NoUri("context")</c>).</exception>
    internal static (LoadRequest Request, ContextPage? Page, int? FallbackIndex) LoadRequestFromPlay(PlayCommand play)
    {
        var options = play.Options;

        LoadContextOptions? contextOptions = options.PlayerOptionsOverride is { } overrides
            ? new LoadContextOptions.WithOptions(Options.From(overrides))
            : null;

        var fallbackIndex = options.SkipTo?.TrackIndex is { } trackIndex ? (int)trackIndex : (int?)null;

        var loadOptions = new LoadRequestOptions
        {
            StartPlaying = true,
            SeekTo = options.SeekTo ?? 0,
            PlayingTrack = options.SkipTo is { } skipTo ? PlayingTrackFromSkipTo(skipTo) : null,
            ContextOptions = contextOptions,
        };

        var context = play.Context;
        LoadRequest request;
        if (context.HasUri)
        {
            request = LoadRequest.FromContextUri(context.Uri, loadOptions);
        }
        else if (context.Pages.Count > 0)
        {
            var tracks = context.Pages
                .SelectMany(p => p.Tracks)
                .Where(t => t.HasUri)
                .Select(t => t.Uri)
                .ToList();
            request = LoadRequest.FromTracks(tracks, loadOptions);
        }
        else
        {
            throw SpircException.NoUri("context");
        }

        ContextPage? page = null;
        if (context.Pages.Count > 0)
        {
            page = context.Pages[^1];
            context.Pages.RemoveAt(context.Pages.Count - 1);
        }

        return (request, page, fallbackIndex);
    }

    /// <summary>
    /// Upstream <c>impl TryFrom&lt;SkipTo&gt; for PlayingTrack</c> (model.rs): the order of checks is important, as
    /// the index can be 0 but still have an uid or uri, so the index is only used as last resort. Null when the
    /// skip-to is empty.
    /// </summary>
    internal static PlayingTrack? PlayingTrackFromSkipTo(SkipTo value)
    {
        if (value.TrackUri is { } uri)
        {
            return new PlayingTrack.Uri(uri);
        }
        else if (value.TrackUid is { } uid)
        {
            return new PlayingTrack.Uid(uid);
        }
        else if (value.TrackIndex is { } index)
        {
            return new PlayingTrack.Index(index);
        }

        return null;
    }

    // ---- transfer / activation ------------------------------------------------------------------------------------

    /// <summary>Upstream <c>handle_transfer</c>.</summary>
    private void HandleTransfer(TransferState transfer)
    {
        var transferContext = transfer.CurrentSession?.Context;
        string? ctxUri;
        if (transferContext is null || !transferContext.HasUri)
        {
            throw SpircException.NoUri("transfer context");
        }
        else if (transferContext.Uri is "-" or "")
        {
            // can apparently happen when a state is transferred and was started with "uris" via the api
            ctxUri = null;
        }
        else
        {
            ctxUri = transferContext.Uri;
        }

        _connectState.ResetContext(
            ctxUri is not null ? new ResetContextKind.WhenDifferent(ctxUri) : new ResetContextKind.Completely());

        try
        {
            var track = _connectState.CurrentTrackFromTransfer(transfer);
            Log.LogDebug("found initial track <{Uri}>", track.Uri);
            _connectState.SetTrack(track);
        }
        catch (LibrespotException why)
        {
            Log.LogWarning("didn't find initial track: {Why}", why.Message);
        }

        var autoplay = _connectState.CurrentTrack(t => t.IsAutoplay());
        if (autoplay)
        {
            ctxUri = ctxUri?.Replace("station:", "", StringComparison.Ordinal);
        }

        var fallback = _connectState.CurrentTrack(t => t.Uri);
        var loadFromContextUri = ctxUri is not null;

        if (ctxUri is not null)
        {
            _contextResolver.Add(ResolveContext.FromUri(ctxUri, fallback, ContextType.Default, ContextAction.Replace));
        }
        else
        {
            var allTracks = transferContext.Pages.SelectMany(p => p.Tracks).Select(t => t.Clone()).ToList();

            if (allTracks.Count != 0)
            {
                LoadContextFromTracks(ContextPage.FromTracks(allTracks));
            }
            else
            {
                Log.LogWarning(
                    "tried to transfer with an invalid state, using fallback as ctx_uri ({Fallback})",
                    fallback);
                ctxUri = fallback;
            }
        }

        HandleActivate();

        var timestamp = NowMs();
        var state = _connectState;
        state.HandleInitialTransfer(transfer, ctxUri);

        // adjust active context, so resolve knows for which context it should set up the state
        state.ActiveContext = autoplay ? ContextType.Autoplay : ContextType.Default;

        // update position if the track continued playing
        var playback = transfer.Playback;
        var transferTimestamp = playback?.Timestamp ?? 0;
        long position;
        if (playback is { HasPositionAsOfTimestamp: true } && playback.IsPaused)
        {
            position = playback.PositionAsOfTimestamp;
        }
        else if (playback is { HasPositionAsOfTimestamp: true, PositionAsOfTimestamp: > 0 })
        {
            // update position if the track continued playing
            var timeSincePositionUpdate = timestamp - transferTimestamp;
            position = playback.PositionAsOfTimestamp + timeSincePositionUpdate;
        }
        else
        {
            position = 0;
        }

        var isPlaying = !(playback?.IsPaused ?? false);

        if (_connectState.CurrentTrack(t => t.IsAutoplay()) || autoplay)
        {
            if (ctxUri is not null)
            {
                Log.LogDebug("currently in autoplay context, async resolving autoplay for {CtxUri}", ctxUri);
                _contextResolver.Add(ResolveContext.FromUri(ctxUri, fallback, ContextType.Autoplay, ContextAction.Replace));
            }
            else
            {
                Log.LogWarning("couldn't resolve autoplay context without a context uri");
            }
        }

        if (loadFromContextUri)
        {
            _transferState = transfer;
        }
        else
        {
            var ctx = _connectState.TryGetContext(ContextType.Default);
            if (ctx is null)
            {
                Log.LogWarning(
                    "continuing transfer in an unknown state. {Why}",
                    StateException.NoContext(ContextType.Default).Message);
                _transferState = transfer;
            }
            else
            {
                var idx = ConnectState.FindIndexInContext(ctx, pt => _connectState.CurrentTrack(t => pt.Uri == t.Uri));
                _connectState.ResetPlaybackToPosition(idx);
            }
        }

        if (position is < 0 or > uint.MaxValue)
        {
            // upstream: `position.try_into()?` (TryFromIntError -> FailedPrecondition)
            throw LibrespotException.FailedPrecondition("out of range integral type conversion attempted");
        }

        LoadTrack(isPlaying, (uint)position);
    }

    /// <summary>Upstream <c>handle_disconnect</c>.</summary>
    private async Task HandleDisconnectAsync()
    {
        _contextResolver.Clear();

        _playStatus = new SpircPlayStatus.Stopped();
        _connectState.UpdatePositionInRelation(NowMs());
        await NotifyAsync().ConfigureAwait(false);

        await _connectState.BecameInactiveAsync(_session).ConfigureAwait(false);

        _player.EmitSessionDisconnectedEvent(_session.ConnectionId, _session.Username);
    }

    /// <summary>Upstream <c>handle_stop</c>.</summary>
    private void HandleStop()
    {
        _player.Stop();
        _connectState.UpdatePosition(0, NowMs());
        _connectState.ClearNextTracks();

        try
        {
            _connectState.ResetPlaybackToPosition(null);
        }
        catch (LibrespotException why)
        {
            Log.LogWarning("failed filling up next_track during stopping: {Why}", why.Message);
        }
    }

    /// <summary>Upstream <c>handle_activate</c>.</summary>
    internal void HandleActivate()
    {
        _connectState.SetActive(true);
        _player.EmitSessionConnectedEvent(_session.ConnectionId, _session.Username);
        _player.EmitSessionClientChangedEvent(
            _session.ClientId,
            _session.ClientName,
            _session.ClientBrandName,
            _session.ClientModelName);

        _player.EmitVolumeChangedEvent(unchecked((ushort)_connectState.DeviceInfo.Volume));

        _player.EmitAutoPlayChangedEvent(_session.Autoplay);

        _player.EmitFilterExplicitContentChangedEvent(_session.FilterExplicitContent);

        _player.EmitShuffleChangedEvent(_connectState.ShufflingContext);

        _player.EmitRepeatChangedEvent(_connectState.RepeatContext, _connectState.RepeatTrack);
    }

    // ---- load -----------------------------------------------------------------------------------------------------

    /// <summary>Upstream <c>handle_load</c>.</summary>
    internal async Task HandleLoadAsync(LoadRequest cmd, ContextPage? page, int? fallbackIndex)
    {
        _connectState.ResetContext(
            cmd.Context is PlayContext.Uri uriContext
                ? new ResetContextKind.WhenDifferent(uriContext.Value)
                : new ResetContextKind.Completely());

        _connectState.ResetOptions();

        var autoplay = cmd.Options.ContextOptions is LoadContextOptions.Autoplay;
        switch (cmd.Context)
        {
            case PlayContext.Uri uri:
                await LoadContextFromUriAsync(uri.Value, page, autoplay).ConfigureAwait(false);
                break;
            case PlayContext.Tracks tracks:
                LoadContextFromTracks(ContextPage.FromUris(tracks.Uris));
                break;
        }

        var cmdOptions = cmd.Options;

        _connectState.SetActiveContext(ContextType.Default);

        // for play commands with skip by uid, the context of the command contains
        // tracks with uri and uid, so we merge the new context with the resolved/existing context
        _connectState.MergeContext(page);

        // load here, so that we clear the queue only after we definitely retrieved a new context
        _connectState.ClearNextTracks();
        _connectState.ClearRestrictions();

        Log.LogDebug("play track <{PlayingTrack}>", cmdOptions.PlayingTrack?.ToString() ?? "None");

        int? index = null;
        if (cmdOptions.PlayingTrack is { } playingTrack)
        {
            try
            {
                index = playingTrack switch
                {
                    PlayingTrack.Index i => (int)i.Value,
                    PlayingTrack.Uri u => FindIndexInDefaultContext(t => t.Uri == u.Value),
                    PlayingTrack.Uid u => FindIndexInDefaultContext(t => t.Uid == u.Value),
                    _ => throw new InvalidOperationException("closed hierarchy"),
                };
            }
            catch (StateException why) when (why.Cause == StateException.Reason.CanNotFindTrackInContext)
            {
                Log.LogWarning(
                    "Failed to resolve index by {PlayingTrack}, using fallback index: {FallbackIndex} (Error: {Why})",
                    playingTrack,
                    RustDebug.Option(fallbackIndex),
                    why.Message);
                index = fallbackIndex ?? 0;
            }
        }

        var withOptions = cmdOptions.ContextOptions as LoadContextOptions.WithOptions;
        if (withOptions is not null)
        {
            var options = withOptions.Options;
            Log.LogDebug(
                "loading with shuffle: <{Shuffle}>, repeat track: <{Repeat}> context: <{RepeatTrack}>",
                options.Shuffle,
                options.Repeat,
                options.RepeatTrack);

            _connectState.SetShuffle(options.Shuffle);
            _connectState.SetRepeatContext(options.Repeat);
            _connectState.SetRepeatTrack(options.RepeatTrack);
        }

        if (withOptions is { Options.Shuffle: true })
        {
            if (index is { } i)
            {
                _connectState.SetCurrentTrack(i);
            }
            else
            {
                _connectState.SetCurrentTrackRandom();
            }

            if (_contextResolver.HasNext)
            {
                _connectState.UpdateQueueRevision();
            }
            else
            {
                _connectState.ShuffleNew();
                AddAutoplayResolvingWhenRequired();
            }
        }
        else
        {
            _connectState.SetCurrentTrack(index ?? 0);
            _connectState.ResetPlaybackToPosition(index);
            AddAutoplayResolvingWhenRequired();
        }

        if (_connectState.Player.Track is not null)
        {
            LoadTrack(cmdOptions.StartPlaying, cmdOptions.SeekTo);
        }
        else
        {
            Log.LogInformation("No active track, stopping");
            HandleStop();
        }
    }

    /// <summary>
    /// <c>get_context(Default)?</c> (propagated) followed by <c>find_index_in_context</c> (whose error falls back to
    /// the skip-to index in <see cref="HandleLoadAsync"/>).
    /// </summary>
    private int FindIndexInDefaultContext(Func<ProvidedTrack, bool> predicate)
    {
        // NoContext must propagate, so it is thrown outside the CanNotFindTrackInContext filter of the caller
        var ctx = _connectState.GetContext(ContextType.Default);
        return ConnectState.FindIndexInContext(ctx, predicate);
    }

    /// <summary>Upstream <c>load_context_from_uri</c>.</summary>
    private async Task LoadContextFromUriAsync(string contextUri, ContextPage? page, bool autoplay)
    {
        if (!_connectState.IsActive)
        {
            HandleActivate();
        }

        var updateContext = autoplay ? ContextType.Autoplay : ContextType.Default;

        _connectState.SetActiveContext(updateContext);

        string fallback;
        if (page is not null)
        {
            // check that the uri is valid or the page has a valid uri that can be used
            fallback = ConnectState.FindValidUri(contextUri, page) ?? throw SpircException.InvalidUri(contextUri);
        }
        else
        {
            // when there is no page, the uri should be valid
            fallback = contextUri;
        }

        var currentContextUri = _connectState.ContextUri;

        if (currentContextUri == contextUri && fallback == contextUri)
        {
            Log.LogDebug("context <{ContextUri}> didn't change, no resolving required", currentContextUri);
        }
        else
        {
            Log.LogDebug("resolving context for load command");
            _contextResolver.Clear();
            _contextResolver.Add(ResolveContext.FromUri(contextUri, fallback, updateContext, ContextAction.Replace));
            var context = await GetNextContextAsync(static () => [], CancellationToken.None).ConfigureAwait(false);
            HandleNextContext(context);
        }
    }

    /// <summary>Upstream <c>load_context_from_tracks</c>.</summary>
    private void LoadContextFromTracks(ContextPage tracks)
    {
        const string WebApiUri = "spotify:web-api";
        var ctx = new Context
        {
            // by providing values for uri/url the player in the official client's isn't frozen
            Uri = WebApiUri,
            Url = $"context://{WebApiUri}",
            Pages = { tracks },
        };

        _ = _connectState.UpdateContext(ctx, ContextType.Default);

        EmitSetQueueEvent();
    }

    // ---- playback control -----------------------------------------------------------------------------------------

    /// <summary>Upstream <c>handle_play</c>.</summary>
    private void HandlePlay()
    {
        switch (_playStatus)
        {
            case SpircPlayStatus.Paused paused:
                _player.Play();
                _connectState.UpdatePosition(paused.PositionMs, NowMs());
                _playStatus = new SpircPlayStatus.Playing(
                    NowMs() - paused.PositionMs,
                    paused.PreloadingOfNextTrackTriggered);
                break;
            case SpircPlayStatus.LoadingPause loadingPause:
                _player.Play();
                _playStatus = new SpircPlayStatus.LoadingPlay(loadingPause.PositionMs);
                break;
            default:
                return;
        }

        // Synchronize the volume from the mixer. This is useful on
        // systems that can switch sources from and back to librespot.
        var currentVolume = _mixer.Volume;
        SetVolume(currentVolume);
    }

    /// <summary>Upstream <c>handle_play_pause</c>.</summary>
    private void HandlePlayPause()
    {
        switch (_playStatus)
        {
            case SpircPlayStatus.Paused or SpircPlayStatus.LoadingPause:
                HandlePlay();
                break;
            case SpircPlayStatus.Playing or SpircPlayStatus.LoadingPlay:
                HandlePause();
                break;
        }
    }

    /// <summary>Upstream <c>handle_pause</c>.</summary>
    private void HandlePause()
    {
        switch (_playStatus)
        {
            case SpircPlayStatus.Playing playing:
            {
                _player.Pause();
                var positionMs = unchecked((uint)(NowMs() - playing.NominalStartTime));
                _connectState.UpdatePosition(positionMs, NowMs());
                _playStatus = new SpircPlayStatus.Paused(positionMs, playing.PreloadingOfNextTrackTriggered);
                break;
            }
            case SpircPlayStatus.LoadingPlay loadingPlay:
                _player.Pause();
                _playStatus = new SpircPlayStatus.LoadingPause(loadingPlay.PositionMs);
                break;
        }
    }

    /// <summary>Upstream <c>handle_seek</c>.</summary>
    private void HandleSeek(uint positionMs)
    {
        var duration = _connectState.Player.Duration;
        if (positionMs > duration)
        {
            Log.LogWarning("tried to seek to {PositionMs}ms of {Duration}ms", positionMs, duration);
            return;
        }

        _connectState.UpdatePosition(positionMs, NowMs());
        _player.Seek(positionMs);
        var now = NowMs();
        _playStatus = _playStatus switch
        {
            SpircPlayStatus.LoadingPause => new SpircPlayStatus.LoadingPause(positionMs),
            SpircPlayStatus.LoadingPlay => new SpircPlayStatus.LoadingPlay(positionMs),
            SpircPlayStatus.Paused paused => paused with { PositionMs = positionMs },
            SpircPlayStatus.Playing playing => playing with { NominalStartTime = now - positionMs },
            var stopped => stopped,
        };
    }

    /// <summary>Upstream <c>handle_shuffle</c>.</summary>
    private void HandleShuffle(bool shuffle)
    {
        _player.EmitShuffleChangedEvent(shuffle);
        _connectState.HandleShuffle(shuffle);
    }

    /// <summary>Upstream <c>handle_repeat_context</c>.</summary>
    private void HandleRepeatContext(bool repeat)
    {
        _player.EmitRepeatChangedEvent(repeat, _connectState.RepeatTrack);
        _connectState.HandleSetRepeatContext(repeat);
    }

    /// <summary>Upstream <c>handle_repeat_track</c>.</summary>
    private void HandleRepeatTrack(bool repeat)
    {
        _player.EmitRepeatChangedEvent(_connectState.RepeatContext, repeat);
        _connectState.SetRepeatTrack(repeat);
    }

    /// <summary>Upstream <c>handle_add_to_queue</c>.</summary>
    private async Task HandleAddToQueueAsync(SpotifyUri uri)
    {
        List<string> trackUris;
        switch (uri)
        {
            case SpotifyUri.Track or SpotifyUri.Episode:
                trackUris = [uri.ToUri()];
                break;
            case SpotifyUri.Album or SpotifyUri.Playlist:
                try
                {
                    var context = await _session.SpClient.GetContextAsync(uri.ToUri()).ConfigureAwait(false);
                    trackUris = context.Pages
                        .SelectMany(page => page.Tracks)
                        .Where(track => track.HasUri)
                        .Select(track => track.Uri)
                        .ToList();
                }
                catch (Exception e)
                {
                    Log.LogError("failed to resolve context for {ItemType}: {Why}", uri.ItemType, Why(e));
                    return;
                }

                break;
            default:
                return;
        }

        foreach (var trackUri in trackUris)
        {
            var track = new ProvidedTrack { Uri = trackUri };
            _connectState.AddToQueue(track, true);
        }

        EmitSetQueueEvent();
    }

    /// <summary>Upstream <c>handle_preload_next_track</c>: requests the player thread to preload the next track.</summary>
    private void HandlePreloadNextTrack()
    {
        _playStatus = _playStatus switch
        {
            SpircPlayStatus.Paused paused => paused with { PreloadingOfNextTrackTriggered = true },
            SpircPlayStatus.Playing playing => playing with { PreloadingOfNextTrackTriggered = true },
            var other => other,
        };

        if (_connectState.PreviewNextTrack() is { } trackId)
        {
            _player.Preload(trackId);
        }
    }

    /// <summary>Upstream <c>handle_unavailable</c>: mark unavailable tracks so we can skip them later.</summary>
    private void HandleUnavailable(SpotifyUri trackId)
    {
        _connectState.MarkUnavailable(trackId);
        HandlePreloadNextTrack();
    }

    /// <summary>Upstream <c>add_autoplay_resolving_when_required</c>.</summary>
    private void AddAutoplayResolvingWhenRequired()
    {
        var requireLoadNew = !_connectState.HasNextTracks(ContextFetchThreshold)
            && _session.Autoplay
            && _connectState.ContextUri.Length != 0;

        if (!requireLoadNew)
        {
            return;
        }

        var currentContext = _connectState.ContextUri;
        var fallback = _connectState.CurrentTrack(t => t.Uri);

        var hasTracks = _connectState.TryGetContext(ContextType.Autoplay) is { Tracks.Count: > 0 };

        var resolve = ResolveContext.FromUri(
            currentContext,
            fallback,
            ContextType.Autoplay,
            hasTracks ? ContextAction.Append : ContextAction.Replace);

        _contextResolver.Add(resolve);
    }

    /// <summary>Upstream <c>handle_next</c>.</summary>
    private void HandleNext(string? trackUri)
    {
        var continuePlaying = _connectState.IsPlaying;

        var currentUri = _connectState.CurrentTrack(t => t.Uri);
        var hasNextTrack = trackUri is not null && currentUri == trackUri;

        if (!hasNextTrack)
        {
            // upstream loops until the requested track is current (or, without a requested track, once)
            while (true)
            {
                var index = _connectState.NextTrack();

                currentUri = _connectState.CurrentTrack(t => t.Uri);
                if (trackUri is not null && currentUri != trackUri)
                {
                    continue;
                }

                hasNextTrack = index is not null;
                break;
            }
        }

        if (hasNextTrack)
        {
            AddAutoplayResolvingWhenRequired();
            LoadTrack(continuePlaying, 0);
        }
        else
        {
            Log.LogInformation("Not playing next track because there are no more tracks left in queue.");
            HandleStop();
        }
    }

    /// <summary>
    /// Upstream <c>handle_prev</c>: under 3s it goes to the previous song (starts playing), over 3s it seeks to zero
    /// (retains previous play status).
    /// </summary>
    private void HandlePrev()
    {
        if (Position() < 3000)
        {
            var repeatContext = _connectState.RepeatContext;
            var prev = _connectState.PrevTrack();
            if (prev is null && repeatContext)
            {
                _connectState.ResetPlaybackToPosition(null);
            }
            else if (prev is null)
            {
                _connectState.ResetPlaybackToPosition(null);
                HandleStop();
            }
            else
            {
                LoadTrack(_connectState.IsPlaying, 0);
            }
        }
        else
        {
            HandleSeek(0);
        }
    }

    /// <summary>Upstream <c>handle_volume_up</c>: saturating add of the volume step size.</summary>
    private void HandleVolumeUp()
    {
        var volume = SaturatingAdd(unchecked((ushort)_connectState.DeviceInfo.Volume), _connectState.VolumeStepSize);

        SetVolume(volume);
    }

    /// <summary>Upstream <c>handle_volume_down</c>: saturating subtract of the volume step size.</summary>
    private void HandleVolumeDown()
    {
        var volume = SaturatingSub(unchecked((ushort)_connectState.DeviceInfo.Volume), _connectState.VolumeStepSize);

        SetVolume(volume);
    }

    /// <summary>Rust <c>u16::saturating_add</c>.</summary>
    internal static ushort SaturatingAdd(ushort a, ushort b) => (ushort)Math.Min(a + b, ushort.MaxValue);

    /// <summary>Rust <c>u16::saturating_sub</c>.</summary>
    internal static ushort SaturatingSub(ushort a, ushort b) => (ushort)Math.Max(a - b, 0);

    /// <summary>Upstream <c>handle_playlist_modification</c>.</summary>
    private void HandlePlaylistModification(PlaylistModificationInfo playlistModificationInfo)
    {
        if (!playlistModificationInfo.HasUri)
        {
            throw SpircException.NoUri("playlist modification");
        }

        string uri;
        try
        {
            uri = StrictUtf8.GetString(playlistModificationInfo.Uri.Span);
        }
        catch (DecoderFallbackException e)
        {
            throw LibrespotException.From(e);
        }

        if (_connectState.ContextUri != uri)
        {
            Log.LogDebug(
                "ignoring playlist modification update for playlist <{Uri}>, because it isn't the current context",
                uri);
            return;
        }

        Log.LogDebug("playlist modification for current context: {Uri}", uri);
        _contextResolver.Add(ResolveContext.FromUri(
            uri,
            _connectState.CurrentTrack(t => t.Uri),
            ContextType.Default,
            ContextAction.Replace));
    }

    /// <summary>Upstream <c>handle_session_update</c>.</summary>
    internal void HandleSessionUpdate(FallbackWrapper<SessionUpdate> sessionUpdate)
    {
        // we know that this enum value isn't present in our current proto definitions, by that
        // the json parsing fails because the enum isn't known as proto representation
        const string Wbc = "WIFI_BROADCAST_CHANGED";

        SessionUpdate update;
        switch (sessionUpdate)
        {
            case FallbackWrapper<SessionUpdate>.Inner inner:
                update = inner.Value;
                break;
            case FallbackWrapper<SessionUpdate>.Fallback fallback:
            {
                var fallbackInner = fallback.Value.GetRawText();
                if (fallbackInner.Contains(Wbc, StringComparison.Ordinal))
                {
                    Log.LogDebug("Received SessionUpdate::{Wbc}", Wbc);
                }
                else
                {
                    Log.LogWarning("SessionUpdate couldn't be parse correctly: {Value}", fallbackInner);
                }

                return;
            }
            default:
                return;
        }

        var reason = update.Reason;

        var session = update.Session;
        if (session is null)
        {
            return;
        }

        string? activeDevice = null;
        if (session.HasHostActiveDeviceId)
        {
            activeDevice = session.HostActiveDeviceId;
            session.ClearHostActiveDeviceId();
        }

        if (activeDevice is not null && activeDevice == _session.DeviceId)
        {
            Log.LogInformation(
                "session update: <{Reason}> for self, current session_id {CurrentSessionId}, new session_id {NewSessionId}",
                reason,
                _session.SessionId,
                session.SessionId);

            if (_session.SessionId != session.SessionId)
            {
                _session.SetSessionId(session.SessionId);
                _connectState.SetSessionId(session.SessionId);
            }
        }
        else
        {
            Log.LogDebug(
                "session update: <{Reason}> from active session host: <{ActiveDevice}>",
                reason,
                RustDebug.Option(activeDevice));
        }

        // this seems to be used for jams or handling the current session_id
        //
        // handling this event was intended to keep the playback when other clients (primarily
        // mobile) connects, otherwise they would steel the current playback when there was no
        // session_id provided on the initial PutStateReason::NEW_DEVICE state update
        //
        // by generating an initial session_id from the get-go we prevent that behavior and
        // currently don't need to handle this event, might still be useful for later "jam" support
    }

    /// <summary>Upstream <c>position</c>.</summary>
    private uint Position() => _playStatus switch
    {
        SpircPlayStatus.LoadingPlay loadingPlay => loadingPlay.PositionMs,
        SpircPlayStatus.LoadingPause loadingPause => loadingPause.PositionMs,
        SpircPlayStatus.Paused paused => paused.PositionMs,
        SpircPlayStatus.Playing playing => unchecked((uint)(NowMs() - playing.NominalStartTime)),
        _ => 0,
    };

    /// <summary>Upstream <c>load_track</c>.</summary>
    private void LoadTrack(bool startPlaying, uint positionMs)
    {
        if (_connectState.Player.Track is null)
        {
            Log.LogDebug("current track is none, stopping playback");
            HandleStop();
            return;
        }

        var currentUri = _connectState.CurrentTrack(t => t.Uri);
        var id = SpotifyUri.FromUri(currentUri);
        _player.Load(id, startPlaying, positionMs);

        _connectState.UpdatePosition(positionMs, NowMs());
        _playStatus = startPlaying
            ? new SpircPlayStatus.LoadingPlay(positionMs)
            : new SpircPlayStatus.LoadingPause(positionMs);
        _connectState.SetStatus(_playStatus);
    }

    /// <summary>Upstream <c>notify</c>: updates status, position and timestamp and sends the state.</summary>
    private async Task NotifyAsync()
    {
        _connectState.SetStatus(_playStatus);

        if (_connectState.IsPlaying)
        {
            _connectState.UpdatePositionInRelation(NowMs());
        }

        _connectState.SetNow((ulong)NowMs());

        await _connectState.SendStateAsync(_session).ConfigureAwait(false);
    }

    /// <summary>Upstream <c>set_volume</c>.</summary>
    internal void SetVolume(ushort volume)
    {
        Log.LogDebug("SpircTask::set_volume({Volume})", volume);

        var oldVolume = _connectState.DeviceInfo.Volume;
        var newVolume = (uint)volume;
        if (oldVolume != newVolume || _mixer.Volume != volume)
        {
            _updateVolume = true;

            _connectState.SetVolume(newVolume);
            _mixer.SetVolume(volume);
            _session.Cache?.SaveVolume(volume);

            if (_connectState.IsActive)
            {
                _player.EmitVolumeChangedEvent(volume);
            }
        }
    }

    /// <summary>The upstream <c>Display</c> of an error.</summary>
    private static string Why(Exception e) => LibrespotException.From(e).Message;
}
