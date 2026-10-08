using System.Net;
using System.Net.Http;
using Librespot.Core;
using Xunit;
using HttpClient = Librespot.Core.HttpClient;

namespace Librespot.Tests.Core;

public class HttpClientTests
{
    private const long Now = 1_700_000_000_000;

    private static HttpResponseMessage Response(params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    [Fact]
    public void Retry_after_seconds()
    {
        using var response = Response(("Retry-After", "3"));
        Assert.Equal(TimeSpan.FromSeconds(3), HttpClient.GetRetryAfterAt(response.Headers, Now));
    }

    [Fact]
    public void Retry_after_beyond_max_wait_is_ignored()
    {
        using var response = Response(("Retry-After", "11"));
        Assert.Null(HttpClient.GetRetryAfterAt(response.Headers, Now));
    }

    [Fact]
    public void Retry_after_http_date_is_not_supported_like_upstream()
    {
        using var response = Response(("Retry-After", "Wed, 21 Oct 2015 07:28:00 GMT"));
        Assert.Null(HttpClient.GetRetryAfterAt(response.Headers, Now));
    }

    [Fact]
    public void Fastly_reset_is_a_unix_ms_timestamp()
    {
        using var response = Response(("Fastly-RateLimit-Reset", (Now + 2500).ToString()), ("Retry-After", "1"));
        Assert.Equal(TimeSpan.FromMilliseconds(2500), HttpClient.GetRetryAfterAt(response.Headers, Now));
    }

    [Fact]
    public void Fastly_reset_in_the_past_is_ignored()
    {
        // Upstream: negative i64 `as u64` is huge, so it exceeds the limit.
        using var response = Response(("Fastly-RateLimit-Reset", (Now - 1000).ToString()));
        Assert.Null(HttpClient.GetRetryAfterAt(response.Headers, Now));
    }

    [Fact]
    public void Akamai_next_is_an_iso8601_date_and_takes_precedence()
    {
        var target = DateTimeOffset.FromUnixTimeMilliseconds(Now + 4000).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        using var response = Response(("X-RateLimit-Next", target), ("Fastly-RateLimit-Reset", (Now + 1000).ToString()));
        Assert.Equal(TimeSpan.FromSeconds(4), HttpClient.GetRetryAfterAt(response.Headers, Now));
    }

    [Fact]
    public void Unparsable_header_shadows_lower_priority_ones()
    {
        using var response = Response(("X-RateLimit-Next", "garbage"), ("Retry-After", "1"));
        Assert.Null(HttpClient.GetRetryAfterAt(response.Headers, Now));
    }

    [Theory]
    [InlineData("https://api.spotify.com/v1/me", "spotifycom")]
    [InlineData("https://spotify.com/", "spotifycom")]
    [InlineData("http://localhost:8080/", "localhost")]
    [InlineData("https://audio4-fa.scdn.co/audio/x", "scdnco")]
    public void Rate_limit_key_is_the_last_two_labels(string url, string expected)
    {
        Assert.Equal(expected, HttpClient.RateLimitKey(new Uri(url)));
    }

    [Fact]
    public void Rate_limiter_allows_burst_then_replenishes()
    {
        var time = new ManualTimeProvider();
        var limiter = new KeyedRateLimiter(TimeSpan.FromMilliseconds(100), 300, time);

        for (var i = 0; i < 300; i++)
        {
            Assert.True(limiter.TryAcquire("spotifycom", out _));
        }

        Assert.False(limiter.TryAcquire("spotifycom", out var wait));
        Assert.Equal(TimeSpan.FromMilliseconds(100), wait);

        // Other domains have their own bucket.
        Assert.True(limiter.TryAcquire("scdnco", out _));

        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(limiter.TryAcquire("spotifycom", out _));
        Assert.False(limiter.TryAcquire("spotifycom", out _));

        time.Advance(TimeSpan.FromSeconds(30));
        for (var i = 0; i < 300; i++)
        {
            Assert.True(limiter.TryAcquire("spotifycom", out _));
        }
    }

    [Fact]
    public void Status_code_kinds_match_upstream()
    {
        Assert.Equal(ErrorKind.NotFound, HttpClientException.KindFor(HttpStatusCode.NotFound));
        Assert.Equal(ErrorKind.ResourceExhausted, HttpClientException.KindFor(HttpStatusCode.TooManyRequests));
        Assert.Equal(ErrorKind.Unauthenticated, HttpClientException.KindFor(HttpStatusCode.Unauthorized));
        Assert.Equal(ErrorKind.OutOfRange, HttpClientException.KindFor(HttpStatusCode.RequestedRangeNotSatisfiable));
        Assert.Equal(ErrorKind.Unavailable, HttpClientException.KindFor(HttpStatusCode.ServiceUnavailable));
        Assert.Equal(ErrorKind.Unknown, HttpClientException.KindFor(HttpStatusCode.BadGateway));
        Assert.Equal(ErrorKind.Unknown, HttpClientException.KindFor((HttpStatusCode)418));
    }

    [Fact]
    public async Task Request_retries_after_429_and_sets_user_agent()
    {
        var handler = new ScriptedHandler(
            _ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                r.Headers.TryAddWithoutValidation("Retry-After", "0");
                return r;
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        var client = new HttpClient(null, TimeProvider.System, handler);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com/x") { Content = new ByteArrayContent([9]) };
        var body = await client.RequestBodyAsync(request);

        Assert.Equal(new byte[] { 1, 2, 3 }, body);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal(client.UserAgent, r.UserAgent);
            Assert.Equal(new byte[] { 9 }, r.Body);
        });
        Assert.StartsWith("Spotify/", client.UserAgent);
        Assert.EndsWith($"({LibrespotVersion.VersionString})", client.UserAgent);
    }

    [Fact]
    public async Task Request_throws_on_non_success()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = new HttpClient(null, TimeProvider.System, handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/missing");
        var error = await Assert.ThrowsAsync<HttpClientException>(() => client.RequestAsync(request));

        Assert.Equal(HttpClientException.Reason.StatusCode, error.Cause);
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Equal(ErrorKind.NotFound, error.Kind);
        Assert.StartsWith("Response status code: 404", error.Detail);
    }

    [Fact]
    public async Task Request_stream_returns_non_success_without_throwing()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var client = new HttpClient(null, TimeProvider.System, handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://audio.example.com/file");
        using var response = await client.RequestStreamAsync(request);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Local_rate_limit_refuses_with_resource_exhausted()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var time = new ManualTimeProvider();
        var client = new HttpClient(null, time, handler);

        for (var i = 0; i < HttpClient.RateLimitCallsPerInterval; i++)
        {
            using var ok = new HttpRequestMessage(HttpMethod.Get, "https://a.example.com/");
            using var _ = await client.RequestStreamAsync(ok);
        }

        using var refused = new HttpRequestMessage(HttpMethod.Get, "https://b.example.com/");
        var error = await Assert.ThrowsAsync<HttpClientException>(() => client.RequestStreamAsync(refused));
        Assert.Equal(HttpClientException.Reason.RateLimited, error.Cause);
        Assert.Equal(ErrorKind.ResourceExhausted, error.Kind);
        Assert.Equal("rate limited for at least another 0 seconds", error.Detail);
    }

    private sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _next;

        public List<(string? UserAgent, byte[]? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add((request.Headers.NonValidated.TryGetValues("User-Agent", out var ua) ? ua.ToString() : null, body));
            var factory = responses[Math.Min(_next++, responses.Length - 1)];
            var response = factory(request);
            response.RequestMessage = request;
            return response;
        }
    }
}

/// <summary>A <see cref="TimeProvider"/> whose clock only moves when told to.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
