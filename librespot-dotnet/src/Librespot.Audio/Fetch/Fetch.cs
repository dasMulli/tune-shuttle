// Port of audio/src/fetch/mod.rs.
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Channels;
using Librespot.Core;
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Librespot.Audio.Fetch;

/// <summary>Upstream <c>AudioFileError</c>.</summary>
public sealed class AudioFileException : LibrespotException
{
    /// <summary>The upstream enum variant.</summary>
    public enum Reason
    {
        /// <summary>Other end of channel disconnected (kind Aborted).</summary>
        Channel,
        /// <summary>Required header not found (kind Unavailable).</summary>
        Header,
        /// <summary>Streamer received no data (kind Unavailable).</summary>
        NoData,
        /// <summary>No output available (kind Aborted).</summary>
        Output,
        /// <summary>Invalid status code (kind FailedPrecondition).</summary>
        StatusCode,
        /// <summary>Wait timeout exceeded (kind DeadlineExceeded).</summary>
        WaitTimeout,
    }

    /// <summary>The upstream variant this exception represents.</summary>
    public Reason Cause { get; }

    /// <summary>Creates the exception; prefer the static factories, which fix kind and text as upstream does.</summary>
    public AudioFileException(Reason cause, ErrorKind kind, string detail, Exception? inner = null) : base(kind, detail, inner)
    {
        Cause = cause;
    }

    /// <summary>Upstream <c>AudioFileError::Channel</c>.</summary>
    public static AudioFileException Channel() => new(Reason.Channel, ErrorKind.Aborted, "other end of channel disconnected");

    /// <summary>Upstream <c>AudioFileError::Header</c>.</summary>
    public static AudioFileException Header() => new(Reason.Header, ErrorKind.Unavailable, "required header not found");

    /// <summary>Upstream <c>AudioFileError::NoData</c>.</summary>
    public static AudioFileException NoData() => new(Reason.NoData, ErrorKind.Unavailable, "streamer received no data");

    /// <summary>Upstream <c>AudioFileError::Output</c>.</summary>
    public static AudioFileException Output() => new(Reason.Output, ErrorKind.Aborted, "no output available");

    /// <summary>Upstream <c>AudioFileError::StatusCode(code)</c>.</summary>
    public static AudioFileException StatusCode(HttpStatusCode code) =>
        new(Reason.StatusCode, ErrorKind.FailedPrecondition, $"invalid status code {StatusDisplay(code)}");

    /// <summary>Upstream <c>AudioFileError::WaitTimeout</c>.</summary>
    public static AudioFileException WaitTimeout() => new(Reason.WaitTimeout, ErrorKind.DeadlineExceeded, "wait timeout exceeded");

    /// <summary>
    /// Renders a status code like hyper's <c>StatusCode</c> <c>Display</c> (<c>"206 Partial Content"</c>). .NET has no
    /// public canonical reason phrase table, so the phrase is derived from the <see cref="HttpStatusCode"/> name.
    /// </summary>
    internal static string StatusDisplay(HttpStatusCode code)
    {
        var name = Enum.IsDefined(code) ? code.ToString() : null;
        if (name is null)
        {
            return $"{(int)code} <unknown status code>";
        }

        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && char.IsLower(name[i - 1]))
            {
                sb.Append(' ');
            }

            sb.Append(name[i]);
        }

        return $"{(int)code} {sb}";
    }
}

/// <summary>Tunables for the CDN fetcher (upstream <c>AudioFetchParams</c>); process-wide via <see cref="Set"/> / <see cref="Get"/>.</summary>
public sealed record AudioFetchParams
{
    /// <summary>The minimum size of a block that is requested from the Spotify servers in one request (power of 2, &gt; 32 kB).</summary>
    public int MinimumDownloadSize { get; init; } = 64 * 1024;

    /// <summary>The minimum network throughput that we expect; with the minimum download size determines the wait timeout.</summary>
    public int MinimumThroughput { get; init; } = 8 * 1024;

    /// <summary>The ping time used for calculations before a ping time was actually measured.</summary>
    public TimeSpan InitialPingTimeEstimate { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Measured ping times above this are capped to avoid run-away block sizes and pre-fetching.</summary>
    public TimeSpan MaximumAssumedPingTime { get; init; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>Before playback starts, this many seconds of data must be present (nominal bitrate).</summary>
    public TimeSpan ReadAheadBeforePlayback { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>While playing back, this many seconds of data ahead of the current read position are requested.</summary>
    public TimeSpan ReadAheadDuringPlayback { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Prefetch when pending bytes &lt; factor * ping time * nominal data rate.</summary>
    public float PrefetchThresholdFactor { get; init; } = 4.0f;

    /// <summary>The time we will wait to obtain status updates on downloading.</summary>
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromSeconds((64 * 1024) / (8 * 1024));

    private static AudioFetchParams? _current;

    /// <summary>Sets the process-wide params; fails (returns false) if already set or read.</summary>
    public static bool Set(AudioFetchParams parameters) => Interlocked.CompareExchange(ref _current, parameters, null) is null;

    /// <summary>The process-wide params (defaults if never set).</summary>
    public static AudioFetchParams Get() => _current ?? Interlocked.CompareExchange(ref _current, new AudioFetchParams(), null) ?? _current!;
}

/// <summary>A command for the download task of one file (upstream <c>StreamLoaderCommand</c>).</summary>
internal abstract record StreamLoaderCommand
{
    private StreamLoaderCommand()
    {
    }

    /// <summary>Signal the stream loader to fetch a range of the file.</summary>
    public sealed record Fetch(Range Range) : StreamLoaderCommand;

    /// <summary>Terminate and don't load any more data.</summary>
    public sealed record Close : StreamLoaderCommand;
}

/// <summary>
/// One CDN range request handed to <see cref="AudioFileFetch.ReceiveDataAsync"/> (upstream <c>StreamingRequest</c>).
/// Upstream holds a lazy <c>ResponseFuture</c> that is only polled once the download slot is acquired; here that is
/// <see cref="Streamer"/>, which starts the HTTP request when invoked.
/// </summary>
internal sealed class StreamingRequest
{
    /// <summary>Starts the request (upstream <c>streamer</c>).</summary>
    public required Func<CancellationToken, Task<HttpResponseMessage>> Streamer { get; init; }

    /// <summary>A response whose request was already made outside of the receive task (the first chunk).</summary>
    public HttpResponseMessage? InitialResponse { get; set; }

    /// <summary>First byte of the requested range.</summary>
    public required long Offset { get; init; }

    /// <summary>Number of bytes requested.</summary>
    public required long Length { get; init; }
}

/// <summary>Upstream <c>StreamLoaderController</c>: steers the download task of one audio file.</summary>
public sealed class StreamLoaderController
{
    private readonly ChannelWriter<StreamLoaderCommand>? _channelTx;
    private readonly AudioFileShared? _streamShared;
    private readonly long _fileSize;

    internal StreamLoaderController(ChannelWriter<StreamLoaderCommand>? channelTx, AudioFileShared? streamShared, long fileSize)
    {
        _channelTx = channelTx;
        _streamShared = streamShared;
        _fileSize = fileSize;
    }

    /// <summary>The size of the file in bytes (upstream <c>len()</c>).</summary>
    public long Length => _fileSize;

    /// <summary>True when the file is empty.</summary>
    public bool IsEmpty => Length == 0;

    /// <summary>True when the bytes <c>[start, start + length)</c> are downloaded.</summary>
    public bool RangeAvailable(long start, long length)
    {
        if (_streamShared is { } shared)
        {
            lock (shared.DownloadStatus)
            {
                return length <= shared.DownloadStatus.Downloaded.ContainedLengthFromValue(start);
            }
        }

        return length <= Length - start;
    }

    /// <summary>True when everything from the current read position to the end of the file is downloaded.</summary>
    public bool RangeToEndAvailable()
    {
        if (_streamShared is { } shared)
        {
            var readPosition = shared.ReadPosition;
            return RangeAvailable(readPosition, Length - readPosition);
        }

        return true;
    }

    /// <summary>The current ping time estimate to the CDN, or <c>null</c> for local/cached files.</summary>
    public TimeSpan? PingTime => _streamShared?.PingTime;

    private void SendStreamLoaderCommand(StreamLoaderCommand command)
    {
        // Ignore the error in case the channel has been closed already.
        // This means that the file was completely downloaded.
        _channelTx?.TryWrite(command);
    }

    /// <summary>Signals the stream loader to fetch a range of the file.</summary>
    public void Fetch(long start, long length) => SendStreamLoaderCommand(new StreamLoaderCommand.Fetch(new Range(start, length)));

    /// <summary>
    /// Signals the stream loader to fetch a range of the file and blocks until it is loaded.
    /// </summary>
    /// <exception cref="AudioFileException">No progress within <see cref="AudioFetchParams.DownloadTimeout"/> (<c>WaitTimeout</c>).</exception>
    public void FetchBlocking(long start, long length)
    {
        var range = ClampToFile(new Range(start, length));

        Fetch(range.Start, range.Length);

        if (_streamShared is not { } shared)
        {
            return;
        }

        var status = shared.DownloadStatus;
        var downloadTimeout = AudioFetchParams.Get().DownloadTimeout;

        lock (status)
        {
            while (range.Length > status.Downloaded.ContainedLengthFromValue(range.Start))
            {
                if (!status.Wait(downloadTimeout))
                {
                    throw AudioFileException.WaitTimeout();
                }

                RefetchIfLost(status, range);
            }
        }
    }

    /// <summary>
    /// Asynchronous variant of <see cref="FetchBlocking"/>: waits without blocking a thread.
    /// </summary>
    /// <exception cref="AudioFileException">No progress within <see cref="AudioFetchParams.DownloadTimeout"/> (<c>WaitTimeout</c>).</exception>
    public async Task FetchBlockingAsync(long start, long length, CancellationToken ct = default)
    {
        var range = ClampToFile(new Range(start, length));

        Fetch(range.Start, range.Length);

        if (_streamShared is not { } shared)
        {
            return;
        }

        var status = shared.DownloadStatus;
        var downloadTimeout = AudioFetchParams.Get().DownloadTimeout;

        Task changed;
        lock (status)
        {
            if (range.Length <= status.Downloaded.ContainedLengthFromValue(range.Start))
            {
                return;
            }

            changed = status.Changed;
        }

        while (true)
        {
            try
            {
                await changed.WaitAsync(downloadTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw AudioFileException.WaitTimeout();
            }

            lock (status)
            {
                RefetchIfLost(status, range);

                if (range.Length <= status.Downloaded.ContainedLengthFromValue(range.Start))
                {
                    return;
                }

                changed = status.Changed;
            }
        }
    }

    // ensure the range is within the file's bounds.
    private Range ClampToFile(Range range)
    {
        if (range.Start >= Length)
        {
            return range with { Length = 0 };
        }

        if (range.End > Length)
        {
            return range with { Length = Length - range.Start };
        }

        return range;
    }

    // Must be called with the download status lock held.
    private void RefetchIfLost(AudioFileDownloadStatus status, Range range)
    {
        if (range.Length > status.Downloaded.Union(status.Requested).ContainedLengthFromValue(range.Start))
        {
            // For some reason, the requested range is neither downloaded nor requested.
            // This could be due to a network error. Request it again.
            Fetch(range.Start, range.Length);
        }
    }

    /// <summary>
    /// Requests <paramref name="requestLength"/> bytes from the current read position and blocks until the first
    /// <paramref name="waitLength"/> bytes of them are available (upstream <c>fetch_next_and_wait</c>).
    /// </summary>
    public void FetchNextAndWait(long requestLength, long waitLength)
    {
        if (_streamShared is { } shared)
        {
            var start = shared.ReadPosition;
            Fetch(start, requestLength);
            FetchBlocking(start, waitLength);
        }
    }

    /// <summary>Asynchronous variant of <see cref="FetchNextAndWait"/>.</summary>
    public Task FetchNextAndWaitAsync(long requestLength, long waitLength, CancellationToken ct = default)
    {
        if (_streamShared is { } shared)
        {
            var start = shared.ReadPosition;
            Fetch(start, requestLength);
            return FetchBlockingAsync(start, waitLength, ct);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The skeleton's original signature of <see cref="FetchNextAndWaitAsync(long, long, CancellationToken)"/>, kept so
    /// the contract is extended, not broken. Upstream takes the read position from the file's own <c>Read</c>/<c>Seek</c>,
    /// so <paramref name="readPosition"/> is ignored.
    /// </summary>
    [Obsolete("The read position comes from the file's own Read/Seek; use FetchNextAndWaitAsync(requestLength, waitLength, ct).")]
    public Task FetchNextAndWaitAsync(long requestLength, long waitLength, long readPosition, CancellationToken ct = default) =>
        FetchNextAndWaitAsync(requestLength, waitLength, ct);

    /// <summary>Optimises the download strategy for random access.</summary>
    public void SetRandomAccessMode() => _streamShared?.SetDownloadStreaming(false);

    /// <summary>Optimises the download strategy for streaming.</summary>
    public void SetStreamMode() => _streamShared?.SetDownloadStreaming(true);

    /// <summary>Terminates stream loading; no more data is loaded for this file.</summary>
    public void Close() => SendStreamLoaderCommand(new StreamLoaderCommand.Close());

    /// <summary>A controller for a fully available local file.</summary>
    public static StreamLoaderController FromLocalFile(long fileSize) => new(null, null, fileSize);
}

/// <summary>The requested and downloaded byte ranges of a streaming file, plus the condition variable signalling changes.</summary>
/// <remarks>
/// Upstream guards this with a <c>Mutex</c> + <c>Condvar</c>. Here the object itself is the monitor: lock it, then use
/// <see cref="Wait"/> (synchronous waiters) or <see cref="Changed"/> (asynchronous waiters); <see cref="NotifyAll"/> wakes both.
/// </remarks>
internal sealed class AudioFileDownloadStatus
{
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Ranges a request is in flight for.</summary>
    public RangeSet Requested { get; } = new();

    /// <summary>Ranges written to the temp file.</summary>
    public RangeSet Downloaded { get; } = new();

    /// <summary>Completes on the next <see cref="NotifyAll"/>. Lock must be held.</summary>
    public Task Changed => _changed.Task;

    /// <summary>Upstream <c>cond.notify_all()</c>. Lock must be held.</summary>
    public void NotifyAll()
    {
        Monitor.PulseAll(this);
        var changed = _changed;
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        changed.TrySetResult();
    }

    /// <summary>Upstream <c>cond.wait_timeout</c>; returns false when timed out. Lock must be held.</summary>
    public bool Wait(TimeSpan timeout) => Monitor.Wait(this, timeout);
}

/// <summary>State shared between the reader, the controllers and the download task (upstream <c>AudioFileShared</c>).</summary>
internal sealed class AudioFileShared
{
    private volatile bool _downloadStreaming;
    private long _pingTimeMs;
    private long _readPosition;
    private long _throughput;

    public AudioFileShared(string cdnUrl, long fileSize, int bytesPerSecond)
    {
        CdnUrl = cdnUrl;
        FileSize = fileSize;
        BytesPerSecond = bytesPerSecond;
    }

    public string CdnUrl { get; }

    public long FileSize { get; }

    public int BytesPerSecond { get; }

    /// <summary>Lock this object before touching it.</summary>
    public AudioFileDownloadStatus DownloadStatus { get; } = new();

    /// <summary>Upstream <c>download_slots: Semaphore::new(1)</c>.</summary>
    public SemaphoreSlim DownloadSlots { get; } = new(1, 1);

    public bool IsDownloadStreaming => _downloadStreaming;

    public void SetDownloadStreaming(bool streaming) => _downloadStreaming = streaming;

    public TimeSpan PingTime
    {
        get
        {
            var pingTimeMs = Volatile.Read(ref _pingTimeMs);
            return pingTimeMs > 0 ? TimeSpan.FromMilliseconds(pingTimeMs) : AudioFetchParams.Get().InitialPingTimeEstimate;
        }
    }

    public void SetPingTime(TimeSpan duration) => Volatile.Write(ref _pingTimeMs, (long)duration.TotalMilliseconds);

    public long Throughput => Volatile.Read(ref _throughput);

    public void SetThroughput(long throughput) => Volatile.Write(ref _throughput, throughput);

    public long ReadPosition => Volatile.Read(ref _readPosition);

    public void SetReadPosition(long position) => Volatile.Write(ref _readPosition, position);
}

/// <summary>
/// An audio file being streamed from the CDN or served from the cache (upstream <c>AudioFile</c> enum:
/// Cached(File) | Streaming(AudioFileStreaming)). Exposes the data as a seekable <see cref="Stream"/>.
/// </summary>
public abstract class AudioFile : Stream
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<AudioFile>();

    private protected AudioFile()
    {
    }

    /// <summary>Upstream <c>AudioFile::open(session, file_id, bytes_per_second)</c>.</summary>
    /// <param name="session">The session (cache, temp dir, CDN client).</param>
    /// <param name="fileId">The file to open.</param>
    /// <param name="bytesPerSecond">The nominal data rate of the file, used for read-ahead.</param>
    /// <param name="ct">Cancels resolving the CDN URL and the first request.</param>
    public static async Task<AudioFile> OpenAsync(Session session, FileId fileId, int bytesPerSecond, CancellationToken ct = default)
    {
        if (session.Cache?.File(fileId) is { } file)
        {
            Log.LogDebug("File {FileId} already in cache", fileId);
            return new CachedAudioFile(file);
        }

        Log.LogDebug("Downloading file {FileId}", fileId);

        var completeTx = new TaskCompletionSource<FileStream>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Upstream `session.spawn(complete_rx.map_ok(...))`; the task catches everything, so it is never unobserved.
        _ = SaveToCacheWhenCompleteAsync(session, fileId, completeTx.Task);

        return await AudioFileStreaming.OpenAsync(session, fileId, completeTx, bytesPerSecond, ct).ConfigureAwait(false);
    }

    private static async Task SaveToCacheWhenCompleteAsync(Session session, FileId fileId, Task<FileStream> complete)
    {
        FileStream file;
        try
        {
            file = await complete.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The download task ended without completing the file (upstream: `complete_tx` dropped).
            return;
        }

        await using (file.ConfigureAwait(false))
        {
            Log.LogDebug("Downloading file {FileId} complete", fileId);

            try
            {
                if (session.Cache is { } cache && cache.FilePath(fileId) is { } cacheId)
                {
                    try
                    {
                        cache.SaveFile(fileId, file);
                        Log.LogDebug("File {FileId} cached to {CacheId}", fileId, cacheId);
                    }
                    catch (Exception e)
                    {
                        Log.LogError("Error caching file {FileId} to {CacheId}: {Error}", fileId, cacheId, LibrespotException.From(e).Message);
                    }
                }
            }
            catch (Exception e)
            {
                // Not upstream (cache.file_path cannot fail there); keeps the task from faulting unobserved.
                Log.LogError("Error caching file {FileId}: {Error}", fileId, LibrespotException.From(e).Message);
            }
        }
    }

    /// <summary>A controller to steer the download of this file (upstream <c>get_stream_loader_controller</c>).</summary>
    public abstract StreamLoaderController GetStreamLoaderController();

    /// <summary>True when the file is served from the cache.</summary>
    public abstract bool IsCached { get; }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => true;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public abstract override int Read(Span<byte> buffer);

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Upstream <c>AudioFile::Cached(fs::File)</c>: a file served from the audio cache.</summary>
internal sealed class CachedAudioFile : AudioFile
{
    private readonly FileStream _file;

    public CachedAudioFile(FileStream file)
    {
        _file = file;
    }

    public override bool IsCached => true;

    public override long Length => _file.Length;

    public override long Position
    {
        get => _file.Position;
        set => _file.Position = value;
    }

    public override StreamLoaderController GetStreamLoaderController() => new(null, null, _file.Length);

    public override int Read(Span<byte> buffer) => _file.Read(buffer);

    public override long Seek(long offset, SeekOrigin origin) => _file.Seek(offset, origin);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _file.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Upstream <c>AudioFileStreaming</c>: a file being downloaded from the CDN into a temp file by
/// <see cref="AudioFileFetch"/>; reads block until the requested bytes have arrived.
/// </summary>
internal sealed class AudioFileStreaming : AudioFile
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<AudioFileStreaming>();

    private readonly FileStream _readFile;
    private readonly ChannelWriter<StreamLoaderCommand> _streamLoaderCommandTx;
    private readonly AudioFileShared _shared;
    private long _position;

    private AudioFileStreaming(FileStream readFile, ChannelWriter<StreamLoaderCommand> streamLoaderCommandTx, AudioFileShared shared)
    {
        _readFile = readFile;
        _streamLoaderCommandTx = streamLoaderCommandTx;
        _shared = shared;
    }

    /// <summary>Upstream <c>AudioFileStreaming::open</c>: resolves the CDN URLs, then opens the file from them.</summary>
    public static async Task<AudioFileStreaming> OpenAsync(
        Session session,
        FileId fileId,
        TaskCompletionSource<FileStream> completeTx,
        int bytesPerSecond,
        CancellationToken ct)
    {
        IReadOnlyList<string> urls;
        SpClient spClient;
        try
        {
            var cdnUrl = await new CdnUrl(fileId).ResolveAudioAsync(session, ct).ConfigureAwait(false);
            urls = cdnUrl.TryGetUrls();
            spClient = session.SpClient;
        }
        catch
        {
            completeTx.TrySetCanceled();
            throw;
        }

        return await OpenAsync(urls, spClient.StreamFromCdnAsync, session.Config.TmpDir, completeTx, bytesPerSecond, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the file from the first of <paramref name="urls"/> that answers 206, and starts the download task.
    /// <paramref name="fetcher"/> is <c>SpClient.StreamFromCdnAsync</c> in production and a fake in tests.
    /// <paramref name="completeTx"/> receives the complete temp file (rewound) once everything is downloaded, or is
    /// cancelled when the download ends otherwise.
    /// </summary>
    internal static async Task<AudioFileStreaming> OpenAsync(
        IReadOnlyList<string> urls,
        Func<string, long, long, CancellationToken, Task<HttpResponseMessage>> fetcher,
        string tmpDir,
        TaskCompletionSource<FileStream> completeTx,
        int bytesPerSecond,
        CancellationToken ct)
    {
        var handedOff = false;
        try
        {
            var result = await OpenCoreAsync(urls, fetcher, tmpDir, completeTx, bytesPerSecond, ct).ConfigureAwait(false);
            handedOff = true;
            return result;
        }
        finally
        {
            if (!handedOff)
            {
                // Upstream drops `complete_tx`, so the cache task never runs.
                completeTx.TrySetCanceled();
            }
        }
    }

    private static async Task<AudioFileStreaming> OpenCoreAsync(
        IReadOnlyList<string> urls,
        Func<string, long, long, CancellationToken, Task<HttpResponseMessage>> fetcher,
        string tmpDir,
        TaskCompletionSource<FileStream> completeTx,
        int bytesPerSecond,
        CancellationToken ct)
    {
        var minimumDownloadSize = AudioFetchParams.Get().MinimumDownloadSize;

        HttpResponseMessage? response = null;
        string? url = null;
        foreach (var candidate in urls)
        {
            // When the audio file is really small, this `download_size` may turn out to be
            // larger than the audio file we're going to stream later on. This is OK; requesting
            // `Content-Range` > `Content-Length` will return the complete file with status code
            // 206 Partial Content.
            //
            // Get the first chunk with the headers to get the file size. The response body is then
            // further processed in `AudioFileFetch`.
            //
            // Deviation: upstream builds the request synchronously (`stream_from_cdn(..)?` aborts the whole open on
            // a malformed URL); here building and sending are one async call, so every failure tries the next URL.
            HttpResponseMessage r;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    r = await fetcher(candidate, 0, minimumDownloadSize, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw AudioFileException.WaitTimeout();
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.LogWarning("Fetching {Url} failed with error {Error}, trying next", candidate, LibrespotException.From(e).Message);
                continue;
            }

            if (r.StatusCode == HttpStatusCode.PartialContent)
            {
                response = r;
                url = candidate;
                break;
            }

            Log.LogWarning(
                "Fetching {Url} returned {Status} (expected 206 Partial Content), trying next",
                candidate,
                AudioFileException.StatusDisplay(r.StatusCode));
            r.Dispose();
        }

        if (response is null || url is null)
        {
            throw LibrespotException.Unavailable($"{urls.Count} URLs failed, none left to try");
        }

        Log.LogTrace("Streaming from {Url}", url);

        FileStream? writeFile = null;
        FileStream? readFile = null;
        try
        {
            var (upperBound, fileSize) = ParseContentRange(response);

            var initialRequest = new StreamingRequest
            {
                Streamer = _ => throw new InvalidOperationException("the initial request has exactly one response"),
                InitialResponse = response,
                Offset = 0,
                Length = upperBound + 1,
            };

            var shared = new AudioFileShared(url, fileSize, bytesPerSecond);

            // Upstream `NamedTempFile::new_in(tmp_dir)` + `reopen()`: one handle for the download task, one for reading.
            // Both are unbuffered so the reader never sees stale bytes of a range that was not yet downloaded.
            var path = Path.Combine(tmpDir, ".tmp" + Path.GetRandomFileName());
            writeFile = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 0,
                FileOptions.DeleteOnClose);
            writeFile.SetLength(fileSize);

            readFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);

            var commands = Channel.CreateUnbounded<StreamLoaderCommand>(new UnboundedChannelOptions { SingleReader = true });

            AudioFileFetch.Spawn(() => AudioFileFetch.RunAsync(fetcher, shared, initialRequest, writeFile, commands, completeTx));

            return new AudioFileStreaming(readFile, commands.Writer, shared);
        }
        catch (Exception e)
        {
            response.Dispose();
            readFile?.Dispose();
            writeFile?.Dispose();
            if (e is LibrespotException)
            {
                throw;
            }

            throw LibrespotException.From(e);
        }
    }

    // Upstream fetch/mod.rs: "bytes a-b/size" -> (b, size).
    private static (long UpperBound, long FileSize) ParseContentRange(HttpResponseMessage response)
    {
        string? value = null;
        if (response.Content.Headers.TryGetValues("Content-Range", out var contentValues))
        {
            value = contentValues.FirstOrDefault();
        }
        else if (response.Headers.TryGetValues("Content-Range", out var values))
        {
            value = values.FirstOrDefault();
        }

        if (value is null)
        {
            throw AudioFileException.Header();
        }

        var hyphenIndex = Math.Max(value.IndexOf('-'), 0);
        var slashIndex = Math.Max(value.IndexOf('/'), 0);
        try
        {
            var upperBound = long.Parse(value.AsSpan()[(hyphenIndex + 1)..slashIndex], NumberStyles.None, CultureInfo.InvariantCulture);
            var fileSize = long.Parse(value.AsSpan()[(slashIndex + 1)..], NumberStyles.None, CultureInfo.InvariantCulture);
            return (upperBound, fileSize);
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            // ParseIntError -> FailedPrecondition
            throw LibrespotException.FailedPrecondition($"invalid Content-Range \"{value}\": {e.Message}", e);
        }
    }

    public override bool IsCached => false;

    public override long Length => _shared.FileSize;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override StreamLoaderController GetStreamLoaderController() => new(_streamLoaderCommandTx, _shared, _shared.FileSize);

    /// <summary>Reads at the current position, requesting missing data and blocking until the first byte is there.</summary>
    /// <exception cref="IOException">The download task is gone, or no data arrived within the download timeout.</exception>
    public override int Read(Span<byte> buffer)
    {
        var offset = _position;

        if (offset >= _shared.FileSize)
        {
            return 0;
        }

        var length = Math.Min(buffer.Length, _shared.FileSize - offset);
        if (length == 0)
        {
            return 0;
        }

        var parameters = AudioFetchParams.Get();
        long lengthToRequest;
        if (_shared.IsDownloadStreaming)
        {
            lengthToRequest = length + (long)((float)parameters.ReadAheadDuringPlayback.TotalSeconds * _shared.BytesPerSecond);

            // Due to the read-ahead stuff, we potentially request more than the actual request demanded.
            lengthToRequest = Math.Min(lengthToRequest, _shared.FileSize - offset);
        }
        else
        {
            lengthToRequest = length;
        }

        var rangesToRequest = new RangeSet();
        rangesToRequest.AddRange(new Range(offset, lengthToRequest));

        var status = _shared.DownloadStatus;
        long availableLength;
        lock (status)
        {
            rangesToRequest.SubtractRangeSet(status.Downloaded);
            rangesToRequest.SubtractRangeSet(status.Requested);

            foreach (var range in rangesToRequest.Ranges)
            {
                if (!_streamLoaderCommandTx.TryWrite(new StreamLoaderCommand.Fetch(range)))
                {
                    // io::ErrorKind::BrokenPipe
                    throw new IOException("channel closed", AudioFileException.Channel());
                }
            }

            var downloadTimeout = parameters.DownloadTimeout;
            while (!status.Downloaded.Contains(offset))
            {
                if (!status.Wait(downloadTimeout))
                {
                    // io::ErrorKind::TimedOut wrapping Error::deadline_exceeded(AudioFileError::WaitTimeout)
                    var error = AudioFileException.WaitTimeout();
                    throw new IOException(error.Message, error);
                }
            }

            availableLength = status.Downloaded.ContainedLengthFromValue(offset);
        }

        _position = _readFile.Seek(offset, SeekOrigin.Begin);
        var readLen = (int)Math.Min(length, availableLength);
        readLen = _readFile.Read(buffer[..readLen]);

        _position += readLen;
        _shared.SetReadPosition(_position);

        return readLen;
    }

    /// <summary>Seeks; switches the downloader to random access while seeking into a part that is not downloaded yet.</summary>
    public override long Seek(long offset, SeekOrigin origin)
    {
        // If we are already at this position, we don't need to switch download mode.
        // These checks and locks are less expensive than interrupting streaming.
        var currentPosition = _position;
        var requestedPos = origin switch
        {
            SeekOrigin.Begin => offset,
            // Upstream computes `file_size - pos - 1` for `SeekFrom::End(pos)`; kept as is. It only affects the
            // "already there" / "already downloaded" shortcuts, the actual seek below is exact.
            SeekOrigin.End => _shared.FileSize - offset - 1,
            SeekOrigin.Current => currentPosition + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (requestedPos == currentPosition)
        {
            return currentPosition;
        }

        // Again if we have already downloaded this part.
        bool available;
        lock (_shared.DownloadStatus)
        {
            available = _shared.DownloadStatus.Downloaded.Contains(requestedPos);
        }

        var wasStreaming = false;
        if (!available)
        {
            // Ensure random access mode if we need to download this part.
            // Checking whether we are streaming now is a micro-optimization
            // to save an atomic load.
            wasStreaming = _shared.IsDownloadStreaming;
            if (wasStreaming)
            {
                _shared.SetDownloadStreaming(false);
            }
        }

        _position = _readFile.Seek(offset, origin);
        _shared.SetReadPosition(_position);

        if (!available && wasStreaming)
        {
            _shared.SetDownloadStreaming(true);
        }

        return _position;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Upstream: dropping the file drops its command sender; the download task stops once every
            // `StreamLoaderController` clone is gone as well. A ChannelWriter is not reference counted, so disposing
            // the file completes the channel and stops the download task right away.
            _streamLoaderCommandTx.TryComplete();
            _readFile.Dispose();
        }

        base.Dispose(disposing);
    }
}
