using System.Net;
using Airplay2.Sender.Crypto;
using Microsoft.Extensions.Logging;

namespace Airplay2.Sender.Rtsp;

/// <summary>
/// One TCP connection to a receiver's AirPlay port carrying pairing POSTs, GET /info, RTSP methods and keep-alives
/// (SPEC-control.md 2). After pairing the same socket switches to the HAP frame encryption (2.5). Requests are
/// serialised one at a time; responses are matched by CSeq with FIFO fallback.
/// </summary>
public interface IRtspConnection : IAsyncDisposable
{
    /// <summary>The receiver endpoint.</summary>
    IPEndPoint RemoteEndPoint { get; }

    /// <summary>Our address on this connection: used in the session URI and as the timing/control address we announce.</summary>
    IPAddress LocalAddress { get; }

    /// <summary>The identity headers used on every request.</summary>
    RtspClientIdentity Identity { get; }

    /// <summary>Random 32-bit session id of this connection; the session URI is <c>rtsp://&lt;LocalAddress&gt;/&lt;SessionId&gt;</c>.</summary>
    uint SessionId { get; }

    /// <summary>The session URI for RTSP methods.</summary>
    string SessionUri { get; }

    /// <summary>True once <see cref="EnableEncryption"/> was called.</summary>
    bool IsEncrypted { get; }

    /// <summary>Completes when the receiver closes the socket or a read fails; the result describes why.</summary>
    Task<string> Closed { get; }

    /// <summary>Sends one request and waits for its response. Non-2xx responses are returned, not thrown.</summary>
    Task<RtspResponse> SendAsync(RtspRequest request, CancellationToken cancellationToken);

    /// <summary>Sends and throws <see cref="RtspStatusException"/> on a non-2xx status.</summary>
    Task<RtspResponse> SendExpectOkAsync(RtspRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Switches the socket to HAP framing. Must be called after the plaintext pairing response has been fully read and
    /// before the next request (the receiver activates encryption on its next read).
    /// </summary>
    void EnableEncryption(HapChannelKeys keys);
}

/// <summary>Connection options.</summary>
public sealed record RtspConnectionOptions
{
    /// <summary>TCP connect timeout. The SpeakerA needed up to 25 s on Wi-Fi once (SPEC-audio.md appendix A).</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>Per-request timeout for RTSP exchanges (pyatv: 4 s; pairing POSTs: 10 s).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Identity headers; random when null.</summary>
    public RtspClientIdentity? Identity { get; init; }

    /// <summary>Optional <c>X-Apple-Client-Name</c> value.</summary>
    public string? ClientName { get; init; }
}

/// <summary>Socket implementation of <see cref="IRtspConnection"/>.</summary>
public sealed class RtspConnection : IRtspConnection
{
    private readonly TaskCompletionSource<string> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private RtspConnection(IPEndPoint remote, IPAddress local, RtspClientIdentity identity, uint sessionId, ILogger logger)
    {
        RemoteEndPoint = remote;
        LocalAddress = local;
        Identity = identity;
        SessionId = sessionId;
        Logger = logger;
    }

    /// <inheritdoc />
    public IPEndPoint RemoteEndPoint { get; }

    /// <inheritdoc />
    public IPAddress LocalAddress { get; }

    /// <inheritdoc />
    public RtspClientIdentity Identity { get; }

    /// <inheritdoc />
    public uint SessionId { get; }

    /// <inheritdoc />
    public string SessionUri => LocalAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
        ? $"rtsp://[{LocalAddress}]/{SessionId}"
        : $"rtsp://{LocalAddress}/{SessionId}";

    /// <inheritdoc />
    public bool IsEncrypted { get; private set; }

    /// <inheritdoc />
    public Task<string> Closed => _closed.Task;

    private ILogger Logger { get; }

    /// <summary>Connects to <paramref name="remote"/>, choosing the local address the OS routes through.</summary>
    public static Task<RtspConnection> ConnectAsync(IPEndPoint remote, RtspConnectionOptions options, ILogger logger, CancellationToken cancellationToken) =>
        throw new NotImplementedException("TODO(impl: rtsp-transport)");

    /// <inheritdoc />
    public Task<RtspResponse> SendAsync(RtspRequest request, CancellationToken cancellationToken) =>
        throw new NotImplementedException("TODO(impl: rtsp-transport)");

    /// <inheritdoc />
    public async Task<RtspResponse> SendExpectOkAsync(RtspRequest request, CancellationToken cancellationToken)
    {
        var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.IsSuccess ? response : throw new RtspStatusException(request, response);
    }

    /// <inheritdoc />
    public void EnableEncryption(HapChannelKeys keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        IsEncrypted = true;
        throw new NotImplementedException("TODO(impl: rtsp-transport)");
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _closed.TrySetResult("disposed");
        return ValueTask.CompletedTask;
    }
}
