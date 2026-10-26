using System.Threading.Channels;
using Airplay2.Receiver.Playout;
using Airplay2.Sender.Session;
using Microsoft.Extensions.Logging;

namespace Airplay2.Receiver;

/// <summary>
/// The now-playing rule of SPEC-receiver.md 5.5: DMAP text, artwork and progress that carry one <c>rtptime</c> key become one
/// <see cref="IReceiverAudioOutput.SetNowPlayingAsync"/> call, made once all three arrived or <c>window</c> (500 ms) after the
/// first, whichever comes first. A later item with the same key (a progress keep-alive, a seek) updates the remembered
/// track and is sent again after the window; an item with a new key sends what is still pending for the old key at once and
/// starts a new track. <c>elapsed</c> is computed when the call is made, from the next frame the playout buffer will release
/// (<c>(next - start) / 44100</c>), or from the sender's <c>(current - start) / 44100</c> when the buffer is empty. The calls
/// run one at a time on a background loop, so the control plane's handlers never wait for the output.
/// </summary>
internal sealed class NowPlayingCoalescer : IAsyncDisposable
{
    private readonly IReceiverAudioOutput _output;
    private readonly Func<uint?> _nextTimestamp;
    private readonly TimeSpan _window;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly Channel<Update> _updates = Channel.CreateUnbounded<Update>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private Track? _track;
    private CancellationTokenSource? _timer;
    private int _sent;

    /// <summary>Creates the coalescer and starts its send loop (SPEC-receiver.md 5.5).</summary>
    /// <param name="output">Where the now-playing calls go.</param>
    /// <param name="nextTimestamp">The RTP timestamp of the next frame the current stream's buffer releases, or null when empty or without a stream.</param>
    /// <param name="window">How long to wait for the rest of a track's items after the first (500 ms).</param>
    /// <param name="logger">Logger.</param>
    public NowPlayingCoalescer(IReceiverAudioOutput output, Func<uint?> nextTimestamp, TimeSpan window, ILogger logger)
    {
        _output = output;
        _nextTimestamp = nextTimestamp;
        _window = window;
        _logger = logger;
        _loop = Task.Run(SendLoopAsync);
    }

    /// <summary>The number of now-playing calls made (tests).</summary>
    public int Sent => Volatile.Read(ref _sent);

    /// <summary>DMAP text with its <c>rtptime</c> key (SPEC-receiver.md 4.8).</summary>
    public void OnMetadata(DmapItem item, uint key) => Add(key, t => t.Item = item);

    /// <summary>Artwork with its <c>rtptime</c> key (SPEC-receiver.md 4.8).</summary>
    public void OnArtwork(ReadOnlyMemory<byte> bytes, string contentType, uint key) => Add(key, t => (t.Artwork, t.ArtworkType) = (bytes, contentType));

    /// <summary>Progress; its key is the request's <c>rtptime</c>, else its start (SPEC-control.md 5.4: the sender sends <c>rtptime=start</c>).</summary>
    public void OnProgress(ProgressInfo progress) => Add(progress.RtpTime ?? progress.Start, t => t.Progress = progress);

    /// <summary>Forgets the track and drops a pending call: the stream or session ended (SPEC-receiver.md 4.9).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            CancelTimerLocked();
            _track = null;
        }
    }

    /// <summary>Stops the send loop; pending calls are dropped.</summary>
    public async ValueTask DisposeAsync()
    {
        Reset();
        _updates.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private void Add(uint key, Action<Track> apply)
    {
        lock (_lock)
        {
            if (_track is { } current && current.Key != key)
            {
                if (current.Pending)
                {
                    SendLocked(current);
                }

                _track = null;
            }

            var track = _track ??= new Track(key);
            apply(track);
            track.Pending = true;
            if (track.Complete)
            {
                SendLocked(track);
            }
            else if (_timer is null)
            {
                var timer = new CancellationTokenSource();
                _timer = timer;
                _ = FireAfterWindowAsync(track, timer);
            }
        }
    }

    private async Task FireAfterWindowAsync(Track track, CancellationTokenSource timer)
    {
        try
        {
            await Task.Delay(_window, timer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_lock)
        {
            if (!ReferenceEquals(_timer, timer))
            {
                return;
            }

            _timer = null;
            timer.Dispose();
            if (ReferenceEquals(_track, track) && track.Pending)
            {
                SendLocked(track);
            }
        }
    }

    private void SendLocked(Track track)
    {
        CancelTimerLocked();
        track.Pending = false;
        _updates.Writer.TryWrite(new Update(track.Key, track.Item, track.Artwork, track.ArtworkType, track.Progress));
    }

    private void CancelTimerLocked()
    {
        if (_timer is { } timer)
        {
            _timer = null;
            timer.Cancel();
            timer.Dispose();
        }
    }

    private async Task SendLoopAsync()
    {
        await foreach (var update in _updates.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
        {
            var start = update.Progress?.Start ?? update.Key;
            var elapsed = Elapsed(start, _nextTimestamp(), update.Progress);
            var metadata = update.Item?.ToTrackMetadata() ?? new TrackMetadata(null, null, null, null);
            if (metadata.Duration is null && update.Progress is { } progress && progress.End != progress.Start)
            {
                metadata = metadata with { Duration = ReceiverParameters.Duration(progress) };
            }

            try
            {
                await _output.SetNowPlayingAsync(metadata, update.Artwork, update.ArtworkType, elapsed, _stop.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _sent);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Setting the now-playing track '{Title}' on the output failed", metadata.Title);
            }
        }
    }

    /// <summary>
    /// SPEC-receiver.md 5.5: <c>(next - start) / 44100</c> with the next frame the buffer releases; the sender's
    /// <c>(current - start) / 44100</c> when the buffer is empty; zero without either (and never negative).
    /// </summary>
    internal static TimeSpan Elapsed(uint start, uint? nextTimestamp, ProgressInfo? progress)
    {
        int frames;
        if (nextTimestamp is { } next)
        {
            frames = unchecked((int)(next - start));
        }
        else if (progress is { } p)
        {
            frames = unchecked((int)(p.Current - p.Start));
        }
        else
        {
            return TimeSpan.Zero;
        }

        return frames <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(frames / (double)ReceiverParameters.SampleRate);
    }

    private sealed record Update(uint Key, DmapItem? Item, ReadOnlyMemory<byte>? Artwork, string? ArtworkType, ProgressInfo? Progress);

    private sealed class Track(uint key)
    {
        public uint Key { get; } = key;

        public DmapItem? Item { get; set; }

        public ReadOnlyMemory<byte>? Artwork { get; set; }

        public string? ArtworkType { get; set; }

        public ProgressInfo? Progress { get; set; }

        public bool Pending { get; set; }

        public bool Complete => Item is not null && Artwork is not null && Progress is not null;
    }
}
