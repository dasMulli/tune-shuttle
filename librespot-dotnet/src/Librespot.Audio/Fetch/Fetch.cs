// Port of audio/src/fetch/mod.rs (public surface). fetch/receive.rs (the download task) and range_set.rs
// belong to the same package.
// TODO(port: audio-fetch): AudioFileStreaming (temp file + shared RangeSet + download task), StreamLoaderController
// commands over a Channel<StreamLoaderCommand>, cached-file path, Read/Seek blocking on ranges.
// Session members used (all exist as contracts): Session.Cache (File / SaveFile), Session.Config.TmpDir,
// CdnUrl.ResolveAudioAsync, SpClient.StreamFromCdnAsync (HttpResponseMessage: status, Content-Range, body),
// HttpClient.GetRetryAfter, Librespot.Core.Crypto.AesCtr (done). NOT used: Session.AudioKey (the player requests
// the key) and Session.Channel (dead code upstream).
using Librespot.Core;

namespace Librespot.Audio.Fetch;

/// <summary>Upstream <c>AudioFileError</c>.</summary>
public sealed class AudioFileException : LibrespotException
{
    public enum Reason
    {
        /// <summary>Other end of channel disconnected (kind Aborted).</summary>
        Channel,
        /// <summary>Required header not found (kind Unavailable).</summary>
        Header,
        /// <summary>Streamer received no data (kind Unavailable).</summary>
        NoData,
        /// <summary>Could not complete CDN request (kind Unavailable).</summary>
        Output,
        /// <summary>Response status code mismatch (kind FailedPrecondition).</summary>
        StatusCode,
        /// <summary>Wait timeout exceeded (kind DeadlineExceeded).</summary>
        WaitTimeout,
    }

    public Reason Cause { get; }

    public AudioFileException(Reason cause, ErrorKind kind, string detail, Exception? inner = null) : base(kind, detail, inner)
    {
        Cause = cause;
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

/// <summary>Upstream <c>StreamLoaderController</c>: steers the download task of one audio file.</summary>
public sealed class StreamLoaderController
{
    internal StreamLoaderController()
    {
    }

    public long Length => throw new NotImplementedException("TODO(port: audio-fetch)");
    public bool IsEmpty => Length == 0;
    public bool RangeAvailable(long start, long length) => throw new NotImplementedException("TODO(port: audio-fetch)");
    public bool RangeToEndAvailable() => throw new NotImplementedException("TODO(port: audio-fetch)");
    public TimeSpan? PingTime => throw new NotImplementedException("TODO(port: audio-fetch)");
    public void Fetch(long start, long length) => throw new NotImplementedException("TODO(port: audio-fetch)");
    public void FetchBlocking(long start, long length) => throw new NotImplementedException("TODO(port: audio-fetch)");
    public Task FetchNextAndWaitAsync(long requestLength, long waitLength, long readPosition, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: audio-fetch)");
    public void SetRandomAccessMode() => throw new NotImplementedException("TODO(port: audio-fetch)");
    public void SetStreamMode() => throw new NotImplementedException("TODO(port: audio-fetch)");
    public void Close() => throw new NotImplementedException("TODO(port: audio-fetch)");

    /// <summary>A controller for a fully available local file.</summary>
    public static StreamLoaderController FromLocalFile(long fileSize) => throw new NotImplementedException("TODO(port: audio-fetch)");
}

/// <summary>
/// An audio file being streamed from the CDN or served from the cache (upstream <c>AudioFile</c> enum:
/// Cached(File) | Streaming(AudioFileStreaming)). Exposes the data as a seekable <see cref="Stream"/>.
/// </summary>
public abstract class AudioFile : Stream
{
    /// <summary>Upstream <c>AudioFile::open(session, file_id, bytes_per_second)</c>.</summary>
    public static Task<AudioFile> OpenAsync(Session session, FileId fileId, int bytesPerSecond, CancellationToken ct = default) =>
        throw new NotImplementedException("TODO(port: audio-fetch)");

    public abstract StreamLoaderController GetStreamLoaderController();

    public abstract bool IsCached { get; }
}
