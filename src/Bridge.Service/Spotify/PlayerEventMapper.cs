using System.Threading.Channels;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Session;
using Bridge.Contracts;
using Bridge.Service.Api;
using Bridge.Service.Output;
using Librespot.Metadata.Audio;
using Librespot.Playback;
using ArtistRole = Spotify.Metadata.ArtistWithRole.Types.ArtistRole;

namespace Bridge.Service.Spotify;

/// <summary>
/// Maps librespot's <see cref="PlayerEvent"/>s to actions on the AirPlay output (SPEC-audio.md 8.4, the part owned by
/// bridge-service), and mirrors them into the API's playback view.
/// </summary>
/// <remarks>
/// <para>The table of SPEC-audio.md 8.4 as implemented here (sink calls <c>Start</c>/<c>Stop</c> are handled by
/// <see cref="AirplaySink"/>, which pauses and resumes the group player):</para>
/// <list type="bullet">
/// <item><c>Paused</c>/<c>Playing</c>: state only; the sink's <c>Stop()</c>/<c>Start()</c> flush and re-anchor.</item>
/// <item><c>Seeked</c> while playing: <see cref="IPlaybackOutput.FlushAsync"/>, then the now-playing again with the new
/// position (the player does not re-send progress on flush; SPEC-control.md 5.4 "on seek/resume re-send progress").
/// While paused the queue is already empty: only the now-playing.</item>
/// <item>Track change: the now-playing (text, artwork, progress) for the new track, sent when its position is known (the
/// <c>Playing</c>/<c>Paused</c> event that follows <c>TrackChanged</c>), with the elapsed time corrected by the frames
/// written since <c>TrackChanged</c> (SPEC-audio.md 7: the metadata applies at the write position of the first sample of
/// the track). A natural transition (after <c>EndOfTrack</c>) keeps the continuous timeline; a manual skip (a load while
/// the previous track was still playing) flushes first, "like a seek". librespot's gapless player does not stop the sink
/// around a load, so the flush is the bridge's job. A skip is flushed on <c>Loading</c> when there is one; a load of a
/// track librespot already has (preloaded, which Spirc arranges 30 s before every track end, or the same track again)
/// sends no <c>Loading</c> and goes straight to <c>TrackChanged</c>, so that event flushes when no <c>Loading</c> did.</item>
/// <item><c>VolumeChanged</c>: G of the output (<see cref="IPlaybackOutput.SetSpotifyVolumeAsync"/>), coalesced so a
/// dragged slider sends only the latest value.</item>
/// <item><c>Stopped</c>: state only; sessions stay up while "connected but idle" (8.4: TEARDOWN only on user disconnect).</item>
/// </list>
/// <para>
/// Events are handled one at a time in order (a flush finishes before the now-playing that follows it); the now-playing
/// and the volume run in the background so a slow receiver (the SpeakerA answers DMAP with 500 after about 10 s,
/// SPEC-control.md 5.2) never delays the next event.
/// </para>
/// </remarks>
public sealed class PlayerEventMapper
{
    /// <summary>Frames per second of every Spotify format.</summary>
    public const int SampleRate = 44100;

    /// <summary>librespot's volume scale (0..65535).</summary>
    public const int MaxSpotifyVolume = ushort.MaxValue;

    private readonly IPlaybackOutput _output;
    private readonly IArtworkLoader _artwork;
    private readonly RuntimeState _runtime;
    private readonly EventHub? _hub;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Channel<int> _volume = Channel.CreateBounded<int>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly BackgroundTasks _background;
    private readonly CancellationTokenSource _lifetime = new();

    private CancellationTokenSource? _nowPlayingCts;
    private AudioItem? _track;
    private Task<Artwork?>? _artworkTask;
    private long _trackMark;
    private bool _nowPlayingPending;
    private bool _playing;
    private bool _endOfTrack;
    private bool _loadHandled;
    private uint _positionMs;
    private long _positionAt;
    private int _spotifyVolume;

    /// <summary>Creates the mapper.</summary>
    public PlayerEventMapper(IPlaybackOutput output, IArtworkLoader artwork, RuntimeState runtime, EventHub? hub, ILogger logger, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(artwork);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(logger);
        _output = output;
        _artwork = artwork;
        _runtime = runtime;
        _hub = hub;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _spotifyVolume = runtime.Playback.SpotifyVolume;
        _background = new BackgroundTasks(logger, static ex => IsOutputFailure(ex) || ex is ArgumentException);
    }

    /// <summary>How long a now-playing waits for the cover before it is sent without one.</summary>
    public TimeSpan ArtworkWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The current playback view.</summary>
    public PlaybackDto Playback => BuildPlayback();

    /// <summary>Spotify volume (0..65535) to the 0..100 level of the volume model.</summary>
    public static int ToLevel(ushort volume) => (int)Math.Round(volume * 100.0 / MaxSpotifyVolume, MidpointRounding.AwayFromZero);

    /// <summary>A 0..100 level to the Spotify volume; <see cref="ToLevel"/> of the result gives the level back.</summary>
    public static ushort ToSpotifyVolume(int level) => (ushort)Math.Round(Math.Clamp(level, 0, 100) * MaxSpotifyVolume / 100.0, MidpointRounding.AwayFromZero);

    /// <summary>The text metadata the receivers get for an item (DMAP <c>minm</c>/<c>asar</c>/<c>asal</c>, SPEC-control.md 5.2).</summary>
    public static TrackMetadata ToMetadata(AudioItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        TimeSpan? duration = item.DurationMs > 0 ? TimeSpan.FromMilliseconds(item.DurationMs) : null;
        return item.UniqueFields switch
        {
            UniqueFields.Track track => new TrackMetadata(
                item.Name,
                JoinArtists(track.Artists),
                track.Album,
                duration,
                track.Number > 0 ? (int)track.Number : null,
                track.DiscNumber > 0 ? (int)track.DiscNumber : null),
            UniqueFields.Episode episode => new TrackMetadata(item.Name, episode.ShowName, episode.ShowName, duration),
            UniqueFields.Local local => new TrackMetadata(
                item.Name,
                local.Artists,
                local.Album,
                duration,
                local.Number is > 0 ? (int)local.Number.Value : null,
                local.DiscNumber is > 0 ? (int)local.DiscNumber.Value : null),
            _ => new TrackMetadata(item.Name, null, null, duration),
        };
    }

    /// <summary>
    /// Handles events until <paramref name="events"/> completes (the player shut down) or <paramref name="cancellationToken"/>
    /// fires, then waits for the background sends.
    /// </summary>
    public async Task RunAsync(ChannelReader<PlayerEvent> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        var volumeWorker = RunVolumeAsync(_lifetime.Token);
        try
        {
            await foreach (var playerEvent in events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await HandleAsync(playerEvent, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Handling the player event {Event} failed", playerEvent.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // host stopping
        }
        finally
        {
            _volume.Writer.TryComplete();
            await volumeWorker.ConfigureAwait(false);
            await _lifetime.CancelAsync().ConfigureAwait(false);
            await _background.DrainAsync().ConfigureAwait(false);
            _nowPlayingCts?.Dispose();
            _lifetime.Dispose();
        }
    }

    /// <summary>Handles one event (exposed for tests; <see cref="RunAsync"/> calls it in order).</summary>
    internal async Task HandleAsync(PlayerEvent playerEvent, CancellationToken cancellationToken)
    {
        switch (playerEvent)
        {
            case PlayerEvent.Loading loading:
                // A load while the previous track still plays is a manual skip (or a seek during loading): the queued
                // audio of the old track must not play on (SPEC-audio.md 8.4 "treat it like a seek"). After EndOfTrack the
                // timeline continues without a flush.
                if (IsManualSkip)
                {
                    _logger.LogDebug("Manual skip to {Track}: flushing", loading.TrackId);
                    await FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                _loadHandled = true;
                SetPosition(loading.PositionMs);
                break;

            case PlayerEvent.TrackChanged changed:
                // A load librespot serves from what it already has (the preloaded next track, the same track again) sends
                // no Loading: the skip is flushed here, before the mark, so the new track's frames written so far go with
                // the old track's (dropped like after a seek, SPEC-audio.md 8.4).
                if (!_loadHandled && IsManualSkip)
                {
                    _logger.LogDebug("Manual skip to {Track} without loading: flushing", changed.AudioItem.Uri);
                    await FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                _loadHandled = false;
                _track = changed.AudioItem;
                _endOfTrack = false;
                _trackMark = _output.FramesWritten;
                _nowPlayingPending = true;
                _artworkTask = _background.TrackResult(LoadArtworkQuietlyAsync(changed.AudioItem, _lifetime.Token), "Loading the cover");
                _logger.LogInformation("Track: {Title} ({Uri}, {Duration} ms)", changed.AudioItem.Name, changed.AudioItem.Uri, changed.AudioItem.DurationMs);
                Publish();
                break;

            case PlayerEvent.Playing playing:
                _playing = true;
                SetPosition(playing.PositionMs);
                SendPendingNowPlaying(playing.PositionMs);
                Publish();
                break;

            case PlayerEvent.Paused paused:
                _playing = false;
                SetPosition(paused.PositionMs);
                SendPendingNowPlaying(paused.PositionMs);
                Publish();
                break;

            case PlayerEvent.Seeked seeked:
                var mark = _output.FramesWritten;
                if (_playing)
                {
                    await FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                SetPosition(seeked.PositionMs);
                if (_track is not null)
                {
                    StartNowPlaying(seeked.PositionMs, mark);
                }

                Publish();
                break;

            case PlayerEvent.EndOfTrack:
                _endOfTrack = true;
                break;

            case PlayerEvent.Stopped:
                _playing = false;
                _track = null;
                _nowPlayingPending = false;
                SetPosition(0);
                Publish();
                break;

            case PlayerEvent.PositionCorrection correction:
                SetPosition(correction.PositionMs);
                Publish();
                break;

            case PlayerEvent.PositionChanged position:
                SetPosition(position.PositionMs);
                break;

            case PlayerEvent.Unavailable unavailable:
                _logger.LogWarning("Spotify track {Track} is unavailable", unavailable.TrackId);
                break;

            case PlayerEvent.VolumeChanged volume:
                var level = ToLevel(volume.Volume);
                if (level != _spotifyVolume)
                {
                    _spotifyVolume = level;
                    _volume.Writer.TryWrite(level);
                    Publish();
                }

                break;

            case PlayerEvent.SessionClientChanged client:
                _logger.LogInformation("Spotify is controlled from {Client} ({Brand} {Model})", client.ClientName, client.ClientBrandName, client.ClientModelName);
                break;
        }
    }

    /// <summary>A load now replaces a track that is still being heard (not one that ended on its own).</summary>
    private bool IsManualSkip => _playing && !_endOfTrack && _track is not null;

    private static string? JoinArtists(IEnumerable<Librespot.Metadata.ArtistWithRole> artists)
    {
        var all = artists.ToList();
        var main = all.Where(a => a.Role is ArtistRole.MainArtist or ArtistRole.Unknown).Select(a => a.Name).ToList();
        var names = (main.Count > 0 ? main : all.Select(a => a.Name).ToList()).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToList();
        return names.Count == 0 ? null : string.Join(", ", names);
    }

    private void SetPosition(uint positionMs)
    {
        _positionMs = positionMs;
        _positionAt = _time.GetTimestamp();
    }

    private void SendPendingNowPlaying(uint positionMs)
    {
        if (_nowPlayingPending && _track is not null)
        {
            _nowPlayingPending = false;
            StartNowPlaying(positionMs, _trackMark);
        }
    }

    private void StartNowPlaying(uint positionMs, long mark)
    {
        var item = _track!;
        var artwork = _artworkTask;
        var previous = _nowPlayingCts;
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _nowPlayingCts = cts;
        _background.Track(SendNowPlayingAsync(item, artwork, positionMs, mark, cts.Token), "Sending the now playing");
    }

    private async Task SendNowPlayingAsync(AudioItem item, Task<Artwork?>? artworkTask, uint positionMs, long mark, CancellationToken cancellationToken)
    {
        Artwork? artwork = null;
        if (artworkTask is not null)
        {
            try
            {
                artwork = await artworkTask.WaitAsync(ArtworkWait, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogInformation("Cover for {Track} not loaded within {Wait}; sending the text without it", item.Name, ArtworkWait);
            }
        }

        // SPEC-audio.md 7: the elapsed time is that of the next sample written; samples written since the event (mark)
        // already belong to the track, so their duration is added.
        var written = Math.Max(0, _output.FramesWritten - mark);
        var elapsed = TimeSpan.FromMilliseconds(positionMs) + TimeSpan.FromSeconds(written / (double)SampleRate);
        var nowPlaying = new NowPlaying(ToMetadata(item), artwork?.Data, artwork?.ContentType, elapsed);
        await _output.SetNowPlayingAsync(nowPlaying, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Artwork?> LoadArtworkQuietlyAsync(AudioItem item, CancellationToken cancellationToken)
    {
        try
        {
            return await _artwork.LoadAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
        {
            return null;
        }
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsOutputFailure(ex) && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Flushing the AirPlay output failed: {Error}", ex.Message);
        }
    }

    private async Task RunVolumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var level in _volume.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await _output.SetSpotifyVolumeAsync(level, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsOutputFailure(ex) || ex is ArgumentException or KeyNotFoundException)
                {
                    _logger.LogWarning("Applying the Spotify volume {Level} failed: {Error}", level, ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // shut down
        }
    }

    private static bool IsOutputFailure(Exception ex) =>
        ex is IOException or TimeoutException or RtspStatusException or InvalidOperationException or ObjectDisposedException
            or OperationCanceledException or System.Net.Sockets.SocketException or Microsoft.Data.Sqlite.SqliteException;

    private PlaybackDto BuildPlayback()
    {
        var item = _track;
        var meta = item is null ? null : ToMetadata(item);
        var position = (long)_positionMs;
        if (_playing)
        {
            position += (long)_time.GetElapsedTime(_positionAt).TotalMilliseconds;
        }

        if (item is not null && item.DurationMs > 0)
        {
            position = Math.Min(position, item.DurationMs);
        }

        return new PlaybackDto(_playing, item?.Uri, meta?.Title, meta?.Artist, meta?.Album, position, item?.DurationMs ?? 0, item is null ? null : _artwork.UrlOf(item), _spotifyVolume);
    }

    private void Publish()
    {
        var playback = BuildPlayback();
        _runtime.Playback = playback;
        _hub?.Publish(new BridgeEvent.PlaybackChanged(playback));
    }
}
