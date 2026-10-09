// Port of audio/src/fetch/receive.rs.
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading.Channels;
using Librespot.Core;
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Librespot.Audio.Fetch;

/// <summary>A chunk of the file received from the CDN (upstream <c>PartialFileData</c>).</summary>
internal sealed record PartialFileData(long Offset, byte[] Data);

/// <summary>Messages from the receive tasks to the fetch loop (upstream <c>ReceivedData</c>).</summary>
internal abstract record ReceivedData
{
    private ReceivedData()
    {
    }

    /// <summary>Measured throughput of one request in bytes per second.</summary>
    public sealed record Throughput(long BytesPerSecond) : ReceivedData;

    /// <summary>Measured time to first byte of one request.</summary>
    public sealed record ResponseTime(TimeSpan Duration) : ReceivedData;

    /// <summary>Downloaded file data.</summary>
    public sealed record Data(PartialFileData Partial) : ReceivedData;

    /// <summary>
    /// The range request could not be built or sent (local rate limiter refusal, invalid URL). Not upstream: there
    /// <c>download_range</c> builds the request synchronously and <c>stream_from_cdn(..)?</c> ends the fetch task; here the
    /// request is built inside the receive task, which forwards the error so the fetch loop ends the same way.
    /// </summary>
    public sealed record Fatal(LibrespotException Error) : ReceivedData;
}

/// <summary>
/// The download task of one streaming file (upstream <c>AudioFileFetch</c> + <c>audio_file_fetch</c> +
/// <c>receive_data</c>): turns <see cref="StreamLoaderCommand"/>s into CDN range requests, writes the received data to
/// the temp file, keeps the ping-time and throughput estimates and pre-fetches while streaming.
/// </summary>
internal sealed class AudioFileFetch
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<AudioFileFetch>();

    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private readonly Func<string, long, long, CancellationToken, Task<HttpResponseMessage>> _fetcher;
    private readonly AudioFileShared _shared;
    private readonly ChannelWriter<ReceivedData> _fileDataTx;
    private readonly List<TimeSpan> _networkResponseTimes = new(3);
    private readonly AudioFetchParams _params;
    private readonly CancellationToken _shutdown;
    private FileStream? _output;
    private TaskCompletionSource<FileStream>? _completeTx;

    private AudioFileFetch(
        Func<string, long, long, CancellationToken, Task<HttpResponseMessage>> fetcher,
        AudioFileShared shared,
        FileStream output,
        ChannelWriter<ReceivedData> fileDataTx,
        TaskCompletionSource<FileStream> completeTx,
        AudioFetchParams parameters,
        CancellationToken shutdown)
    {
        _fetcher = fetcher;
        _shared = shared;
        _output = output;
        _fileDataTx = fileDataTx;
        _completeTx = completeTx;
        _params = parameters;
        _shutdown = shutdown;
    }

    /// <summary>
    /// Upstream <c>session.spawn(fut)</c>: runs <paramref name="task"/> in the background. Upstream drops the task's
    /// result; here a failure is logged at debug level so it is observed.
    /// </summary>
    internal static void Spawn(Func<Task> task) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await task().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.LogDebug("Audio fetch task ended with error: {Error}", LibrespotException.From(e).Message);
            }
        });

    /// <summary>
    /// Upstream <c>receive_data</c>: performs one range request once a download slot is free and forwards the
    /// response time, data and throughput to the fetch loop.
    /// </summary>
    /// <remarks>
    /// Deviation: upstream keeps downloading after the fetch loop has ended and only stops when sending to it fails.
    /// Here the loop cancels <paramref name="shutdown"/> when it ends, which stops such requests early (the data would be
    /// discarded either way) and returns silently, as a failed send does upstream.
    /// A failure to build or send the request (see <see cref="IsRequestBuildError"/>) is forwarded to the loop as
    /// <see cref="ReceivedData.Fatal"/>, which ends it as upstream's <c>download_range(..)?</c> does.
    /// </remarks>
    internal static async Task ReceiveDataAsync(
        AudioFileShared shared,
        ChannelWriter<ReceivedData> fileDataTx,
        StreamingRequest request,
        CancellationToken shutdown)
    {
        var offset = request.Offset;
        long actualLength = 0;

        try
        {
            await shared.DownloadSlots.WaitAsync(shutdown).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        LibrespotException? result = null;
        LibrespotException? fatal = null;
        try
        {
            var requestTime = Stopwatch.GetTimestamp();
            var measurePingTime = true;
            var measureThroughput = true;

            // Upstream loops over the response stream, which yields exactly one response (or the initial response
            // taken from the request); the loop then ends on the stream's `None`, where the length is checked.
            HttpResponseMessage? response = null;
            try
            {
                if (request.InitialResponse is { } initial)
                {
                    // the request was already made outside of this function
                    request.InitialResponse = null;
                    measurePingTime = false;
                    measureThroughput = false;

                    response = initial;
                }
                else
                {
                    try
                    {
                        response = await request.Streamer(shutdown).ConfigureAwait(false);
                    }
                    catch (LibrespotException e) when (IsRequestBuildError(e))
                    {
                        // upstream: `stream_from_cdn(..)?` in `download_range`, which ends the fetch task
                        fatal = e;
                    }
                }

                if (response is not null)
                {
                    if (measurePingTime)
                    {
                        var duration = Stopwatch.GetElapsedTime(requestTime);

                        // may be zero if we are handling an initial response
                        if ((long)duration.TotalMilliseconds > 0)
                        {
                            if (!fileDataTx.TryWrite(new ReceivedData.ResponseTime(duration)))
                            {
                                return;
                            }

                            measurePingTime = false;
                        }
                    }

                    var code = response.StatusCode;
                    if (code != HttpStatusCode.PartialContent)
                    {
                        if (code == HttpStatusCode.TooManyRequests && Librespot.Core.HttpClient.GetRetryAfter(response.Headers) is { } retryAfter)
                        {
                            Log.LogWarning("Rate limiting, retrying in {Seconds} seconds...", (long)retryAfter.TotalSeconds);

                            // sleeping here means we hold onto this streamer "slot"
                            // (we don't decrease the number of open requests)
                            await Task.Delay(retryAfter, shutdown).ConfigureAwait(false);
                        }

                        result = AudioFileException.StatusCode(code);
                    }
                    else
                    {
                        var data = await response.Content.ReadAsByteArrayAsync(shutdown).ConfigureAwait(false);

                        var dataSize = data.Length;
                        if (!fileDataTx.TryWrite(new ReceivedData.Data(new PartialFileData(offset, data))))
                        {
                            return;
                        }

                        actualLength += dataSize;
                        offset += dataSize;

                        if (actualLength != request.Length)
                        {
                            result = LibrespotException.DataLoss($"did not expect body to contain {actualLength} bytes");
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                result = LibrespotException.From(e);
            }
            finally
            {
                response?.Dispose();
            }

            if (measureThroughput)
            {
                var durationMs = (long)Stopwatch.GetElapsedTime(requestTime).TotalMilliseconds;
                if (actualLength > 0 && durationMs > 0)
                {
                    var throughput = (long)OneSecond.TotalMilliseconds * actualLength / durationMs;
                    if (!fileDataTx.TryWrite(new ReceivedData.Throughput(throughput)))
                    {
                        return;
                    }
                }
            }

            var bytesRemaining = request.Length - actualLength;
            if (bytesRemaining > 0)
            {
                var missingRange = new Range(offset, bytesRemaining);
                lock (shared.DownloadStatus)
                {
                    shared.DownloadStatus.Requested.SubtractRange(missingRange);

                    // Upstream never marks a range requested whose request could not be built, so nobody is woken
                    // for it; waking would only make FetchBlocking re-request it from a fetch loop that is ending.
                    if (fatal is null)
                    {
                        shared.DownloadStatus.NotifyAll();
                    }
                }
            }
        }
        finally
        {
            shared.DownloadSlots.Release();
        }

        if (fatal is not null)
        {
            fileDataTx.TryWrite(new ReceivedData.Fatal(fatal));
            return;
        }

        if (result is not null)
        {
            // Upstream returns the error from the spawned task, where it is dropped; logging it is all that happens.
            Log.LogError("Streamer error requesting range {Offset} +{Length}: {Error}", request.Offset, request.Length, result.Message);
        }
    }

    /// <summary>
    /// True for the errors upstream raises synchronously from <c>stream_from_cdn</c> (building the request, the local
    /// rate limiter in <c>request_fut</c>), as opposed to errors of the request itself.
    /// </summary>
    private static bool IsRequestBuildError(LibrespotException e) =>
        e is HttpClientException { Cause: HttpClientException.Reason.RateLimited } or { Kind: ErrorKind.InvalidArgument };

    private bool HasDownloadSlotsAvailable => _shared.DownloadSlots.CurrentCount > 0;

    private void DownloadRange(long offset, long length)
    {
        if (length < _params.MinimumDownloadSize)
        {
            length = _params.MinimumDownloadSize;
        }

        // If we are in streaming mode (so not seeking) then start downloading as large
        // of chunks as possible for better throughput and improved CPU usage, while
        // still being reasonably responsive (~1 second) in case we want to seek.
        if (_shared.IsDownloadStreaming)
        {
            var throughput = _shared.Throughput;
            length = Math.Max(length, throughput);
        }

        if (offset + length > _shared.FileSize)
        {
            // Upstream would underflow (panic) for an offset past the end; clamp to an empty range instead.
            length = Math.Max(_shared.FileSize - offset, 0);
        }

        var rangesToRequest = new RangeSet();
        rangesToRequest.AddRange(new Range(offset, length));

        // The iteration that follows spawns streamers fast, without awaiting them,
        // so holding the lock for the entire scope of this function should be faster
        // then locking and unlocking multiple times.
        var status = _shared.DownloadStatus;
        lock (status)
        {
            rangesToRequest.SubtractRangeSet(status.Downloaded);
            rangesToRequest.SubtractRangeSet(status.Requested);

            // Upstream TODO, kept as is: refresh cdn_url when the token expired.
            foreach (var range in rangesToRequest.Ranges)
            {
                var streamingRequest = new StreamingRequest
                {
                    Streamer = ct => _fetcher(_shared.CdnUrl, range.Start, range.Length, ct),
                    InitialResponse = null,
                    Offset = range.Start,
                    Length = range.Length,
                };

                status.Requested.AddRange(range);

                Spawn(() => ReceiveDataAsync(_shared, _fileDataTx, streamingRequest, _shutdown));
            }
        }
    }

    private void PreFetchMoreData(long bytes)
    {
        // determine what is still missing
        var missingData = new RangeSet();
        missingData.AddRange(new Range(0, _shared.FileSize));
        lock (_shared.DownloadStatus)
        {
            missingData.SubtractRangeSet(_shared.DownloadStatus.Downloaded);
            missingData.SubtractRangeSet(_shared.DownloadStatus.Requested);
        }

        // download data from after the current read position first
        var tailEnd = new RangeSet();
        var readPosition = _shared.ReadPosition;
        tailEnd.AddRange(new Range(readPosition, _shared.FileSize - readPosition));
        tailEnd = tailEnd.Intersection(missingData);

        if (!tailEnd.IsEmpty)
        {
            var range = tailEnd.GetRange(0);
            var offset = range.Start;
            var length = Math.Min(range.Length, bytes);
            DownloadRange(offset, length);
        }
        else if (!missingData.IsEmpty)
        {
            // ok, the tail is downloaded, download something fom the beginning.
            var range = missingData.GetRange(0);
            var offset = range.Start;
            var length = Math.Min(range.Length, bytes);
            DownloadRange(offset, length);
        }
    }

    /// <summary>Returns true when the loop should stop (upstream <c>ControlFlow::Break</c>).</summary>
    private bool HandleFileData(ReceivedData data)
    {
        switch (data)
        {
            case ReceivedData.Throughput(var measured):
            {
                var throughput = measured;
                if (throughput < _params.MinimumThroughput)
                {
                    Log.LogWarning(
                        "Throughput {Throughput} kbps lower than minimum {Minimum}, setting to minimum",
                        throughput / 1000,
                        _params.MinimumThroughput / 1000);
                    throughput = _params.MinimumThroughput;
                }

                var oldThroughput = _shared.Throughput;
                var avgThroughput = oldThroughput > 0 ? (oldThroughput + throughput) / 2 : throughput;

                // print when the new estimate deviates by more than 10% from the last
                if (MathF.Abs(((float)avgThroughput - oldThroughput) / oldThroughput) > 0.1f)
                {
                    Log.LogTrace("Throughput now estimated as: {Throughput} kbps", avgThroughput / 1000);
                }

                _shared.SetThroughput(avgThroughput);
                break;
            }

            case ReceivedData.ResponseTime(var measured):
            {
                var responseTime = measured;
                if (responseTime > _params.MaximumAssumedPingTime)
                {
                    Log.LogWarning(
                        "Time to first byte {ResponseTime} ms exceeds maximum {Maximum}, setting to maximum",
                        (long)responseTime.TotalMilliseconds,
                        (long)_params.MaximumAssumedPingTime.TotalMilliseconds);
                    responseTime = _params.MaximumAssumedPingTime;
                }

                var oldPingTimeMs = (long)_shared.PingTime.TotalMilliseconds;

                // prune old response times. Keep at most two so we can push a third.
                while (_networkResponseTimes.Count >= 3)
                {
                    _networkResponseTimes.RemoveAt(0);
                }

                // record the response time
                _networkResponseTimes.Add(responseTime);

                // calculate the median of up to three ourselves.
                var pingTime = _networkResponseTimes.Count switch
                {
                    1 => _networkResponseTimes[0],
                    2 => (_networkResponseTimes[0] + _networkResponseTimes[1]) / 2,
                    3 => Median3(_networkResponseTimes),
                    _ => throw new UnreachableException(),
                };

                // print when the new estimate deviates by more than 10% from the last
                if (MathF.Abs(((float)(long)pingTime.TotalMilliseconds - oldPingTimeMs) / oldPingTimeMs) > 0.1f)
                {
                    Log.LogTrace("Time to first byte now estimated as: {PingTime} ms", (long)pingTime.TotalMilliseconds);
                }

                // store our new estimate for everyone to see
                _shared.SetPingTime(pingTime);
                break;
            }

            case ReceivedData.Data(var partial):
            {
                if (_output is not { } output)
                {
                    throw AudioFileException.Output();
                }

                output.Seek(partial.Offset, SeekOrigin.Begin);
                output.Write(partial.Data);

                var receivedRange = new Range(partial.Offset, partial.Data.Length);

                bool full;
                lock (_shared.DownloadStatus)
                {
                    _shared.DownloadStatus.Downloaded.AddRange(receivedRange);
                    _shared.DownloadStatus.NotifyAll();

                    full = _shared.DownloadStatus.Downloaded.ContainedLengthFromValue(0) >= _shared.FileSize;
                }

                if (full)
                {
                    Finish();
                    return true;
                }

                break;
            }

            case ReceivedData.Fatal(var error):
                // upstream `download_range(..)?` / `pre_fetch_more_data(..)?`: the fetch task ends with the error
                throw error;
        }

        return false;
    }

    private static TimeSpan Median3(List<TimeSpan> times)
    {
        var sorted = times.ToArray();
        Array.Sort(sorted);
        return sorted[1];
    }

    /// <summary>Returns true when the loop should stop (upstream <c>ControlFlow::Break</c>).</summary>
    private bool HandleStreamLoaderCommand(StreamLoaderCommand cmd)
    {
        switch (cmd)
        {
            case StreamLoaderCommand.Fetch(var request):
                DownloadRange(request.Start, request.Length);
                break;
            case StreamLoaderCommand.Close:
                return true;
        }

        return false;
    }

    private void Finish()
    {
        var output = _output;
        _output = null;

        var completeTx = _completeTx;
        _completeTx = null;

        if (output is not null)
        {
            output.Seek(0, SeekOrigin.Begin);
            if (completeTx is not null && !completeTx.TrySetResult(output))
            {
                output.Dispose();
                throw AudioFileException.Channel();
            }
        }
    }

    // Upstream drops `output` and `complete_tx` when the task ends: the temp file is deleted and the cache task
    // does not run.
    private void Abandon()
    {
        _output?.Dispose();
        _output = null;
        _completeTx?.TrySetCanceled();
        _completeTx = null;
    }

    /// <summary>Upstream <c>audio_file_fetch</c>: the fetch loop of one streaming file.</summary>
    internal static async Task RunAsync(
        Func<string, long, long, CancellationToken, Task<HttpResponseMessage>> fetcher,
        AudioFileShared shared,
        StreamingRequest initialRequest,
        FileStream output,
        Channel<StreamLoaderCommand> streamLoaderCommands,
        TaskCompletionSource<FileStream> completeTx)
    {
        var streamLoaderCommandRx = streamLoaderCommands.Reader;

        var fileData = Channel.CreateUnbounded<ReceivedData>(new UnboundedChannelOptions { SingleReader = true });

        // Cancelled when the loop ends (see ReceiveDataAsync). Not disposed: receive tasks may still observe its token.
        var shutdown = new CancellationTokenSource();

        lock (shared.DownloadStatus)
        {
            // Upstream passes `offset + length` as the length; kept (the initial offset is always 0).
            var requestedRange = new Range(initialRequest.Offset, initialRequest.Offset + initialRequest.Length);
            shared.DownloadStatus.Requested.AddRange(requestedRange);
        }

        var shutdownToken = shutdown.Token;
        Spawn(() => ReceiveDataAsync(shared, fileData.Writer, initialRequest, shutdownToken));

        var parameters = AudioFetchParams.Get();

        var fetch = new AudioFileFetch(fetcher, shared, output, fileData.Writer, completeTx, parameters, shutdownToken);

        Task<bool>? commandWait = null;
        Task<bool>? dataWait = null;
        try
        {
            while (true)
            {
                // Upstream `tokio::select!` over both receivers.
                bool stop;
                if (streamLoaderCommandRx.TryRead(out var cmd))
                {
                    stop = fetch.HandleStreamLoaderCommand(cmd);
                }
                else if (fileData.Reader.TryRead(out var data))
                {
                    stop = fetch.HandleFileData(data);
                }
                else
                {
                    commandWait ??= streamLoaderCommandRx.WaitToReadAsync().AsTask();
                    dataWait ??= fileData.Reader.WaitToReadAsync().AsTask();
                    await Task.WhenAny(commandWait, dataWait).ConfigureAwait(false);

                    if (commandWait.IsCompleted)
                    {
                        var more = await commandWait.ConfigureAwait(false);
                        commandWait = null;
                        if (!more)
                        {
                            // every sender is gone
                            break;
                        }
                    }

                    if (dataWait.IsCompleted)
                    {
                        var more = await dataWait.ConfigureAwait(false);
                        dataWait = null;
                        if (!more)
                        {
                            break;
                        }
                    }

                    continue;
                }

                if (stop)
                {
                    break;
                }

                if (shared.IsDownloadStreaming && fetch.HasDownloadSlotsAvailable)
                {
                    long bytesPending;
                    lock (shared.DownloadStatus)
                    {
                        bytesPending = shared.DownloadStatus.Requested.Minus(shared.DownloadStatus.Downloaded).Len;
                    }

                    var pingTimeSeconds = (float)shared.PingTime.TotalSeconds;
                    var throughput = shared.Throughput;

                    var desiredPendingBytes = Math.Max(
                        (long)(parameters.PrefetchThresholdFactor * pingTimeSeconds * shared.BytesPerSecond),
                        (long)(pingTimeSeconds * throughput));

                    if (bytesPending < desiredPendingBytes)
                    {
                        fetch.PreFetchMoreData(desiredPendingBytes - bytesPending);
                    }
                }
            }
        }
        finally
        {
            shutdown.Cancel();
            fileData.Writer.TryComplete();

            // Upstream drops `stream_loader_command_rx` here, so further sends fail (BrokenPipe in Read, ignored by the
            // controller) instead of waiting for a download that will never happen.
            streamLoaderCommands.Writer.TryComplete();
            fetch.Abandon();
        }
    }
}
