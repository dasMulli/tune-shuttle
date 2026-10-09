// Port of connect/src/state.rs.
//
// Upstream splits the `impl ConnectState` blocks over state.rs and the state/*.rs submodules. In C# ConnectState is
// one `partial` class spread over State.cs and State/*.cs (file per upstream file). A partial class must live in a
// single namespace, so the state/*.rs submodules are in `Librespot.Connect` as well (deviation from the
// "directory = sub-namespace" rule of the port plan).
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Librespot.Core;
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;
using Capabilities = Spotify.Connectstate.Capabilities;
using ContextIndex = Spotify.Connectstate.ContextIndex;
using ContextPlayerOptions = Spotify.Connectstate.ContextPlayerOptions;
using DealerRequest = Librespot.Core.Dealer.Request;
using Device = Spotify.Connectstate.Device;
using DeviceInfo = Spotify.Connectstate.DeviceInfo;
using MemberType = Spotify.Connectstate.MemberType;
using PlayerState = Spotify.Connectstate.PlayerState;
using PlayOrigin = Spotify.Connectstate.PlayOrigin;
using ProvidedTrack = Spotify.Connectstate.ProvidedTrack;
using PutStateReason = Spotify.Connectstate.PutStateReason;
using PutStateRequest = Spotify.Connectstate.PutStateRequest;
using Suppressions = Spotify.Connectstate.Suppressions;

namespace Librespot.Connect;

/// <summary>Upstream <c>StateError</c>.</summary>
internal sealed class StateException : LibrespotException
{
    /// <summary>The upstream variants.</summary>
    public enum Reason
    {
        /// <summary>The current track couldn't be resolved from the transfer state (kind FailedPrecondition).</summary>
        CouldNotResolveTrackFromTransfer,
        /// <summary>Context is not available (kind FailedPrecondition).</summary>
        NoContext,
        /// <summary>Could not find a track in the context (kind FailedPrecondition).</summary>
        CanNotFindTrackInContext,
        /// <summary>An action is currently not allowed (kind Unavailable).</summary>
        CurrentlyDisallowed,
        /// <summary>The provided context has no tracks (kind FailedPrecondition).</summary>
        ContextHasNoTracks,
        /// <summary>Playback of local files is not supported (kind Unavailable).</summary>
        UnsupportedLocalPlayback,
        /// <summary>A track uri contains invalid characters (kind FailedPrecondition).</summary>
        InvalidTrackUri,
    }

    /// <summary>Which upstream variant this is.</summary>
    public Reason Cause { get; }

    private StateException(Reason cause, ErrorKind kind, string detail) : base(kind, detail)
    {
        Cause = cause;
    }

    /// <summary>Upstream <c>CouldNotResolveTrackFromTransfer</c>.</summary>
    public static StateException CouldNotResolveTrackFromTransfer() =>
        new(Reason.CouldNotResolveTrackFromTransfer, ErrorKind.FailedPrecondition, "the current track couldn't be resolved from the transfer state");

    /// <summary>Upstream <c>NoContext(ContextType)</c>.</summary>
    public static StateException NoContext(ContextType type) =>
        new(Reason.NoContext, ErrorKind.FailedPrecondition, $"context is not available. type: {type}");

    /// <summary>Upstream <c>CanNotFindTrackInContext(Option&lt;usize&gt;, usize)</c>.</summary>
    public static StateException CanNotFindTrackInContext(int? index, int contextLength) =>
        new(Reason.CanNotFindTrackInContext, ErrorKind.FailedPrecondition, $"could not find track {RustDebug.Option(index)} in context of {contextLength}");

    /// <summary>Upstream <c>CurrentlyDisallowed { action, reason }</c>.</summary>
    public static StateException CurrentlyDisallowed(string action, string reason) =>
        new(Reason.CurrentlyDisallowed, ErrorKind.Unavailable, $"currently {action} is not allowed because {reason}");

    /// <summary>Upstream <c>ContextHasNoTracks</c>.</summary>
    public static StateException ContextHasNoTracks() =>
        new(Reason.ContextHasNoTracks, ErrorKind.FailedPrecondition, "the provided context has no tracks");

    /// <summary>Upstream <c>UnsupportedLocalPlayback</c>.</summary>
    public static StateException UnsupportedLocalPlayback() =>
        new(Reason.UnsupportedLocalPlayback, ErrorKind.Unavailable, "playback of local files is not supported");

    /// <summary>Upstream <c>InvalidTrackUri(Option&lt;String&gt;)</c>.</summary>
    public static StateException InvalidTrackUri(string? uri) =>
        new(Reason.InvalidTrackUri, ErrorKind.FailedPrecondition, $"track uri <{RustDebug.Option(uri)}> contains invalid characters");
}

/// <summary>Configuration of the connect device.</summary>
public sealed record ConnectConfig
{
    /// <summary>The name of the connect device (default: librespot).</summary>
    public string Name { get; init; } = "librespot";

    /// <summary>The icon type of the connect device (default: Speaker).</summary>
    public DeviceType DeviceType { get; init; } = DeviceType.Speaker;

    /// <summary>Displays the device type twice in the UI to show up as a group (default: false).</summary>
    public bool IsGroup { get; init; }

    /// <summary>The volume with which the connect device will be initialized (default: 50%).</summary>
    public ushort InitialVolume { get; init; } = ushort.MaxValue / 2;

    /// <summary>Disables the option to control the volume remotely (default: false).</summary>
    public bool DisableVolume { get; init; }

    /// <summary>Number of incremental steps (default: 64).</summary>
    public ushort VolumeSteps { get; init; } = 64;

    /// <summary>Emit <c>SetQueue</c> player events when the queue changes (default: false).</summary>
    public bool EmitSetQueueEvents { get; init; }
}

/// <summary>
/// The connect state of this device: the entire <c>PutStateRequest</c> that is sent to the remote server, plus the
/// contexts from which the previous and next tracks are filled up (upstream <c>ConnectState</c>).
/// </summary>
/// <remarks>Not thread-safe; owned by the spirc task loop like upstream.</remarks>
internal sealed partial class ConnectState
{
    // these limitations are essential, otherwise to many tracks will overload the web-player
    internal const int SpotifyMaxPrevTracksSize = 10;
    internal const int SpotifyMaxNextTracksSize = 80;

    private static readonly ILogger Log = LibrespotLog.CreateLogger<ConnectState>();

    /// <summary>the entire state that is updated to the remote server</summary>
    private readonly PutStateRequest _request;

    private readonly List<string> _unavailableUri = [];

    private DateTimeOffset? _activeSince;
    private ulong _queueCount;

    // separation is necessary because we could have already loaded
    // the autoplay context but are still playing from the default context
    private ContextType _fillUpContext;

    /// <summary>the context from which we play, is used to top up prev and next tracks</summary>
    private StateContext? _context;

    /// <summary>seed extracted in <see cref="HandleInitialTransfer"/> and used in <see cref="FinishTransfer"/></summary>
    private ShuffleState? _transferShuffle;

    /// <summary>a context to keep track of the autoplay context</summary>
    private StateContext? _autoplayContext;

    /// <summary>
    /// The context type currently played from. To update the active context use <see cref="SetActiveContext"/>
    /// (upstream exposes the field directly; spirc assigns it during transfer).
    /// </summary>
    public ContextType ActiveContext { get; set; }

    /// <summary>The volume adjustment per step when handling individual volume adjustments.</summary>
    public ushort VolumeStepSize { get; set; }

    /// <summary>Upstream <c>ConnectState::new(cfg, session)</c>.</summary>
    public static ConnectState New(ConnectConfig cfg, Session session) =>
        new(cfg, session.DeviceId, session.ClientId, session.SessionId);

    /// <summary>
    /// Builds the state from the values upstream reads from the session (<c>device_id</c>, <c>client_id</c>,
    /// <c>session_id</c>); used by <see cref="New"/> and by tests that have no session.
    /// </summary>
    internal ConnectState(ConnectConfig cfg, string deviceId, string clientId, string sessionId)
    {
        VolumeStepSize = cfg.VolumeSteps == 0 ? (ushort)1024 : (ushort)(ushort.MaxValue / cfg.VolumeSteps);

        var capabilities = new Capabilities
        {
            VolumeSteps = cfg.VolumeSteps,
            DisableVolume = cfg.DisableVolume,

            GaiaEqConnectId = true,
            CanBePlayer = true,
            NeedsFullPlayerState = true,
            IsObservable = true,
            IsControllable = true,
            Hidden = false,

            SupportsGzipPushes = true,
            // todo: enable after logout handling is implemented, see spirc logout_request
            SupportsLogout = false,
            SupportsPlaylistV2 = true,
            SupportsTransferCommand = true,
            SupportsCommandRequest = true,
            SupportsSetOptionsCommand = true,

            IsVoiceEnabled = false,
            RestrictToLocal = false,
            ConnectDisabled = false,
            SupportsRename = false,
            SupportsExternalEpisodes = false,
            SupportsSetBackendMetadata = false,
            SupportsHifi = null,
            // that "AI" dj thingy only available to specific regions/users
            SupportsDj = false,
            SupportsRooms = false,
            // AudioQuality::HIFI is available, further investigation necessary
            SupportedAudioQuality = Spotify.Common.Media.AudioQuality.VeryHigh,

            CommandAcks = true,
        };
        capabilities.SupportedTypes.Add(["audio/episode", "audio/track", "audio/local"]);

        var deviceInfo = new DeviceInfo
        {
            CanPlay = true,
            Volume = cfg.InitialVolume,
            Name = cfg.Name,
            DeviceId = deviceId,
            DeviceType = cfg.DeviceType.ToProto(),
            DeviceSoftwareVersion = LibrespotVersion.SemVer,
            SpircVersion = LibrespotVersion.SpotifySpircVersion,
            ClientId = clientId,
            IsGroup = cfg.IsGroup,
            Capabilities = capabilities,
        };

        _request = new PutStateRequest
        {
            MemberType = MemberType.ConnectState,
            PutStateReason = PutStateReason.PlayerStateChanged,
            Device = new Device
            {
                DeviceInfo = deviceInfo,
                PlayerState = new PlayerState { SessionId = sessionId },
            },
        };

        Reset();
    }

    private void Reset()
    {
        SetActive(false);
        _queueCount = 0;

        // preserve the session_id
        var sessionId = Player.SessionId;

        Device.PlayerState = new PlayerState
        {
            SessionId = sessionId,
            IsSystemInitiated = true,
            PlaybackSpeed = 1.0,
            PlayOrigin = new PlayOrigin(),
            Suppressions = new Suppressions(),
            Options = new ContextPlayerOptions(),
        };
    }

    private Device Device => _request.Device;

    /// <summary>The device info as announced to the remote server.</summary>
    public DeviceInfo DeviceInfo => _request.Device.DeviceInfo;

    /// <summary>The player state as announced to the remote server.</summary>
    public PlayerState Player => _request.Device.PlayerState;

    /// <summary>Whether this device is the active connect device.</summary>
    public bool IsActive => _request.IsActive;

    /// <summary>Returns the <c>is_playing</c> value as perceived by other connect devices (see <see cref="SetStatus"/>).</summary>
    public bool IsPlaying => Player.IsPlaying && !Player.IsPaused;

    /// <summary>Returns the <c>is_paused</c> state value as perceived by other connect devices (see <see cref="SetStatus"/>).</summary>
    public bool IsPause => Player.IsPlaying && Player.IsPaused && Player.IsBuffering;

    /// <summary>Upstream <c>set_volume</c>.</summary>
    public void SetVolume(uint volume) => DeviceInfo.Volume = volume;

    /// <summary>Remembers which command (message id and sender) was handled last.</summary>
    public void SetLastCommand(DealerRequest command)
    {
        _request.LastCommandMessageId = command.MessageId;
        _request.LastCommandSentByDeviceId = command.SentByDeviceId;
    }

    /// <summary>Upstream <c>set_now</c>: the client side timestamp in ms, and the start of the active phase.</summary>
    public void SetNow(ulong now)
    {
        _request.ClientSideTimestamp = now;

        if (_activeSince is { } activeSince)
        {
            var activeSinceMs = activeSince.ToUnixTimeMilliseconds();
            // upstream: `duration_since(UNIX_EPOCH)` fails silently for times before the epoch
            if (activeSinceMs >= 0)
            {
                _request.StartedPlayingAt = (ulong)activeSinceMs;
            }
        }
    }

    /// <summary>Upstream <c>set_active</c>.</summary>
    public void SetActive(bool value)
    {
        if (value)
        {
            if (_request.IsActive)
            {
                return;
            }

            _request.IsActive = true;
            _activeSince = DateTimeOffset.UtcNow;
        }
        else
        {
            _request.IsActive = false;
            _activeSince = null;
        }
    }

    /// <summary>Upstream <c>set_origin</c>.</summary>
    public void SetOrigin(PlayOrigin origin) => Player.PlayOrigin = origin;

    /// <summary>Upstream <c>set_session_id</c>.</summary>
    public void SetSessionId(string sessionId) => Player.SessionId = sessionId;

    /// <summary>Maps the spirc play status onto the playing/paused/buffering flags of the player state.</summary>
    public void SetStatus(SpircPlayStatus status)
    {
        var player = Player;
        player.IsPaused = status is SpircPlayStatus.LoadingPause or SpircPlayStatus.Paused or SpircPlayStatus.Stopped;

        player.PlaybackSpeed = player.IsPaused ? 0.0 : 1.0;

        // desktop and mobile require all 'states' set to true, when we are paused,
        // otherwise the play button (desktop) is grayed out or the preview (mobile) can't be opened
        player.IsBuffering = player.IsPaused || status is SpircPlayStatus.LoadingPause or SpircPlayStatus.LoadingPlay;
        player.IsPlaying = player.IsPaused || status is SpircPlayStatus.LoadingPlay or SpircPlayStatus.Playing;

        Log.LogDebug(
            "updated connect play status playing: {IsPlaying}, paused: {IsPaused}, buffering: {IsBuffering}",
            player.IsPlaying,
            player.IsPaused,
            player.IsBuffering);

        UpdateRestrictions();
    }

    /// <summary>Updates the player index, creating it when absent. The index is 0 based, so the first track is index 0.</summary>
    public void UpdateCurrentIndex(Action<ContextIndex> update)
    {
        if (Player.Index is { } playerIndex)
        {
            update(playerIndex);
        }
        else
        {
            var newIndex = new ContextIndex();
            update(newIndex);
            Player.Index = newIndex;
        }
    }

    /// <summary>Upstream <c>update_position</c>.</summary>
    public void UpdatePosition(uint positionMs, long timestamp)
    {
        Player.PositionAsOfTimestamp = positionMs;
        Player.Timestamp = timestamp;
    }

    /// <summary>Upstream <c>update_duration</c>.</summary>
    public void UpdateDuration(uint duration) => Player.Duration = duration;

    /// <summary>
    /// Recomputes the queue revision: upstream hashes the uris of the next tracks with Rust's
    /// <c>DefaultHasher</c> (SipHash-1-3, zero keys); <see cref="RustDefaultHasher"/> reproduces that exactly.
    /// </summary>
    public void UpdateQueueRevision()
    {
        var hasher = new RustDefaultHasher();
        foreach (var track in NextTracks)
        {
            hasher.WriteStr(track.Uri);
        }

        Player.QueueRevision = hasher.Finish().ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Resets prev and next tracks so that playback continues at <paramref name="newIndex"/> (default 0) of the
    /// active context.
    /// </summary>
    public void ResetPlaybackToPosition(int? newIndex)
    {
        Log.LogDebug(
            "reset_playback with active ctx <{ActiveContext}> fill_up ctx <{FillUpContext}>",
            ActiveContext,
            _fillUpContext);

        var index = newIndex ?? 0;
        UpdateCurrentIndex(i => i.Track = (uint)index);
        UpdateContextIndex(ActiveContext, index + 1);
        _fillUpContext = ActiveContext;

        if (!CurrentTrack(t => t.IsQueue() || IsSkipTrack(t, null)))
        {
            SetCurrentTrack(index);
        }

        ClearPrevTrack();

        if (index > 0)
        {
            var context = GetContext(ActiveContext);

            // upstream: tracks.iter().rev().skip(len - new_index).take(MAX_PREV).rev(), i.e. the (at most) ten
            // tracks right before new_index
            if (index <= context.Tracks.Count)
            {
                var start = Math.Max(0, index - SpotifyMaxPrevTracksSize);
                for (var i = start; i < index; i++)
                {
                    PrevTracks.Add(context.Tracks[i].Clone());
                }
            }

            Log.LogDebug("has {Count} prev tracks", PrevTracks.Count);
        }

        ClearNextTracks();
        FillUpNextTracks();
        UpdateRestrictions();
    }

    private static void MarkAsUnavailableForMatch(ProvidedTrack track, string uri)
    {
        if (track.Uri == uri)
        {
            Log.LogDebug("Marked <{Provider}:{Uri}> as unavailable", track.Provider, track.Uri);
            track.SetProvider(Provider.Unavailable);
        }
    }

    /// <summary>Advances the position by the time elapsed since the last update.</summary>
    public void UpdatePositionInRelation(long timestamp)
    {
        var player = Player;

        var diff = timestamp - player.Timestamp;
        player.PositionAsOfTimestamp += diff;

        if (Log.IsEnabled(LogLevel.Debug))
        {
            string time;
            try
            {
                time = Date.FromTimestampMs(timestamp).AsUtc().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            }
            catch (LibrespotException)
            {
                time = timestamp.ToString(CultureInfo.InvariantCulture);
            }

            var (min, s) = PositionMinSec(player.PositionAsOfTimestamp);
            Log.LogDebug("update position to {Min}:{Sec:00} at {Time}", min, s, time);
        }

        player.Timestamp = timestamp;
    }

    /// <summary>
    /// Splits a position in milliseconds into minutes and seconds for logging, like upstream's
    /// <c>Duration::from_millis(pos as u64).as_secs()</c>: a negative position wraps (two's complement) instead of
    /// throwing, so this never fails (a <see cref="TimeSpan"/> would overflow for the wrapped value).
    /// </summary>
    internal static (ulong Min, ulong Sec) PositionMinSec(long positionMs)
    {
        var sec = unchecked((ulong)positionMs) / 1000;
        return (sec / 60, sec % 60);
    }

    /// <summary>Resets the whole state and tells the server that this device became inactive.</summary>
    public async Task<byte[]> BecameInactiveAsync(Session session, CancellationToken cancellationToken = default)
    {
        Reset();
        ResetContext(new ResetContextKind.Completely());

        return await session.SpClient.PutConnectStateInactiveAsync(false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> SendWithReasonAsync(Session session, PutStateReason reason, CancellationToken cancellationToken)
    {
        var prevReason = _request.PutStateReason;

        _request.PutStateReason = reason;
        try
        {
            return await SendStateAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _request.PutStateReason = prevReason;
        }
    }

    /// <summary>Notifies the remote server about a new device.</summary>
    public Task<byte[]> NotifyNewDeviceAppearedAsync(Session session, CancellationToken cancellationToken = default) =>
        SendWithReasonAsync(session, PutStateReason.NewDevice, cancellationToken);

    /// <summary>Notifies the remote server about a new volume.</summary>
    public Task<byte[]> NotifyVolumeChangedAsync(Session session, CancellationToken cancellationToken = default) =>
        SendWithReasonAsync(session, PutStateReason.VolumeChanged, cancellationToken);

    /// <summary>Sends the connect state for the connect session to the remote server.</summary>
    public Task<byte[]> SendStateAsync(Session session, CancellationToken cancellationToken = default) =>
        session.SpClient.PutConnectStateRequestAsync(_request, cancellationToken);

    /// <summary>The request that <see cref="SendStateAsync"/> sends (for inspection and tests).</summary>
    internal PutStateRequest Request => _request;
}

/// <summary>
/// Rust's <c>std::collections::hash_map::DefaultHasher::new()</c>: SipHash-1-3 with both keys zero. Only the
/// operations upstream uses are provided (<c>str::hash</c> = bytes followed by <c>0xff</c>).
/// </summary>
internal sealed class RustDefaultHasher
{
    private ulong _v0 = 0x736f6d6570736575;
    private ulong _v1 = 0x646f72616e646f6d;
    private ulong _v2 = 0x6c7967656e657261;
    private ulong _v3 = 0x7465646279746573;
    private ulong _tail;
    private int _ntail;
    private ulong _length;

    /// <summary>Upstream <c>impl Hash for str</c>: writes the UTF-8 bytes and a <c>0xff</c> terminator.</summary>
    public void WriteStr(string value)
    {
        // Encode into a stack buffer for typical uris, a pooled array for long strings (avoids a byte[] per uri).
        var max = Encoding.UTF8.GetMaxByteCount(value.Length);
        byte[]? rented = null;
        var buf = max <= 256 ? stackalloc byte[256] : (rented = ArrayPool<byte>.Shared.Rent(max));
        try
        {
            Write(buf[..Encoding.UTF8.GetBytes(value, buf)]);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        Write([0xff]);
    }

    /// <summary>Feeds bytes into the hasher.</summary>
    public void Write(ReadOnlySpan<byte> msg)
    {
        _length += (ulong)msg.Length;

        var needed = 0;
        if (_ntail != 0)
        {
            needed = 8 - _ntail;
            var fill = Math.Min(needed, msg.Length);
            for (var i = 0; i < fill; i++)
            {
                _tail |= (ulong)msg[i] << (8 * (_ntail + i));
            }

            if (msg.Length < needed)
            {
                _ntail += msg.Length;
                return;
            }

            CompressBlock(_tail);
            _ntail = 0;
            _tail = 0;
        }

        var rest = msg[needed..];
        var blocks = rest.Length / 8 * 8;
        for (var i = 0; i < blocks; i += 8)
        {
            CompressBlock(BinaryPrimitives.ReadUInt64LittleEndian(rest[i..]));
        }

        _ntail = rest.Length - blocks;
        for (var i = 0; i < _ntail; i++)
        {
            _tail |= (ulong)rest[blocks + i] << (8 * i);
        }
    }

    /// <summary>The 64-bit hash of everything written so far.</summary>
    public ulong Finish()
    {
        var (v0, v1, v2, v3) = (_v0, _v1, _v2, _v3);
        var b = ((_length & 0xff) << 56) | _tail;

        v3 ^= b;
        Round(ref v0, ref v1, ref v2, ref v3);
        v0 ^= b;

        v2 ^= 0xff;
        Round(ref v0, ref v1, ref v2, ref v3);
        Round(ref v0, ref v1, ref v2, ref v3);
        Round(ref v0, ref v1, ref v2, ref v3);

        return v0 ^ v1 ^ v2 ^ v3;
    }

    private void CompressBlock(ulong m)
    {
        _v3 ^= m;
        Round(ref _v0, ref _v1, ref _v2, ref _v3);
        _v0 ^= m;
    }

    private static void Round(ref ulong v0, ref ulong v1, ref ulong v2, ref ulong v3)
    {
        unchecked
        {
            v0 += v1;
            v1 = ulong.RotateLeft(v1, 13);
            v1 ^= v0;
            v0 = ulong.RotateLeft(v0, 32);
            v2 += v3;
            v3 = ulong.RotateLeft(v3, 16);
            v3 ^= v2;
            v0 += v3;
            v3 = ulong.RotateLeft(v3, 21);
            v3 ^= v0;
            v2 += v1;
            v1 = ulong.RotateLeft(v1, 17);
            v1 ^= v2;
            v2 = ulong.RotateLeft(v2, 32);
        }
    }
}

/// <summary>Renders values the way Rust's <c>{:?}</c> does, so log and error texts match upstream.</summary>
internal static class RustDebug
{
    /// <summary><c>Some("x")</c> or <c>None</c>.</summary>
    public static string Option(string? value) => value is null ? "None" : $"Some(\"{value}\")";

    /// <summary><c>Some(1)</c> or <c>None</c>.</summary>
    public static string Option(int? value) =>
        value is { } v ? $"Some({v.ToString(CultureInfo.InvariantCulture)})" : "None";
}
