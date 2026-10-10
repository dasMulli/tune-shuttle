using System.Net;
using System.Net.Sockets;
using System.Text;
using Airplay2.Sender.Crypto;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Tlv8;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Airplay2.Sender.Tests.Pairing;

/// <summary>
/// The same pairing procedures over a real loopback TCP connection: the client is the library's
/// <see cref="RtspConnection"/>, the accessory a minimal socket server built on <see cref="RtspMessageCodec"/> and
/// <see cref="HapAccessoryPairing"/>.
/// </summary>
public sealed class LoopbackPairingProcedureTests : PairingProcedureTests
{
    private protected override async Task<IPairingLink> OpenAsync(HapAccessoryPairing accessory, ResponseTransform? transform = null)
    {
        var server = LoopbackAccessoryServer.Start(accessory, transform);
        var connection = await RtspConnection.ConnectAsync(
            server.EndPoint,
            new RtspConnectionOptions { RequestTimeout = TimeSpan.FromSeconds(10) },
            NullLogger.Instance,
            CancellationToken.None);
        return new Link(connection, server);
    }

    /// <summary>
    /// After transient pairing the client enables encryption; the plaintext M4 response is followed by an encrypted
    /// <c>GET /info</c> in both directions (SPEC-control.md 2.5: "the response to the pairing request is still plaintext").
    /// </summary>
    [Fact]
    public async Task Encrypted_get_info_after_transient_pairing()
    {
        var accessory = NewAccessory();
        await using var link = (Link)await OpenAsync(accessory);
        var session = await NewClient().PairTransientAsync(link.Connection, CancellationToken.None);
        link.Connection.EnableEncryption(session.ControlKeys);

        var response = await link.Connection.SendAsync(RtspRequest.Create("GET", "/info"), CancellationToken.None);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("Loopback Accessory", response.PlistBody()["name"]);
        Assert.True(link.Server.ServedEncrypted);
    }

    /// <summary>The same after PIN pairing and pair-verify on the same connection (owntone's back-to-back order).</summary>
    [Fact]
    public async Task Encrypted_get_info_after_pin_pairing_and_verify()
    {
        var accessory = NewAccessory(pin: "2468");
        await using var link = (Link)await OpenAsync(accessory);
        var client = NewClient();
        var started = await client.StartPinPairingAsync(link.Connection, ControllerIdentity.Generate(), TlvMethod.PairSetup, CancellationToken.None);
        var credentials = await client.FinishPinPairingAsync(started, "2468", CancellationToken.None);
        var session = await client.VerifyAsync(link.Connection, credentials, CancellationToken.None);
        link.Connection.EnableEncryption(session.ControlKeys);

        var response = await link.Connection.SendAsync(RtspRequest.Create("GET", "/info"), CancellationToken.None);
        Assert.Equal(200, response.StatusCode);
        Assert.True(link.Server.ServedEncrypted);
    }

    private sealed class Link(RtspConnection connection, LoopbackAccessoryServer server) : IPairingLink
    {
        public LoopbackAccessoryServer Server => server;

        public IRtspConnection Connection => connection;

        public IReadOnlyList<RecordedRequest> Requests => server.Requests;

        public IReadOnlyList<byte[]> ResponseBodies => server.ResponseBodies;

        public PairingSession? AccessorySession => server.AccessorySession;

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
            await server.DisposeAsync();
        }
    }
}

/// <summary>
/// A one-connection TCP accessory for the loopback tests: parses requests with <see cref="RtspMessageCodec"/>, answers
/// pairing POSTs through <see cref="HapAccessoryPairing"/> and <c>GET /info</c> with a small plist, and switches to
/// <see cref="HapFrameCipher"/> after the response that completed a procedure, as shairport-sync does [sps:rtsp.c:1810-1833].
/// </summary>
internal sealed class LoopbackAccessoryServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly HapAccessoryPairing _accessory;
    private readonly ResponseTransform? _transform;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly List<byte[]> _responseBodies = [];
    private Task _loop = Task.CompletedTask;

    private LoopbackAccessoryServer(HapAccessoryPairing accessory, ResponseTransform? transform)
    {
        _accessory = accessory;
        _transform = transform;
        _listener = new TcpListener(IPAddress.Loopback, 0);
    }

    public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public IReadOnlyList<byte[]> ResponseBodies
    {
        get
        {
            lock (_requests)
            {
                return [.. _responseBodies];
            }
        }
    }

    public PairingSession? AccessorySession { get; private set; }

    public bool ServedEncrypted { get; private set; }

    public static LoopbackAccessoryServer Start(HapAccessoryPairing accessory, ResponseTransform? transform)
    {
        var server = new LoopbackAccessoryServer(accessory, transform);
        server._listener.Start(1);
        server._loop = server.RunAsync(server._stop.Token);
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
            // The client side was disposed first; the test's own assertions decide the outcome.
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // An undecryptable frame after a deliberately broken exchange; not a teardown failure.
        }

        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
        var stream = client.GetStream();
        var receive = new byte[8192];
        var encrypted = new List<byte>();
        var plain = new List<byte>();
        HapFrameCipher? cipher = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var n = await stream.ReadAsync(receive, cancellationToken);
            if (n == 0)
            {
                return;
            }

            if (cipher is null)
            {
                plain.AddRange(receive.AsSpan(0, n));
            }
            else
            {
                encrypted.AddRange(receive.AsSpan(0, n));
                using var decrypted = new MemoryStream();
                var consumedFrames = cipher.DecryptFrames(encrypted.ToArray(), decrypted);
                encrypted.RemoveRange(0, consumedFrames);
                plain.AddRange(decrypted.ToArray());
            }

            while (RtspMessageCodec.TryParseRequest(plain.ToArray(), out var request, out var consumed))
            {
                plain.RemoveRange(0, consumed);
                var (bytes, session) = Handle(request!);
                if (cipher is not null)
                {
                    ServedEncrypted = true;
                    bytes = cipher.Encrypt(bytes);
                }

                await stream.WriteAsync(bytes, cancellationToken);
                if (session is not null)
                {
                    // The response that completed the procedure went out in plaintext; encrypt from the next message on.
                    AccessorySession = session;
                    cipher = new HapFrameCipher(HapAccessoryPairing.ControlKeysFor(session));
                }
            }
        }
    }

    private (byte[] Bytes, PairingSession? Session) Handle(RtspIncomingRequest request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [RtspHeaders.Server] = "AirTunes/366.0",
        };
        if (request.Header(RtspHeaders.CSeq) is { } cseq)
        {
            headers[RtspHeaders.CSeq] = cseq;
        }

        if (request.Method == "GET" && request.Uri == "/info")
        {
            headers[RtspHeaders.ContentType] = RtspContentTypes.BinaryPlist;
            var info = Airplay2.Sender.Plist.BinaryPlist.Encode(new Airplay2.Sender.Plist.PlistDictionary { ["name"] = "Loopback Accessory" });
            return (RtspMessageCodec.EncodeResponse(request.Protocol, 200, "OK", headers, info), null);
        }

        var body = request.Body.ToArray();
        lock (_requests)
        {
            var recorded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in request.Headers)
            {
                recorded[name] = value;
            }

            _requests.Add(new RecordedRequest(request.Method, request.Uri, recorded, body));
        }

        var answer = _accessory.HandleRequest(request.Uri, body);
        if (_transform is not null)
        {
            Tlv8Document? decoded = null;
            try
            {
                decoded = body.Length > 0 ? Tlv8Document.Decode(body) : null;
            }
            catch (FormatException)
            {
            }

            answer = _transform(request.Uri, decoded, answer);
        }

        lock (_requests)
        {
            _responseBodies.Add(answer.Body);
        }

        if (answer.Body.Length > 0)
        {
            headers[RtspHeaders.ContentType] = RtspContentTypes.OctetStream;
        }

        var reason = answer.Status switch
        {
            200 => "OK",
            404 => "Not Found",
            470 => "Connection Authorization Required",
            _ => "Error",
        };
        return (RtspMessageCodec.EncodeResponse("RTSP/1.0", answer.Status, reason, headers, answer.Body), answer.Session);
    }
}
