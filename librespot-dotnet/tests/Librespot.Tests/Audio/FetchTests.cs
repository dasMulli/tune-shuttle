// Tests for audio/src/fetch/mod.rs and fetch/receive.rs (upstream has no #[cfg(test)] module). The CDN is faked by
// the fetcher delegate that production code gets from SpClient.StreamFromCdnAsync.
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Librespot.Audio.Fetch;
using Librespot.Core;
using Xunit;

namespace Librespot.Tests.Audio;

public sealed class FetchTests : IDisposable
{
    private const int FileSize = 300_000;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "librespot-fetch-tests-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _file;

    public FetchTests()
    {
        Directory.CreateDirectory(_tmpDir);
        _file = new byte[FileSize];
        new Random(1234).NextBytes(_file);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tmpDir, recursive: true);
        }
        catch (IOException)
        {
            // a temp file may still be open by a background task; it is deleted on close
        }
    }

    /// <summary>A fake CDN serving <see cref="_file"/> with 206 + Content-Range; hooks let tests intercept requests.</summary>
    private sealed class FakeCdn(byte[] file)
    {
        public ConcurrentQueue<(string Url, long Offset, long Length)> Requests { get; } = new();

        public Func<string, long, long, CancellationToken, Task<HttpResponseMessage?>>? Intercept { get; set; }

        public async Task<HttpResponseMessage> FetchAsync(string url, long offset, long length, CancellationToken ct)
        {
            Requests.Enqueue((url, offset, length));
            if (Intercept is { } intercept && await intercept(url, offset, length, ct) is { } intercepted)
            {
                return intercepted;
            }

            return Serve(file, offset, length);
        }

        public static HttpResponseMessage Serve(byte[] file, long offset, long length)
        {
            var end = Math.Min(offset + length, file.Length);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(file, (int)offset, (int)(end - offset)),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, end - 1, file.Length);
            return response;
        }
    }

    private Task<AudioFileStreaming> OpenAsync(FakeCdn cdn, TaskCompletionSource<FileStream> complete, params string[] urls) =>
        AudioFileStreaming.OpenAsync(urls.Length == 0 ? ["https://cdn.test/a"] : urls, cdn.FetchAsync, _tmpDir, complete, 20_000, CancellationToken.None);

    private static TaskCompletionSource<FileStream> NewComplete() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task OpenReadsFileSizeAndReadsWholeFile()
    {
        var cdn = new FakeCdn(_file);
        var complete = NewComplete();
        await using var file = await OpenAsync(cdn, complete);

        Assert.False(file.IsCached);
        Assert.Equal(FileSize, file.Length);
        var controller = file.GetStreamLoaderController();
        Assert.Equal(FileSize, controller.Length);
        Assert.NotNull(controller.PingTime);

        var output = new byte[FileSize];
        var total = 0;
        while (total < FileSize)
        {
            var n = await Task.Run(() => file.Read(output, total, FileSize - total)).WaitAsync(TestTimeout);
            Assert.True(n > 0);
            total += n;
        }

        Assert.Equal(_file, output);
        Assert.Equal(0, file.Read(new byte[10], 0, 10));
        Assert.True(controller.RangeToEndAvailable());

        // the download task hands the complete temp file over (for the cache)
        await using var completed = await complete.Task.WaitAsync(TestTimeout);
        Assert.Equal(0, completed.Position);
        var copy = new MemoryStream();
        await completed.CopyToAsync(copy);
        Assert.Equal(_file, copy.ToArray());
    }

    [Fact]
    public async Task OpenSkipsUrlsThatDoNotAnswer206()
    {
        var cdn = new FakeCdn(_file)
        {
            Intercept = (url, _, _, _) => Task.FromResult<HttpResponseMessage?>(url switch
            {
                "https://bad/404" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "https://bad/throw" => throw new HttpRequestException("boom"),
                _ => null,
            }),
        };

        await using var file = await OpenAsync(cdn, NewComplete(), "https://bad/404", "https://bad/throw", "https://good/");
        Assert.Equal(FileSize, file.Length);
        Assert.Contains(cdn.Requests, r => r.Url == "https://good/" && r.Offset == 0 && r.Length == AudioFetchParams.Get().MinimumDownloadSize);
    }

    [Fact]
    public async Task OpenFailsWhenNoUrlAnswers()
    {
        var cdn = new FakeCdn(_file)
        {
            Intercept = (_, _, _, _) => Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage(HttpStatusCode.Forbidden)),
        };
        var complete = NewComplete();

        var e = await Assert.ThrowsAnyAsync<LibrespotException>(() => OpenAsync(cdn, complete, "https://a/", "https://b/"));
        Assert.Equal(ErrorKind.Unavailable, e.Kind);
        Assert.Equal("2 URLs failed, none left to try", e.Detail);
        Assert.True(complete.Task.IsCanceled);
    }

    [Fact]
    public async Task OpenFailsWithoutContentRange()
    {
        var cdn = new FakeCdn(_file)
        {
            Intercept = (_, _, _, _) => Task.FromResult<HttpResponseMessage?>(
                new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([1, 2, 3]) }),
        };

        var e = await Assert.ThrowsAsync<AudioFileException>(() => OpenAsync(cdn, NewComplete()));
        Assert.Equal(AudioFileException.Reason.Header, e.Cause);
        Assert.Equal(ErrorKind.Unavailable, e.Kind);
    }

    [Fact]
    public async Task ReadOfMissingRangeBlocksUntilDownloaded()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cdn = new FakeCdn(_file)
        {
            Intercept = async (_, offset, _, ct) =>
            {
                if (offset != 0)
                {
                    await gate.Task.WaitAsync(ct);
                }

                return null;
            },
        };

        await using var file = await OpenAsync(cdn, NewComplete());
        var controller = file.GetStreamLoaderController();

        file.Seek(200_000, SeekOrigin.Begin);
        Assert.False(controller.RangeAvailable(200_000, 1000));

        var buffer = new byte[1000];
        var read = Task.Run(() => file.Read(buffer, 0, buffer.Length));
        await Task.Delay(300);
        Assert.False(read.IsCompleted);

        gate.SetResult();
        var n = await read.WaitAsync(TestTimeout);

        Assert.Equal(1000, n);
        Assert.Equal(_file.AsSpan(200_000, 1000).ToArray(), buffer);
        Assert.True(controller.RangeAvailable(200_000, 1000));
        Assert.Equal(200_000 + 1000, file.Position);

        // the range request started at the read position with at least the minimum download size
        Assert.Contains(cdn.Requests, r => r.Offset == 200_000 && r.Length == AudioFetchParams.Get().MinimumDownloadSize);
    }

    [Fact]
    public async Task RateLimitedRequestIsRetried()
    {
        var rateLimited = 0;
        var cdn = new FakeCdn(_file)
        {
            Intercept = (_, offset, _, _) =>
            {
                if (offset != 0 && Interlocked.Exchange(ref rateLimited, 1) == 0)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    response.Headers.TryAddWithoutValidation("Retry-After", "1");
                    return Task.FromResult<HttpResponseMessage?>(response);
                }

                return Task.FromResult<HttpResponseMessage?>(null);
            },
        };

        await using var file = await OpenAsync(cdn, NewComplete());
        var controller = file.GetStreamLoaderController();

        var started = DateTime.UtcNow;
        await Task.Run(() => controller.FetchBlocking(150_000, 10_000)).WaitAsync(TestTimeout);

        Assert.True(controller.RangeAvailable(150_000, 10_000));
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(900), "the Retry-After delay must be honoured");
        Assert.Equal(2, cdn.Requests.Count(r => r.Offset == 150_000));
    }

    [Fact]
    public async Task LocalRateLimiterRefusalEndsFetchTask()
    {
        // upstream: `stream_from_cdn(..)?` in download_range fails, the fetch task ends, fetch_blocking times out
        var cdn = new FakeCdn(_file)
        {
            Intercept = (_, offset, _, _) => offset != 0
                ? throw new HttpClientException(
                    HttpClientException.Reason.RateLimited,
                    ErrorKind.ResourceExhausted,
                    "rate limited",
                    retryAfter: TimeSpan.FromSeconds(5))
                : Task.FromResult<HttpResponseMessage?>(null),
        };
        var complete = NewComplete();

        await using var file = await OpenAsync(cdn, complete);
        var controller = file.GetStreamLoaderController();

        var e = await Assert.ThrowsAsync<AudioFileException>(
            () => Task.Run(() => controller.FetchBlocking(150_000, 10_000)).WaitAsync(TestTimeout));
        Assert.Equal(AudioFileException.Reason.WaitTimeout, e.Cause);
        Assert.True(cdn.Requests.Count < 10, $"expected no retry spin, got {cdn.Requests.Count} requests");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => complete.Task.WaitAsync(TestTimeout));
        Assert.True(complete.Task.IsCanceled);

        // the command channel is closed with the loop: reading a missing range fails at once (upstream BrokenPipe)
        file.Seek(250_000, SeekOrigin.Begin);
        Assert.Throws<IOException>(() => file.Read(new byte[10], 0, 10));
    }

    [Fact]
    public async Task FetchNextAndWaitAsyncWaitsForData()
    {
        var cdn = new FakeCdn(_file);
        await using var file = await OpenAsync(cdn, NewComplete());
        var controller = file.GetStreamLoaderController();

        file.Seek(100_000, SeekOrigin.Begin);
        Assert.Equal(1, file.Read(new byte[1], 0, 1)); // sets the shared read position
        await controller.FetchNextAndWaitAsync(50_000, 20_000).WaitAsync(TestTimeout);
        Assert.True(controller.RangeAvailable(100_001, 20_000));
    }

    [Fact]
    public async Task StreamModeReadsWholeFileWithReadAhead()
    {
        var cdn = new FakeCdn(_file);
        var complete = NewComplete();
        await using var file = await OpenAsync(cdn, complete);
        var controller = file.GetStreamLoaderController();
        controller.SetStreamMode();

        // In stream mode every read requests ReadAheadDuringPlayback * bytesPerSecond (5 s * 20 000 B/s) beyond it.
        var buffer = new byte[4096];
        var output = new MemoryStream();
        int n;
        while ((n = await Task.Run(() => file.Read(buffer, 0, buffer.Length)).WaitAsync(TestTimeout)) > 0)
        {
            output.Write(buffer, 0, n);
        }

        Assert.Equal(_file, output.ToArray());
        Assert.Contains(cdn.Requests, r => r.Offset == 65_536 && r.Length == AudioFetchParams.Get().MinimumDownloadSize);

        await using var completed = await complete.Task.WaitAsync(TestTimeout);
        Assert.True(controller.RangeAvailable(0, FileSize));
        Assert.True(controller.RangeToEndAvailable());
    }

    [Fact]
    public async Task CloseStopsDownloadTask()
    {
        var cdn = new FakeCdn(_file);
        var complete = NewComplete();
        await using var file = await OpenAsync(cdn, complete);

        file.GetStreamLoaderController().Close();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => complete.Task.WaitAsync(TestTimeout));
    }

    [Fact]
    public void LocalFileController()
    {
        var controller = StreamLoaderController.FromLocalFile(1000);
        Assert.Equal(1000, controller.Length);
        Assert.False(controller.IsEmpty);
        Assert.True(controller.RangeAvailable(100, 900));
        Assert.False(controller.RangeAvailable(100, 901));
        Assert.True(controller.RangeToEndAvailable());
        Assert.Null(controller.PingTime);
        controller.FetchBlocking(0, 2000);
        controller.FetchNextAndWait(10, 10);
        controller.Close();
        Assert.True(StreamLoaderController.FromLocalFile(0).IsEmpty);
    }

    [Fact]
    public void ErrorsRenderLikeUpstream()
    {
        var e = AudioFileException.StatusCode(HttpStatusCode.TooManyRequests);
        Assert.Equal(ErrorKind.FailedPrecondition, e.Kind);
        Assert.Equal("invalid status code 429 Too Many Requests", e.Detail);
        Assert.Equal("206 Partial Content", AudioFileException.StatusDisplay(HttpStatusCode.PartialContent));
        Assert.Equal("200 OK", AudioFileException.StatusDisplay(HttpStatusCode.OK));

        Assert.Equal(ErrorKind.DeadlineExceeded, AudioFileException.WaitTimeout().Kind);
        Assert.Equal(ErrorKind.Aborted, AudioFileException.Channel().Kind);
        Assert.Equal(ErrorKind.Aborted, AudioFileException.Output().Kind);
        Assert.Equal(ErrorKind.Unavailable, AudioFileException.NoData().Kind);
    }
}
