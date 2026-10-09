using System.Net;
using Airplay2.Sender.Crypto;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Tlv8;

namespace Airplay2.Sender.Tests.Pairing;

/// <summary>One pairing request as the accessory side saw it (method, path, headers incl. Content-Type, body).</summary>
internal sealed record RecordedRequest(string Method, string Uri, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    /// <summary>Case-insensitive header lookup.</summary>
    public string? Header(string name) =>
        Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>
/// Rewrites what the accessory answers before it reaches the client: (request path, decoded request or null, accessory
/// answer) → answer to send. Used to inject 470s, TLV errors and tampered M6 messages.
/// </summary>
internal delegate HapAccessoryResponse ResponseTransform(string path, Tlv8Document? request, HapAccessoryResponse response);

/// <summary>
/// In-memory <see cref="IRtspConnection"/> test double (WORK-PLAN.md "pairing" verify list): pipes each
/// <see cref="RtspRequest"/> straight to a <see cref="HapAccessoryPairing"/> and wraps its answer in an
/// <see cref="RtspResponse"/>, so the pairing tests need no socket and no rtsp-transport.
/// </summary>
internal sealed class InMemoryRtspConnection(HapAccessoryPairing accessory, ResponseTransform? transform = null) : IRtspConnection
{
    private readonly TaskCompletionSource<string> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<RecordedRequest> _requests = [];
    private int _cseq;

    /// <summary>The accessory state of this connection.</summary>
    public HapAccessoryPairing Accessory { get; } = accessory;

    /// <summary>Every request in order.</summary>
    public IReadOnlyList<RecordedRequest> Requests => _requests;

    /// <summary>Every raw response body in order (to check fragmentation of what the accessory sends).</summary>
    public List<byte[]> ResponseBodies { get; } = [];

    /// <summary>The session the accessory reported when a procedure completed.</summary>
    public PairingSession? AccessorySession { get; private set; }

    /// <summary>The keys passed to <see cref="EnableEncryption"/>.</summary>
    public HapChannelKeys? EnabledKeys { get; private set; }

    /// <inheritdoc />
    public IPEndPoint RemoteEndPoint { get; } = new(IPAddress.Loopback, 7000);

    /// <inheritdoc />
    public IPAddress LocalAddress => IPAddress.Loopback;

    /// <inheritdoc />
    public RtspClientIdentity Identity { get; } = RtspClientIdentity.CreateRandom();

    /// <inheritdoc />
    public uint SessionId { get; } = 1234567;

    /// <inheritdoc />
    public string SessionUri => $"rtsp://{LocalAddress}/{SessionId}";

    /// <inheritdoc />
    public bool IsEncrypted { get; private set; }

    /// <inheritdoc />
    public Task<string> Closed => _closed.Task;

    /// <inheritdoc />
    public Task<RtspResponse> SendAsync(RtspRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsEncrypted)
        {
            throw new InvalidOperationException("Pairing must not send after encryption was enabled.");
        }

        var cseq = _cseq++;
        var headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase)
        {
            [RtspHeaders.CSeq] = cseq.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [RtspHeaders.UserAgent] = Identity.UserAgent,
        };
        if (request.ContentType is not null)
        {
            headers[RtspHeaders.ContentType] = request.ContentType;
        }

        if (request.ContentType is not null || !request.Body.IsEmpty)
        {
            headers[RtspHeaders.ContentLength] = request.Body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var body = request.Body.ToArray();
        _requests.Add(new RecordedRequest(request.Method, request.Uri, headers, body));

        var answer = Accessory.HandleRequest(request.Uri, body);
        if (transform is not null)
        {
            Tlv8Document? decoded = null;
            try
            {
                decoded = body.Length > 0 ? Tlv8Document.Decode(body) : null;
            }
            catch (FormatException)
            {
            }

            answer = transform(request.Uri, decoded, answer);
        }

        if (answer.Session is not null)
        {
            AccessorySession = answer.Session;
        }

        ResponseBodies.Add(answer.Body);
        var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [RtspHeaders.CSeq] = headers[RtspHeaders.CSeq],
            [RtspHeaders.Server] = "AirTunes/366.0",
            [RtspHeaders.ContentLength] = answer.Body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (answer.Body.Length > 0)
        {
            responseHeaders[RtspHeaders.ContentType] = RtspContentTypes.OctetStream;
        }

        var reason = answer.Status switch
        {
            200 => "OK",
            404 => "Not Found",
            470 => "Connection Authorization Required",
            _ => "Error",
        };
        return Task.FromResult(new RtspResponse("RTSP/1.0", answer.Status, reason, responseHeaders, answer.Body));
    }

    /// <inheritdoc />
    public async Task<RtspResponse> SendExpectOkAsync(RtspRequest request, CancellationToken cancellationToken)
    {
        var response = await SendAsync(request, cancellationToken);
        return response.IsSuccess ? response : throw new RtspStatusException(request, response);
    }

    /// <inheritdoc />
    public void EnableEncryption(HapChannelKeys keys)
    {
        EnabledKeys = keys;
        IsEncrypted = true;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _closed.TrySetResult("disposed");
        return ValueTask.CompletedTask;
    }
}
