// Port of core/src/http_client.rs.
//
// Name clash note: this type shadows System.Net.Http.HttpClient; Directory.Build.props removes the implicit
// `using System.Net.Http;` solution-wide so that `HttpClient` means this type inside the Librespot namespaces.
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Librespot.Core;

/// <summary>Upstream <c>HttpClientError</c>.</summary>
public sealed class HttpClientException : LibrespotException
{
    /// <summary>The upstream variant (plus the rate limiter refusal, which upstream reports as a plain error).</summary>
    public enum Reason
    {
        /// <summary>Response status code not successful (kind derived from the status, see <see cref="KindFor"/>).</summary>
        StatusCode,
        /// <summary>Rate limited by the local rate limiter; wait for <see cref="RetryAfter"/>.</summary>
        RateLimited,
    }

    /// <summary>Which variant this is.</summary>
    public Reason Cause { get; }

    /// <summary>The response status, for <see cref="Reason.StatusCode"/>.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>How long to wait before trying again, for <see cref="Reason.RateLimited"/>.</summary>
    public TimeSpan? RetryAfter { get; }

    public HttpClientException(Reason cause, ErrorKind kind, string detail, HttpStatusCode? statusCode = null, TimeSpan? retryAfter = null)
        : base(kind, detail)
    {
        Cause = cause;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// Upstream <c>HttpClientError::StatusCode(code)</c> converted into an <c>Error</c>: the message is
    /// <c>"Response status code: {code} {reason}"</c> and the kind is <see cref="KindFor"/>.
    /// </summary>
    public static HttpClientException FromStatusCode(HttpStatusCode code, string? reasonPhrase = null) =>
        new(Reason.StatusCode, KindFor(code), $"Response status code: {(int)code} {reasonPhrase ?? "<unknown status code>"}", code);

    /// <summary>The upstream <c>impl From&lt;HttpClientError&gt; for Error</c> classification (not exhaustive).</summary>
    public static ErrorKind KindFor(HttpStatusCode code) => (int)code switch
    {
        504 or 408 => ErrorKind.DeadlineExceeded,
        410 or 404 or 301 or 308 or 307 => ErrorKind.NotFound,
        403 or 402 => ErrorKind.PermissionDenied,
        511 or 407 or 401 => ErrorKind.Unauthenticated,
        417 or 412 or 428 => ErrorKind.FailedPrecondition,
        416 => ErrorKind.OutOfRange,
        500 or 421 or 503 or 451 => ErrorKind.Unavailable,
        400 or 505 or 411 or 405 or 406 or 413 or 431 or 415 or 414 => ErrorKind.InvalidArgument,
        429 => ErrorKind.ResourceExhausted,
        501 => ErrorKind.Unimplemented,
        _ => ErrorKind.Unknown,
    };
}

/// <summary>Shared, rate-limited HTTP/1.1+2 client with the Spotify user agent.</summary>
public sealed class HttpClient
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<HttpClient>();

    /// <summary>
    /// The 30 seconds interval is documented by Spotify, but the calls per interval is a guesstimate and probably
    /// subject to licensing (purchasing extra calls) and may change at any time.
    /// </summary>
    public static readonly TimeSpan RateLimitInterval = TimeSpan.FromSeconds(30);

    /// <summary>The longest <c>Retry-After</c> wait that is honoured (upstream <c>RATE_LIMIT_MAX_WAIT</c>).</summary>
    public static readonly TimeSpan RateLimitMaxWait = TimeSpan.FromSeconds(10);

    /// <summary>Calls allowed per <see cref="RateLimitInterval"/> and domain (upstream <c>RATE_LIMIT_CALLS_PER_INTERVAL</c>).</summary>
    public const int RateLimitCallsPerInterval = 300;

    private readonly string _userAgent;
    private readonly Uri? _proxyUrl;
    private readonly Lazy<System.Net.Http.HttpClient> _inner;
    private readonly KeyedRateLimiter _rateLimiter;

    /// <summary>Upstream <c>HttpClient::new(proxy_url)</c>.</summary>
    public HttpClient(Uri? proxyUrl)
        : this(proxyUrl, TimeProvider.System)
    {
    }

    internal HttpClient(Uri? proxyUrl, TimeProvider timeProvider, HttpMessageHandler? handler = null)
    {
        var osVersion = Platform.OsVersion();

        var (spotifyPlatform, platformVersion) = Platform.Os switch
        {
            "android" => ("Android", osVersion),
            "ios" => ("iOS", osVersion),
            "macos" => ("OSX", "0"),
            "windows" => ("Win32", "0"),
            _ => ("Linux", "0"),
        };

        var userAgent = $"Spotify/{LibrespotVersion.SpotifyVersionForPlatform()} {spotifyPlatform}/{platformVersion} ({LibrespotVersion.VersionString})";
        if (!IsValidHeaderValue(userAgent))
        {
            Log.LogError("Invalid user agent <{UserAgent}>: failed to parse header value", userAgent);
            userAgent = LibrespotVersion.FallbackUserAgent;
        }

        _userAgent = userAgent;
        _proxyUrl = proxyUrl;
        _rateLimiter = new KeyedRateLimiter(RateLimitInterval / RateLimitCallsPerInterval, RateLimitCallsPerInterval, timeProvider);

        // configuring TLS is expensive and should be done once per client, on first use (upstream OnceLock)
        _inner = new Lazy<System.Net.Http.HttpClient>(() => CreateClient(_proxyUrl, handler), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The user agent sent with every request.</summary>
    public string UserAgent => _userAgent;

    /// <summary>The underlying .NET client (created on first use). Requests sent through it bypass rate limiting.</summary>
    public System.Net.Http.HttpClient Inner => _inner.Value;

    private static System.Net.Http.HttpClient CreateClient(Uri? proxyUrl, HttpMessageHandler? handler)
    {
        if (handler is null)
        {
            var sockets = new SocketsHttpHandler
            {
                // Upstream hyper does not decompress; spclient sends `Accept-Encoding: gzip` itself and decodes.
                AutomaticDecompression = DecompressionMethods.None,
                // Upstream's dummy proxy (Intercept::None) when no proxy is configured: never use the system proxy.
                UseProxy = proxyUrl is not null,
                // hyper's http2_adaptive_window(true): SocketsHttpHandler scales the HTTP/2 window dynamically by default.
            };

            if (proxyUrl is not null)
            {
                sockets.Proxy = new WebProxy(proxyUrl);
            }

            handler = sockets;
        }

        return new System.Net.Http.HttpClient(handler)
        {
            // Bodies may be streamed for a long time (audio); per-request cancellation is used instead.
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// Upstream <c>request(req)</c>: sends with rate limiting, retries after a 429 with an acceptable
    /// <see cref="GetRetryAfter"/>, and maps any other non-success status to <see cref="HttpClientException"/>.
    /// The returned response's content is buffered.
    /// </summary>
    public async Task<HttpResponseMessage> RequestAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        Log.LogDebug("Requesting {Uri}", request.RequestUri);

        // `HttpRequestMessage` can only be sent once, so every attempt sends a copy with the body buffered up front
        // (upstream clones `Request<Bytes>` for the same reason).
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        while (true)
        {
            using var attempt = CloneRequest(request, body);
            var response = await SendAsync(attempt, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);

            var code = response.StatusCode;
            if (code == HttpStatusCode.TooManyRequests && GetRetryAfter(response.Headers) is { } duration)
            {
                Log.LogWarning("Rate limited by service, retrying in {Seconds} seconds...", (long)duration.TotalSeconds);
                response.Dispose();
                await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = HttpClientException.FromStatusCode(code, response.ReasonPhrase);
                response.Dispose();
                throw error;
            }

            return response;
        }
    }

    /// <summary>Upstream <c>request_body(req)</c>: <see cref="RequestAsync"/> and the full response body.</summary>
    public async Task<byte[]> RequestBodyAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        using var response = await RequestAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not LibrespotException and not OperationCanceledException)
        {
            throw LibrespotException.From(e);
        }
    }

    /// <summary>
    /// Upstream <c>request_stream(req)</c>: sends with <c>HttpCompletionOption.ResponseHeadersRead</c> and returns
    /// the response as soon as the headers arrived; the body is streamed via <c>Content.ReadAsStreamAsync</c>.
    /// Unlike <see cref="RequestAsync"/> a non-success status is returned, not thrown (the audio fetcher inspects it),
    /// and a 429 is not retried. The caller disposes the response.
    /// </summary>
    public Task<HttpResponseMessage> RequestStreamAsync(HttpRequestMessage request, CancellationToken cancellationToken = default) =>
        SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    /// <summary>
    /// Upstream <c>request_fut(req)</c>: sets the user agent, applies the per-domain rate limit and sends.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        request.Headers.Remove("User-Agent");
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);

        // hyper negotiates HTTP/2 via ALPN whenever the server offers it, independent of the request's version.
        if (request.Version == HttpVersion.Version11 && request.VersionPolicy == HttpVersionPolicy.RequestVersionOrLower)
        {
            request.Version = HttpVersion.Version20;
        }

        // For rate limiting we cannot *just* depend on Spotify sending us HTTP/429
        // Retry-After headers. For example, when there is a service interruption
        // and HTTP/500 is returned, we don't want to DoS the Spotify infrastructure.
        var domain = RateLimitKey(request.RequestUri);
        if (!_rateLimiter.TryAcquire(domain, out var waitTime))
        {
            throw new HttpClientException(
                HttpClientException.Reason.RateLimited,
                ErrorKind.ResourceExhausted,
                $"rate limited for at least another {(long)waitTime.TotalSeconds} seconds",
                retryAfter: waitTime);
        }

        try
        {
            return await Inner.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not LibrespotException && !(e is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            throw LibrespotException.From(e);
        }
    }

    /// <summary>
    /// The rate-limit key of a URI: the last two host labels concatenated (assume rate limit is per domain, not
    /// subdomain). Upstream collects the labels into a <c>String</c> without a separator, so
    /// <c>api.spotify.com</c> becomes <c>spotifycom</c>; the key is only used for bucketing.
    /// </summary>
    internal static string RateLimitKey(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || string.IsNullOrEmpty(uri.Host))
        {
            return "";
        }

        var parts = uri.Host.Split('.');
        var n = Math.Max(parts.Length - 2, 0);
        return string.Concat(parts[n..]);
    }

    /// <summary>
    /// Upstream <c>get_retry_after(headers)</c>: how long to wait before retrying, from <c>X-RateLimit-Next</c>
    /// (Akamai, ISO 8601 date), <c>Fastly-RateLimit-Reset</c> (Fastly, Unix ms) or <c>Retry-After</c> (seconds),
    /// in that order of precedence. Null when absent, unparsable or longer than <see cref="RateLimitMaxWait"/>.
    /// </summary>
    public static TimeSpan? GetRetryAfter(HttpResponseHeaders headers) => GetRetryAfterAt(headers, Date.NowUtc().AsTimestampMs());

    internal static TimeSpan? GetRetryAfterAt(HttpHeaders headers, long now)
    {
        long? retryAfterMs = null;
        if (FirstValue(headers, "X-RateLimit-Next") is { } nextHeader)
        {
            // *.akamaized.net (Akamai)
            try
            {
                var target = Date.FromIso8601(nextHeader);
                retryAfterMs = SaturatingSub(target.AsTimestampMs(), now);
            }
            catch (LibrespotException)
            {
                // Not a date: ignored like upstream.
            }
        }
        else if (FirstValue(headers, "Fastly-RateLimit-Reset") is { } resetHeader)
        {
            // *.scdn.co (Fastly)
            if (long.TryParse(resetHeader, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var target))
            {
                retryAfterMs = SaturatingSub(target, now);
            }
        }
        else if (FirstValue(headers, "Retry-After") is { } retryAfterHeader)
        {
            // Generic RFC compliant (including *.spotify.com)
            if (long.TryParse(retryAfterHeader, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds))
            {
                retryAfterMs = unchecked(seconds * 1000);
            }
        }

        if (retryAfterMs is { } retryAfter)
        {
            // Upstream converts with `as u64`: a negative wait becomes a huge one and is rejected below.
            var ms = unchecked((ulong)retryAfter);
            if (ms <= (ulong)RateLimitMaxWait.TotalMilliseconds)
            {
                return TimeSpan.FromMilliseconds(ms);
            }

            Log.LogDebug(
                "Waiting {Seconds} seconds would exceed {Limit} second limit",
                ms / 1000,
                (long)RateLimitMaxWait.TotalSeconds);
        }

        return null;
    }

    private static string? FirstValue(HttpHeaders headers, string name) =>
        headers.NonValidated.TryGetValues(name, out var values) && values.Count > 0 ? values.First() : null;

    private static long SaturatingSub(long a, long b)
    {
        var result = a - b;
        // Overflow iff the operands have different signs and the result's sign differs from a's.
        if (((a ^ b) & (a ^ result)) < 0)
        {
            return a < 0 ? long.MinValue : long.MaxValue;
        }

        return result;
    }

    private static bool IsValidHeaderValue(string value)
    {
        foreach (var c in value)
        {
            if ((c < 0x20 && c != '\t') || c >= 0x7f)
            {
                return false;
            }
        }

        return true;
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request, byte[]? body)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };

        foreach (var header in request.Headers.NonValidated)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (body is not null && request.Content is not null)
        {
            var content = new ByteArrayContent(body);
            foreach (var header in request.Content.Headers.NonValidated)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            clone.Content = content;
        }

        foreach (var option in request.Options)
        {
            ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;
        }

        return clone;
    }
}

/// <summary>
/// Keyed GCRA rate limiter, the behaviour of <c>governor::RateLimiter::keyed(Quota::with_period(period).allow_burst(burst))</c>
/// used by upstream: each key may spend <c>burst</c> cells at once and regains one cell per <c>period</c>.
/// <see cref="TryAcquire"/> never waits; a refused call reports the earliest time a cell is available.
/// </summary>
internal sealed class KeyedRateLimiter
{
    private readonly long _periodTicks;
    private readonly long _burstTicks;
    private readonly TimeProvider _timeProvider;
    private readonly long _origin;
    private readonly Dictionary<string, long> _theoreticalArrival = new();
    private readonly object _lock = new();

    public KeyedRateLimiter(TimeSpan period, int burst, TimeProvider timeProvider)
    {
        _periodTicks = period.Ticks;
        _burstTicks = period.Ticks * burst;
        _timeProvider = timeProvider;
        _origin = timeProvider.GetTimestamp();
    }

    private long NowTicks() => _timeProvider.GetElapsedTime(_origin).Ticks;

    /// <summary>Takes one cell for <paramref name="key"/>; on refusal <paramref name="waitTime"/> is the time until one is available.</summary>
    public bool TryAcquire(string key, out TimeSpan waitTime)
    {
        lock (_lock)
        {
            var now = NowTicks();
            var tat = _theoreticalArrival.TryGetValue(key, out var stored) ? Math.Max(stored, now) : now;
            var newTat = tat + _periodTicks;

            if (newTat - now > _burstTicks)
            {
                waitTime = TimeSpan.FromTicks(newTat - now - _burstTicks);
                return false;
            }

            _theoreticalArrival[key] = newTat;
            waitTime = TimeSpan.Zero;
            return true;
        }
    }
}
