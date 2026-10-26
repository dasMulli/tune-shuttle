using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Airplay2.Receiver.Pairing;
using Airplay2.Receiver.Rtsp;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Session;
using Microsoft.Extensions.Logging;

namespace Airplay2.Receiver.Session;

/// <summary>
/// The state machine behind one control connection (SPEC-receiver.md 4.1-4.9): the pairing POSTs, <c>/info</c>, session
/// SETUP (event channel and PTP slave started before the answer), stream SETUP (the claim of R8), RECORD, SETPEERS,
/// FLUSH, SETRATEANCHORTIME, FLUSHBUFFERED, SET_PARAMETER/GET_PARAMETER, <c>/feedback</c> liveness (R16), TEARDOWN and the
/// end of the session. Request handlers and <see cref="EndAsync"/> are serialised with the control plane's one
/// <c>SemaphoreSlim</c>; a handler that finds the session ended answers 451 (4.9). Created by
/// <see cref="ReceiverControlPlane"/> for every accepted connection.
/// </summary>
public sealed class ControlSession : IAsyncDisposable, IEventChannelHost
{
    /// <summary>Size of the canned <c>/auth-setup</c> answer (public key, MFi certificate, signature; SPEC-receiver.md 4.2, SPEC-control.md 3.10).</summary>
    public const int AuthSetupResponseSize = 1076;

    /// <summary>The methods an OPTIONS answer lists (SPEC-receiver.md 4.8) [sps:rtsp.c:2284-2293].</summary>
    public const string PublicMethods = "ANNOUNCE, SETUP, RECORD, PAUSE, FLUSH, FLUSHBUFFERED, TEARDOWN, OPTIONS, POST, GET, PUT";

    private readonly ReceiverControlPlane _plane;
    private readonly ConnectionPairing _pairing;
    private readonly ILogger _logger;
    private readonly Lock _timerLock = new();
    private RtspServerConnection? _connection;
    private SessionState? _session;
    private IReceiverStream? _stream;
    private uint _streamId;
    private int? _lastSenderLevel;
    private Timer? _liveness;
    private bool _livenessArmed;
    private volatile bool _ended;

    internal ControlSession(ReceiverControlPlane plane, ConnectionPairing pairing, int id, ILogger logger)
    {
        _plane = plane;
        _pairing = pairing;
        _logger = logger;
        Id = id;
    }

    /// <summary>The connection number (1-based, per control plane), for logs.</summary>
    public int Id { get; }

    /// <summary>The control connection.</summary>
    public RtspServerConnection Connection => _connection ?? throw new InvalidOperationException("The control session has not started.");

    /// <summary>The sender after its session SETUP (SPEC-receiver.md 4.3), or null.</summary>
    public SenderInfo? Sender => Volatile.Read(ref _session)?.Sender;

    /// <summary>True once the connection completed a PTP session SETUP (SPEC-receiver.md 4.1 "the session connection"); a remote-control-only session (<c>None</c>, RQ9) is not one.</summary>
    public bool IsSessionConnection => Volatile.Read(ref _session)?.Sender is not null && !_ended;

    /// <summary>True while a stream is set up (SPEC-receiver.md 4.6: status flag 17, the output claimed).</summary>
    public bool HasStream => Volatile.Read(ref _stream) is not null;

    /// <summary>The <c>streamID</c> of the current stream, or null without one (SPEC-receiver.md 4.6).</summary>
    public uint? StreamId => HasStream ? _streamId : null;

    /// <summary>True once the connection completed transient pair-setup or pair-verify (SPEC-receiver.md 4.1, 4.2).</summary>
    public bool IsPaired => _pairing.Session is not null;

    /// <summary>True once the session ended (SPEC-receiver.md 4.9); every further request is answered 451.</summary>
    public bool IsEnded => _ended;

    private IControlPlaneHost Host => _plane.Host;

    /// <summary>The <see cref="RtspRequestHandler"/> for this connection: dispatches one request per SPEC-receiver.md 4.1-4.9.</summary>
    public async ValueTask<RtspServerResponse> HandleAsync(RtspServerConnection connection, RtspIncomingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);
        Touch(); // R16: every request refreshes the liveness timer.
        await _plane.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ended)
            {
                _logger.LogDebug("Receiver connection #{Id}: {Method} {Uri} after the session ended; answering 451", Id, request.Method, request.Uri);
                return RtspServerResponse.Status(RtspStatus.Unavailable);
            }

            return await DispatchAsync(connection, request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _plane.Gate.Release();
        }
    }

    /// <summary>
    /// Ends the session (SPEC-receiver.md 4.9): stream, event channel, PTP slave, output released, <c>SenderGone(reason)</c>,
    /// and closes the connection. Runs for a closed connection, the liveness timer (R16), the host's request (R8) and the
    /// receiver's stop; a no-op when already ended. Must not be called from a request handler (TEARDOWN ends the session
    /// inside its handler).
    /// </summary>
    public async Task EndAsync(string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reason);
        Task closing;
        await _plane.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            closing = await EndCoreAsync(reason, closeConnection: true).ConfigureAwait(false);
        }
        finally
        {
            _plane.Gate.Release();
        }

        await closing.ConfigureAwait(false);
    }

    /// <summary>Ends the session when it is still running (<see cref="EndAsync"/> with the reason "disposed").</summary>
    public async ValueTask DisposeAsync() => await EndAsync("disposed", CancellationToken.None).ConfigureAwait(false);

    /// <inheritdoc />
    PlistDictionary IEventChannelHost.BuildUpdateInfo(bool forceVolume) =>
        Host.BuildInfo(Normalize(Connection.RemoteEndPoint), includeTxt: true, _plane.HasActiveStream);

    /// <inheritdoc />
    void IEventChannelHost.Track(Task task, string what) => _plane.Track(task, what);

    /// <summary>Starts serving an accepted socket (SPEC-receiver.md 4.1); a closed connection ends the session at once (R16).</summary>
    internal void Start(Socket socket)
    {
        var options = _plane.Options.Connection with { Name = $"{_plane.Options.Name} control #{Id}" };
        var connection = RtspServerConnection.Start(socket, HandleAsync, options, _logger);
        _connection = connection;
        _plane.Track(EndWhenClosedAsync(connection), $"control connection #{Id} close");
    }

    /// <summary>
    /// The end of the session under the gate (SPEC-receiver.md 4.9). Returns the connection close when
    /// <paramref name="closeConnection"/>, which the caller awaits only after releasing the gate.
    /// </summary>
    internal async Task<Task> EndCoreAsync(string reason, bool closeConnection)
    {
        if (_ended)
        {
            return Task.CompletedTask;
        }

        _ended = true;
        var hadSession = _session?.Sender is not null;
        await StopStreamCoreAsync().ConfigureAwait(false);
        await DisposeSessionStateAsync().ConfigureAwait(false);
        _liveness?.Dispose();
        _plane.Remove(this);
        if (hadSession)
        {
            _logger.LogInformation("Receiver connection #{Id}: session ended: {Reason}", Id, reason);
            Host.Publish(new ReceiverEvent.SenderGone(reason));
        }

        return closeConnection && _connection is { } connection ? connection.DisposeAsync().AsTask() : Task.CompletedTask;
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static IPEndPoint Normalize(IPEndPoint endpoint) => new(Normalize(endpoint.Address), endpoint.Port);

    private static string Path(string uri)
    {
        if (uri.StartsWith('/'))
        {
            var query = uri.IndexOfAny(['?', '#']);
            return query >= 0 ? uri[..query] : uri;
        }

        return uri;
    }

    private static ulong? GetUInt64(PlistDictionary body, string key) => body.TryGetValue(key, out var value) ? value switch
    {
        ulong u => u,
        long l => unchecked((ulong)l),
        int i => unchecked((ulong)i),
        uint u32 => u32,
        double d when d >= 0 => (ulong)d,
        _ => null,
    } : null;

    private static PlistDictionary? TryPlist(RtspIncomingRequest request)
    {
        if (request.Body.IsEmpty)
        {
            return null;
        }

        try
        {
            return request.PlistBody();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private async Task EndWhenClosedAsync(RtspServerConnection connection)
    {
        var reason = await connection.Closed.ConfigureAwait(false);
        await EndAsync($"connection closed ({reason})", CancellationToken.None).ConfigureAwait(false);
    }

    private async ValueTask<RtspServerResponse> DispatchAsync(RtspServerConnection connection, RtspIncomingRequest request, CancellationToken cancellationToken)
    {
        var method = request.Method.ToUpperInvariant();
        var path = Path(request.Uri);
        switch (method)
        {
            case "GET" or "POST" when path == "/info":
                return Info(connection, request);
            case "OPTIONS":
                return RtspServerResponse.Ok() with { Headers = new Dictionary<string, string> { [RtspHeaders.Public] = PublicMethods } };
            case "POST" when ConnectionPairing.Handles(path):
                return (await _pairing.HandleAsync(connection, request).ConfigureAwait(false)).Response;
            case "POST" when path == "/fp-setup":
                return FpSetup(request);
            case "POST" when path == "/auth-setup":
                // SPEC-receiver.md 4.2: as the fake receiver, 1076 zero bytes (SPEC-control.md 3.10).
                return RtspServerResponse.Bytes(RtspContentTypes.OctetStream, new byte[AuthSetupResponseSize]);
        }

        if (!IsPaired)
        {
            // SPEC-receiver.md 4.1: everything else needs a paired connection (the fake receiver's RequirePairing rule).
            _logger.LogWarning("Receiver connection #{Id}: {Method} {Uri} before pairing; answering 470", Id, request.Method, request.Uri);
            return RtspServerResponse.Status(RtspStatus.ConnectionAuthorizationRequired);
        }

        switch (method)
        {
            case "SETUP":
                return await SetupAsync(connection, request, cancellationToken).ConfigureAwait(false);
            case "RECORD":
                // SPEC-receiver.md 4.5 [sps:rtsp.c:903-911; ap2r:ap2-receiver.py:717-732].
                return RtspServerResponse.Ok() with { Headers = new Dictionary<string, string> { [RtspHeaders.AudioLatency] = "0" } };
            case "SETPEERS":
            case "SETPEERSX":
                return SetPeers(request);
            case "FLUSH":
                return Flush(request);
            case "SETRATEANCHORTIME":
            case "SETRATEANCHORTI": // shairport registers the name truncated to 15 characters [sps:rtsp.c:3703]
                return SetRateAnchorTime(request);
            case "FLUSHBUFFERED":
                return FlushBuffered(request);
            case "SET_PARAMETER":
                return await SetParameterAsync(request, cancellationToken).ConfigureAwait(false);
            case "GET_PARAMETER":
                return await GetParameterAsync(request, cancellationToken).ConfigureAwait(false);
            case "TEARDOWN":
                return await TeardownAsync(request).ConfigureAwait(false);
            case "SETMAGICCOOKIE":
                return RtspServerResponse.Ok();
            case "POST" when path == "/feedback":
                return Feedback();
            case "POST" when path == "/command":
                // Bit 50 is not advertised (SPEC-receiver.md R10, 4.8): accepted and ignored.
                _logger.LogDebug("Receiver connection #{Id}: POST /command with {Length} bytes ignored", Id, request.Body.Length);
                return RtspServerResponse.Ok();
            case "POST" when path == "/audioMode":
            case "PUT" when path == "/setProperty":
                return RtspServerResponse.Ok();
            case "GET" or "POST" or "PUT":
                _logger.LogWarning("Receiver connection #{Id}: unknown endpoint {Method} {Uri}; answering 404", Id, request.Method, request.Uri);
                return RtspServerResponse.Status(RtspStatus.NotFound);
            default:
                _logger.LogWarning("Receiver connection #{Id}: unsupported method {Method} {Uri}; answering 501", Id, request.Method, request.Uri);
                return RtspServerResponse.Status(RtspStatus.NotImplemented);
        }
    }

    /// <summary><c>GET /info</c> and the stage-1 <c>POST /info</c> with a <c>qualifier [txtAirPlay]</c> (SPEC-receiver.md 3.2).</summary>
    private RtspServerResponse Info(RtspServerConnection connection, RtspIncomingRequest request)
    {
        var withTxt = TryPlist(request)?.GetArray("qualifier")?.Contains("txtAirPlay") == true;
        return RtspServerResponse.Plist(Host.BuildInfo(Normalize(connection.RemoteEndPoint), withTxt, _plane.HasActiveStream));
    }

    /// <summary><c>POST /fp-setup</c> (SPEC-receiver.md R14): the canned reply selected by the mode byte.</summary>
    private RtspServerResponse FpSetup(RtspIncomingRequest request)
    {
        var reply = FairPlaySetup.Reply(request.Body.Span, out var unsupported);
        if (unsupported)
        {
            _logger.LogDebug("Receiver connection #{Id}: /fp-setup with an unsupported FairPlay version or type", Id);
        }

        if (reply is null)
        {
            // shairport-sync answers 200 with no body when it cannot build a reply [sps:rtsp.c:1946-1951].
            _logger.LogWarning("Receiver connection #{Id}: cannot answer /fp-setup ({Length} bytes)", Id, request.Body.Length);
            return RtspServerResponse.Ok() with { ContentType = RtspContentTypes.OctetStream };
        }

        return RtspServerResponse.Bytes(RtspContentTypes.OctetStream, reply);
    }

    private async ValueTask<RtspServerResponse> SetupAsync(RtspServerConnection connection, RtspIncomingRequest request, CancellationToken cancellationToken)
    {
        PlistDictionary body;
        try
        {
            body = request.PlistBody();
        }
        catch (FormatException ex)
        {
            _logger.LogWarning("Receiver connection #{Id}: SETUP without a bplist body: {Error}", Id, ex.Message);
            return RtspServerResponse.Status(RtspStatus.BadRequest);
        }

        // The two SETUPs are told apart by the presence of "streams" [sps:rtsp.c:2407-2415; ap2r:ap2-receiver.py:579-595].
        return body.ContainsKey("streams")
            ? await StreamSetupAsync(connection, body, cancellationToken).ConfigureAwait(false)
            : await SessionSetupAsync(connection, body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Session SETUP (SPEC-receiver.md 4.3): PTP accepted, NTP 501, <c>None</c> with <c>isRemoteControlOnly</c> a bare event channel, anything else 400.</summary>
    private async ValueTask<RtspServerResponse> SessionSetupAsync(RtspServerConnection connection, PlistDictionary body, CancellationToken cancellationToken)
    {
        var timingProtocol = body.GetString("timingProtocol");
        var remoteControlOnly = timingProtocol == "None" && body.GetBool("isRemoteControlOnly") == true;
        switch (timingProtocol)
        {
            case "NTP":
                _logger.LogWarning("Receiver connection #{Id}: refusing an NTP session SETUP (R1); answering 501", Id);
                return RtspServerResponse.Status(RtspStatus.NotImplemented);
            case "PTP":
                break;
            case "None" when remoteControlOnly:
                break;
            default:
                _logger.LogWarning("Receiver connection #{Id}: session SETUP with timingProtocol {Protocol}; answering 400", Id, timingProtocol ?? "(none)");
                return RtspServerResponse.Status(RtspStatus.BadRequest);
        }

        if (_session is not null)
        {
            // A second session SETUP on the same connection replaces the first (the fake receiver's behaviour).
            await StopStreamCoreAsync().ConfigureAwait(false);
            await DisposeSessionStateAsync().ConfigureAwait(false);
        }

        var local = Normalize(connection.LocalEndPoint.Address);
        var keys = _pairing.Session is { } paired ? ReceiverPairing.EventKeysFor(paired) : null;
        var eventOptions = new EventChannelServerOptions
        {
            BindAddress = local,
            ServerHeader = _plane.Options.Connection.ServerHeader,
            Name = $"{_plane.Options.Name} events #{Id}",
        };
        var events = new EventChannelServer(this, eventOptions, keys, Id, _logger);
        events.Start(); // listening before the answer is written [sps:rtsp.c:2626-2638]
        var answer = new PlistDictionary
        {
            ["eventPort"] = (long)events.Port,
            ["timingPort"] = 0L, // shairport and airplay2-receiver send a dummy 0 [sps:rtsp.c:2654; ap2r:ap2-receiver.py:220]
        };

        if (remoteControlOnly)
        {
            // RQ9: accepted and ignored; not a session connection.
            _session = new SessionState(null, events, null);
            _logger.LogInformation("Receiver connection #{Id}: remote-control-only session SETUP accepted (RQ9)", Id);
            return RtspServerResponse.Plist(answer);
        }

        var remote = Normalize(connection.RemoteEndPoint.Address);
        var peer = ReadTimingPeer(body, remote);
        ISenderTiming? timing = null;
        try
        {
            timing = await Host.StartTimingAsync(peer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Receiver connection #{Id}: the PTP slave did not start; anchor-only timing (R6)", Id);
        }

        var sender = new SenderInfo(body.GetString("name"), body.GetString("model"), body.GetString("deviceID"), remote);
        _session = new SessionState(sender, events, timing);
        _logger.LogInformation(
            "Receiver connection #{Id}: PTP session SETUP from {Sender} ({Model}), clock {ClockId}, group {Group}",
            Id,
            sender.Name ?? "(no name)",
            sender.Model ?? "(no model)",
            peer.ClockId?.ToString("X16", CultureInfo.InvariantCulture) ?? "none",
            body.GetString("groupUUID") ?? "none");
        Host.Publish(new ReceiverEvent.SenderConnected(sender));

        var addresses = LocalAddresses(local).Select(a => (object?)a.ToString()).ToList();
        answer["timingPeerInfo"] = new PlistDictionary
        {
            ["Addresses"] = addresses,
            ["ID"] = local.ToString(),
        };
        return RtspServerResponse.Plist(answer);
    }

    private static SenderTimingPeer ReadTimingPeer(PlistDictionary body, IPAddress remote)
    {
        var info = body.GetDictionary("timingPeerInfo");
        var addresses = new List<IPAddress>();
        if (info?.GetArray("Addresses") is { } list)
        {
            foreach (var entry in list)
            {
                if (entry is string text && IPAddress.TryParse(text, out var address))
                {
                    addresses.Add(Normalize(address));
                }
            }
        }

        if (addresses.Count == 0)
        {
            addresses.Add(remote);
        }

        var clockId = info is null ? null : GetUInt64(info, "ClockID");
        return new SenderTimingPeer(addresses, clockId, remote);
    }

    /// <summary>Our addresses for <c>timingPeerInfo.Addresses</c>: the connection's local address first (SPEC-receiver.md 4.3), then the other unicast addresses of that family when it is not loopback.</summary>
    private static List<IPAddress> LocalAddresses(IPAddress local)
    {
        var result = new List<IPAddress> { local };
        if (IPAddress.IsLoopback(local))
        {
            return result;
        }

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily == local.AddressFamily && !IPAddress.IsLoopback(address) && !result.Contains(address))
                    {
                        result.Add(address);
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // the local address alone is enough
        }

        return result;
    }

    /// <summary>Stream SETUP (SPEC-receiver.md 4.6): validated, the older session evicted (R8), the sockets bound, the claim raised.</summary>
    private async ValueTask<RtspServerResponse> StreamSetupAsync(RtspServerConnection connection, PlistDictionary body, CancellationToken cancellationToken)
    {
        if (_session?.Sender is not { } sender)
        {
            _logger.LogWarning("Receiver connection #{Id}: stream SETUP before a PTP session SETUP; answering 451", Id);
            return RtspServerResponse.Status(RtspStatus.Unavailable);
        }

        if (!connection.IsEncrypted)
        {
            // shk travels inside the body: refuse it on a plaintext channel (SPEC-receiver.md 4.6).
            _logger.LogWarning("Receiver connection #{Id}: stream SETUP on an unencrypted connection; answering 470", Id);
            return RtspServerResponse.Status(RtspStatus.ConnectionAuthorizationRequired);
        }

        if (body.GetArray("streams") is not [PlistDictionary stream, ..])
        {
            return RtspServerResponse.Status(RtspStatus.BadRequest);
        }

        var key = stream.GetData("shk");
        if (key is not { Length: 32 })
        {
            _logger.LogWarning("Receiver connection #{Id}: stream SETUP shk has {Length} bytes, not 32; answering 400", Id, key?.Length ?? 0);
            return RtspServerResponse.Status(RtspStatus.BadRequest);
        }

        var type = stream.GetInt64("type");
        var audioFormat = GetUInt64(stream, "audioFormat") ?? 0;
        string format;
        switch (type)
        {
            case (long)StreamType.Realtime:
                var spf = stream.GetInt64("spf");
                var sr = stream.GetInt64("sr");
                if (spf is not null and not 352 || sr is not null and not 44100)
                {
                    _logger.LogWarning("Receiver connection #{Id}: realtime stream SETUP with spf {Spf} and sr {Sr}; answering 400", Id, spf, sr);
                    return RtspServerResponse.Status(RtspStatus.BadRequest);
                }

                format = ReceiverStreamFormats.AlacName;
                audioFormat = ReceiverStreamFormats.Alac;
                break;
            case (long)StreamType.Buffered:
                if (ReceiverStreamFormats.BufferedFormatName(audioFormat, Host.AacAvailable) is not { } name)
                {
                    _logger.LogWarning(
                        "Receiver connection #{Id}: buffered stream SETUP with audioFormat 0x{Format:X} (ct {Ct}); supported 0x{Supported:X}; answering 400",
                        Id,
                        audioFormat,
                        stream.GetInt64("ct"),
                        Discovery.ReceiverFeatures.BufferStreamFormatsFor(Host.AacAvailable));
                    return RtspServerResponse.Status(RtspStatus.BadRequest);
                }

                format = name;
                break;
            default:
                _logger.LogWarning("Receiver connection #{Id}: stream SETUP with type {Type}; answering 400", Id, type);
                return RtspServerResponse.Status(RtspStatus.BadRequest);
        }

        var streamType = (StreamType)(int)type;

        // R8: a second sender's stream SETUP ends the first sender's session before this one claims the output.
        await _plane.EvictOthersAsync(this, $"another sender ({sender.Name ?? sender.Address.ToString()}) started a stream").ConfigureAwait(false);
        if (_stream is not null)
        {
            // A new stream on the same session replaces the old one.
            await StopStreamCoreAsync().ConfigureAwait(false);
        }

        var senderControl = new IPEndPoint(sender.Address, (int)(stream.GetInt64("controlPort") ?? 0));
        var streamRequest = new ReceiverStreamRequest(streamType, key, audioFormat, format, senderControl, Normalize(connection.LocalEndPoint.Address), stream);
        IReceiverStream created;
        try
        {
            created = await Host.CreateStreamAsync(streamRequest, _session.Timing, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Receiver connection #{Id}: binding the {Type} stream failed; answering 500", Id, (int)streamType);
            return RtspServerResponse.Status(RtspStatus.InternalError);
        }

        _streamId = ReceiverControlPlane.NextStreamId();
        Volatile.Write(ref _stream, created);
        ArmLiveness();
        _logger.LogInformation("Receiver connection #{Id}: stream {StreamId} set up: type {Type}, {Format}, data port {DataPort}, control port {ControlPort}", Id, _streamId, (int)streamType, format, created.DataPort, created.ControlPort);
        Host.Publish(new ReceiverEvent.SenderStreamStarted(sender, streamType, format));

        var answer = new PlistDictionary
        {
            ["type"] = (long)(int)streamType,
            ["dataPort"] = (long)created.DataPort,
            ["controlPort"] = (long)created.ControlPort,
            ["streamID"] = (long)_streamId,
        };
        if (streamType == StreamType.Buffered)
        {
            answer["audioBufferSize"] = created.AudioBufferSize ?? 8388608L;
        }

        return RtspServerResponse.Plist(new PlistDictionary { ["streams"] = new List<object?> { answer } });
    }

    /// <summary><c>SETPEERS</c> (address strings) and <c>SETPEERSX</c> (dictionaries with <c>Addresses</c>): the slave's accepted sources (SPEC-receiver.md 4.5).</summary>
    private RtspServerResponse SetPeers(RtspIncomingRequest request)
    {
        var addresses = new List<IPAddress>();
        try
        {
            var span = request.Body.Span;
            var start = span.IndexOf("bplist00"u8);
            if (start >= 0 && BinaryPlist.Decode(span[start..]) is List<object?> list)
            {
                foreach (var entry in list)
                {
                    switch (entry)
                    {
                        case string text when IPAddress.TryParse(text, out var address):
                            addresses.Add(Normalize(address));
                            break;
                        case PlistDictionary peer when peer.GetArray("Addresses") is { } peerAddresses:
                            foreach (var item in peerAddresses)
                            {
                                if (item is string s && IPAddress.TryParse(s, out var a))
                                {
                                    addresses.Add(Normalize(a));
                                }
                            }

                            break;
                    }
                }
            }
        }
        catch (FormatException ex)
        {
            _logger.LogWarning("Receiver connection #{Id}: {Method} body is not a bplist array: {Error}", Id, request.Method, ex.Message);
        }

        _logger.LogDebug("Receiver connection #{Id}: {Method} with {Count} addresses", Id, request.Method, addresses.Count);
        if (addresses.Count > 0)
        {
            _session?.Timing?.AddPeers(addresses);
        }

        return RtspServerResponse.Ok();
    }

    /// <summary><c>FLUSH</c> (realtime; SPEC-receiver.md 4.5): every buffered frame dropped, the output flushed.</summary>
    private RtspServerResponse Flush(RtspIncomingRequest request)
    {
        var (seq, rtpTime) = RequestParsing.ParseRtpInfo(request.Header(RtspHeaders.RtpInfo));
        if (_stream is { } stream)
        {
            stream.Flush(seq);
            Host.Publish(new ReceiverEvent.SenderFlush(Deferred: false));
        }

        _logger.LogDebug("Receiver connection #{Id}: FLUSH seq {Seq} rtptime {RtpTime}", Id, seq, rtpTime);
        return RtspServerResponse.Ok();
    }

    /// <summary><c>SETRATEANCHORTIME</c> (SPEC-receiver.md 4.7, SPEC-audio.md 4.3): rate 1 anchors and resumes, rate 0 pauses.</summary>
    private RtspServerResponse SetRateAnchorTime(RtspIncomingRequest request)
    {
        if (TryPlist(request) is not { } body)
        {
            _logger.LogWarning("Receiver connection #{Id}: SETRATEANCHORTIME without a bplist body; answering 400", Id);
            return RtspServerResponse.Status(RtspStatus.BadRequest);
        }

        var rate = body.GetDouble("rate") ?? 0;
        if (_stream is not { } stream)
        {
            _logger.LogDebug("Receiver connection #{Id}: SETRATEANCHORTIME rate {Rate} without a stream", Id, rate);
            return RtspServerResponse.Ok();
        }

        if (rate > 0)
        {
            if (body.GetInt64("rtpTime") is { } rtpTime && GetUInt64(body, "networkTimeSecs") is { } secs)
            {
                var frac = GetUInt64(body, "networkTimeFrac") ?? 0;
                var nanos = unchecked((secs * 1_000_000_000UL) + (ulong)(((UInt128)frac * 1_000_000_000UL) >> 64));
                var timeline = GetUInt64(body, "networkTimeTimelineID") ?? 0;
                stream.SetAnchor(unchecked((uint)rtpTime), nanos, timeline);
            }

            stream.Resume();
            Host.Publish(new ReceiverEvent.SenderResumed());
        }
        else
        {
            stream.Pause();
            Host.Publish(new ReceiverEvent.SenderPaused());
        }

        return RtspServerResponse.Ok();
    }

    /// <summary><c>FLUSHBUFFERED</c> (SPEC-receiver.md 4.7): immediate (flush and pause) or deferred (splice).</summary>
    private RtspServerResponse FlushBuffered(RtspIncomingRequest request)
    {
        if (TryPlist(request) is not { } body || body.GetInt64("flushUntilSeq") is not { } until)
        {
            _logger.LogWarning("Receiver connection #{Id}: FLUSHBUFFERED without flushUntilSeq; answering 400", Id);
            return RtspServerResponse.Status(RtspStatus.BadRequest);
        }

        if (_stream is not { } stream)
        {
            return RtspServerResponse.Ok();
        }

        if (body.GetInt64("flushFromSeq") is { } from)
        {
            stream.Splice(unchecked((uint)from), unchecked((uint)until));
            Host.Publish(new ReceiverEvent.SenderFlush(Deferred: true));
        }
        else
        {
            stream.FlushBuffered(unchecked((uint)until));
            Host.Publish(new ReceiverEvent.SenderFlush(Deferred: false));
        }

        return RtspServerResponse.Ok();
    }

    /// <summary><c>SET_PARAMETER</c> by content type (SPEC-receiver.md 4.8): volume, progress, DMAP, artwork; always 200.</summary>
    private async ValueTask<RtspServerResponse> SetParameterAsync(RtspIncomingRequest request, CancellationToken cancellationToken)
    {
        var contentType = request.ContentType ?? string.Empty;
        var rtpTime = RequestParsing.ParseRtpInfo(request.Header(RtspHeaders.RtpInfo)).RtpTime;
        if (contentType.StartsWith(RtspContentTypes.Parameters, StringComparison.OrdinalIgnoreCase))
        {
            // The parsing is the dmap-and-volume helpers' (ReceiverParameters, SPEC-receiver.md 4.8, R9).
            var text = Encoding.UTF8.GetString(request.Body.Span);
            var handled = false;
            if (ReceiverParameters.ParseVolume(text) is { } db)
            {
                await SetVolumeAsync(db, cancellationToken).ConfigureAwait(false);
                handled = true;
            }

            if (ReceiverParameters.ParseProgress(text, rtpTime) is { } progress)
            {
                Host.Publish(new ReceiverEvent.SenderProgress(progress));
                handled = true;
            }

            if (!handled)
            {
                _logger.LogDebug("Receiver connection #{Id}: unknown text parameters {Text}", Id, text.Trim());
            }
        }
        else if (contentType.StartsWith(RtspContentTypes.DmapTagged, StringComparison.OrdinalIgnoreCase))
        {
            if (DmapDecoder.TryDecode(request.Body.Span, out var item))
            {
                Host.Publish(new ReceiverEvent.SenderMetadata(item, rtpTime ?? 0));
            }
            else
            {
                _logger.LogWarning("Receiver connection #{Id}: malformed DMAP metadata ({Length} bytes) dropped", Id, request.Body.Length);
            }
        }
        else if (ReceiverParameters.ArtworkContentType(contentType) is { } imageType)
        {
            if (ReceiverParameters.IsArtworkWithinLimit(request.Body.Length))
            {
                Host.Publish(new ReceiverEvent.SenderArtwork(request.Body.ToArray(), imageType, rtpTime ?? 0));
            }
            else
            {
                _logger.LogWarning("Receiver connection #{Id}: artwork of {Length} bytes dropped (1 to {Max} bytes are kept)", Id, request.Body.Length, ReceiverParameters.MaxArtworkBytes);
            }
        }
        else
        {
            // A receiver that does not want a metadata kind drops it (SPEC-control.md 5).
            _logger.LogDebug("Receiver connection #{Id}: SET_PARAMETER with content type {ContentType} ignored", Id, contentType);
        }

        return RtspServerResponse.Ok();
    }

    /// <summary>R9: the dB value mapped with <see cref="VolumeMapping.DbToLevel"/> to the output's master level; <c>SenderVolume</c> raised.</summary>
    private async Task SetVolumeAsync(double db, CancellationToken cancellationToken)
    {
        var level = VolumeMapping.DbToLevel(db);
        _lastSenderLevel = level;
        try
        {
            await Host.Output.SetMasterLevelAsync(level, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Receiver connection #{Id}: setting the master level {Level} failed", Id, level);
        }

        Host.Publish(new ReceiverEvent.SenderVolume(level, db));
    }

    /// <summary><c>GET_PARAMETER volume</c> (SPEC-receiver.md 4.8, R9): the current G, else the sender's last level, else 50.</summary>
    private async ValueTask<RtspServerResponse> GetParameterAsync(RtspIncomingRequest request, CancellationToken cancellationToken)
    {
        var text = Encoding.UTF8.GetString(request.Body.Span).Trim();
        if (!text.StartsWith("volume", StringComparison.OrdinalIgnoreCase))
        {
            return RtspServerResponse.Ok(); // shairport answers anything else with 200 and no body [sps:rtsp.c:3318-3338]
        }

        var outputLevel = await Host.GetOutputLevelAsync(cancellationToken).ConfigureAwait(false);
        var body = ReceiverParameters.FormatVolumeAnswer(outputLevel, _lastSenderLevel);
        return RtspServerResponse.Bytes(RtspContentTypes.Parameters, Encoding.ASCII.GetBytes(body));
    }

    /// <summary><c>POST /feedback</c> (SPEC-receiver.md 4.8, R16): the stream's type and rate while one is set up, else an empty body.</summary>
    private RtspServerResponse Feedback()
    {
        if (_stream is not { } stream)
        {
            return RtspServerResponse.Ok();
        }

        return RtspServerResponse.Plist(new PlistDictionary
        {
            ["streams"] = new List<object?> { new PlistDictionary { ["type"] = (long)(int)stream.Type, ["sr"] = 44100.0 } },
        });
    }

    /// <summary>
    /// <c>TEARDOWN</c> (SPEC-receiver.md 4.9): <c>{streams: [...]}</c> ends the stream and keeps the session; <c>{}</c> and a
    /// body-less TEARDOWN (a deliberate deviation from shairport-sync's no-op) end the session, answered with
    /// <c>Connection: close</c> before the connection is closed.
    /// </summary>
    private async ValueTask<RtspServerResponse> TeardownAsync(RtspIncomingRequest request)
    {
        var body = TryPlist(request);
        if (body?.GetArray("streams") is { Count: > 0 } streams)
        {
            var ids = streams.OfType<PlistDictionary>().Select(s => s.GetInt64("streamID")).ToList();
            if (_stream is not null && (ids.Count == 0 || ids.Any(id => id is null || id == _streamId)))
            {
                _logger.LogInformation("Receiver connection #{Id}: TEARDOWN of stream {StreamId}", Id, _streamId);
                await StopStreamCoreAsync().ConfigureAwait(false);
            }

            return RtspServerResponse.Ok();
        }

        await EndCoreAsync(body is null ? "TEARDOWN without a body" : "TEARDOWN", closeConnection: false).ConfigureAwait(false);
        return RtspServerResponse.Ok() with
        {
            Headers = new Dictionary<string, string> { [RtspHeaders.Connection] = "close" },
            CloseAfter = true,
        };
    }

    /// <summary>Ends the stream (SPEC-receiver.md 4.9): sockets closed, buffer dropped, output released, status flag 17 cleared.</summary>
    private async Task StopStreamCoreAsync()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream is null)
        {
            return;
        }

        DisarmLiveness();
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Receiver connection #{Id}: closing stream {StreamId} failed", Id, _streamId);
        }

        try
        {
            await Host.Output.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Receiver connection #{Id}: releasing the output failed", Id);
        }
    }

    private async Task DisposeSessionStateAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null)
        {
            return;
        }

        try
        {
            await session.Events.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Receiver connection #{Id}: closing the event channel failed", Id);
        }

        if (session.Timing is { } timing)
        {
            try
            {
                await timing.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Receiver connection #{Id}: stopping the PTP slave failed", Id);
            }
        }
    }

    /// <summary>R16: from a stream SETUP on, a sender that sends nothing for <see cref="AirplayReceiverOptions.SessionTimeout"/> is gone.</summary>
    private void ArmLiveness()
    {
        lock (_timerLock)
        {
            if (_liveness is null)
            {
                // Created inside a handler: do not let the handler's async-local state (RtspServerConnection's "handler
                // running" marker) flow into the timer callback, which ends the session from outside any handler.
                using (ExecutionContext.SuppressFlow())
                {
                    _liveness = new Timer(static state => ((ControlSession)state!).OnLivenessExpired(), this, Timeout.Infinite, Timeout.Infinite);
                }
            }

            _livenessArmed = true;
            _liveness.Change(_plane.Options.SessionTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    private void DisarmLiveness()
    {
        lock (_timerLock)
        {
            _livenessArmed = false;
            _liveness?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private void Touch()
    {
        lock (_timerLock)
        {
            if (_livenessArmed && !_ended)
            {
                _liveness?.Change(_plane.Options.SessionTimeout, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnLivenessExpired()
    {
        lock (_timerLock)
        {
            if (!_livenessArmed || _ended)
            {
                return;
            }
        }

        var seconds = _plane.Options.SessionTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        _plane.Track(EndAsync($"no request on the control connection for {seconds} s (R16)", CancellationToken.None), $"control connection #{Id} liveness end");
    }

    /// <summary>The session-level state after a session SETUP (SPEC-receiver.md 4.3): the sender (null for a remote-control-only session), its event channel and PTP side.</summary>
    private sealed record SessionState(SenderInfo? Sender, EventChannelServer Events, ISenderTiming? Timing);
}
