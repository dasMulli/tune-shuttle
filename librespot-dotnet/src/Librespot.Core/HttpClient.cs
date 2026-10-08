// Port of core/src/http_client.rs.
// TODO(port: core-connection): proxy support (HttpClientHandler.Proxy), user agent (Version.SpotifyVersion + Platform),
// rate limiter (governor -> token bucket: RATE_LIMIT_CALLS_PER_INTERVAL per RATE_LIMIT_INTERVAL), Retry-After parsing,
// request_stream (streaming body).
//
// Name clash note: this type shadows System.Net.Http.HttpClient; Directory.Build.props removes the implicit
// `using System.Net.Http;` solution-wide so that `HttpClient` means this type inside the Librespot namespaces.
using System.Net.Http;
using System.Net.Http.Headers;

namespace Librespot.Core;

/// <summary>Upstream <c>HttpClientError</c>.</summary>
public sealed class HttpClientException : LibrespotException
{
    public enum Reason
    {
        /// <summary>Response status code not successful (kind Unavailable / FailedPrecondition per status).</summary>
        StatusCode,
        /// <summary>Rate limited; wait for <see cref="RetryAfter"/>.</summary>
        RateLimited,
    }

    public Reason Cause { get; }
    public System.Net.HttpStatusCode? StatusCode { get; }
    public TimeSpan? RetryAfter { get; }

    public HttpClientException(Reason cause, ErrorKind kind, string detail, System.Net.HttpStatusCode? statusCode = null, TimeSpan? retryAfter = null)
        : base(kind, detail)
    {
        Cause = cause;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }
}

/// <summary>Shared, rate-limited HTTP/1.1+2 client with the Spotify user agent.</summary>
public sealed class HttpClient
{
    public static readonly TimeSpan RateLimitInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RateLimitMaxWait = TimeSpan.FromSeconds(10);
    public const int RateLimitCallsPerInterval = 300;

    private readonly System.Net.Http.HttpClient _inner;

    public HttpClient(Uri? proxyUrl)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };

        if (proxyUrl is not null)
        {
            handler.Proxy = new System.Net.WebProxy(proxyUrl);
            handler.UseProxy = true;
        }

        _inner = new System.Net.Http.HttpClient(handler);
        _inner.DefaultRequestHeaders.UserAgent.ParseAdd(LibrespotVersion.FallbackUserAgent); // TODO(port: core-connection): dynamic user agent
    }

    /// <summary>The underlying .NET client, for code that needs streaming responses.</summary>
    public System.Net.Http.HttpClient Inner => _inner;

    /// <summary>Upstream <c>request(req)</c>: sends with rate limiting, maps non-success status to <see cref="HttpClientException"/>.</summary>
    public Task<HttpResponseMessage> RequestAsync(HttpRequestMessage request, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: core-connection)");

    /// <summary>Upstream <c>request_body(req)</c>: the full response body.</summary>
    public Task<byte[]> RequestBodyAsync(HttpRequestMessage request, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: core-connection)");

    /// <summary>
    /// Upstream <c>request_stream(req)</c>: sends with <c>HttpCompletionOption.ResponseHeadersRead</c> and returns
    /// the response as soon as the headers arrived; the body is streamed via <c>Content.ReadAsStreamAsync</c>.
    /// Unlike <see cref="RequestAsync"/> a non-success status is returned, not thrown (the audio fetcher inspects it).
    /// </summary>
    public Task<HttpResponseMessage> RequestStreamAsync(HttpRequestMessage request, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: core-connection)");

    /// <summary>Upstream <c>get_retry_after(headers)</c>.</summary>
    public static TimeSpan? GetRetryAfter(HttpResponseHeaders headers) =>
        headers.RetryAfter?.Delta ?? (headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
}
