using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Airplay2.Receiver.Pairing;
using Airplay2.Receiver.Rtsp;
using Airplay2.Receiver.Session;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtsp;
using Microsoft.Extensions.Logging;

namespace Airplay2.FakeReceiver;

/// <content>The control connection: request dispatch and the RTSP/HTTP handlers (SPEC-control.md 3-5, receiver side).</content>
public sealed partial class FakeReceiver
{
    /// <summary>Size of the canned <c>/auth-setup</c> answer (public key, MFi certificate, signature; SPEC-control.md 3.10).</summary>
    private const int AuthSetupResponseSize = 1076;

    /// <summary>The methods an OPTIONS answer lists [sps:rtsp.c:2284-2293].</summary>
    private const string PublicMethods = "ANNOUNCE, SETUP, RECORD, PAUSE, FLUSH, FLUSHBUFFERED, TEARDOWN, OPTIONS, POST, GET, PUT";

    private int _nextStreamId;

    private uint NextStreamId() => unchecked((uint)Interlocked.Increment(ref _nextStreamId));

    /// <summary>The session-level state of one control connection (after an accepted session SETUP).</summary>
    private sealed record SessionState(string TimingProtocol, int SenderTimingPort, EventChannelServer EventChannel);

    /// <summary>
    /// One control connection: its pairing view (<see cref="ReceiverPairing.ForConnection"/>, which holds the pairing
    /// result), session and event channel. Requests run one at a time on the connection's read loop, so the fields need
    /// no lock except against <see cref="CleanupAsync"/>, which runs after the read loop has ended.
    /// </summary>
    private sealed class ControlConnection(FakeReceiver owner, ConnectionPairing pairing, int id)
    {
        private readonly TaskCompletionSource<string> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private RtspServerConnection? _connection;
        private SessionState? _session;
        private int _cleanedUp;

        public int Id { get; } = id;

        public IPEndPoint Remote { get; private set; } = new(IPAddress.None, 0);

        public Task<string> Closed => _closed.Task;

        public void Start(Socket socket)
        {
            Remote = (IPEndPoint)socket.RemoteEndPoint!;
            var options = new RtspServerConnectionOptions
            {
                ServerHeader = owner.Options.ServerHeader,
                Name = $"{owner.Options.Name} control #{Id}",
            };
            _connection = RtspServerConnection.Start(socket, HandleAsync, options, owner.Logger);
            _ = _connection.Closed.ContinueWith(
                static (t, state) => ((TaskCompletionSource<string>)state!).TrySetResult(t.Result),
                _closed,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public async Task CloseAsync(string reason)
        {
            if (_connection is { } connection)
            {
                owner.Logger.LogDebug("Fake receiver {Name}: closing control connection #{Id}: {Reason}", owner.Options.Name, Id, reason);
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>After the connection closed: closes the event channel and stops a stream this connection set up (shairport's event-receiver cleanup [sps:ap2_event_receiver.c:37-59]).</summary>
        public async Task CleanupAsync()
        {
            if (Interlocked.Exchange(ref _cleanedUp, 1) != 0)
            {
                return;
            }

            await EndSessionAsync().ConfigureAwait(false);
            if (_connection is { } connection)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async Task EndSessionAsync()
        {
            await owner.StopStreamOwnedByAsync(this).ConfigureAwait(false);
            var session = Interlocked.Exchange(ref _session, null);
            if (session is not null)
            {
                await session.EventChannel.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async ValueTask<RtspServerResponse> HandleAsync(RtspServerConnection connection, RtspIncomingRequest request, CancellationToken cancellationToken)
        {
            owner.Write(new FakeReceiverEvent.Request(request.Method, request.Uri, connection.IsEncrypted, request.Headers, request.Body.ToArray()));
            var method = request.Method.ToUpperInvariant();
            switch (method)
            {
                case "GET" when request.Uri == "/info":
                    return Info(request);
                case "POST":
                    return await PostAsync(connection, request).ConfigureAwait(false);
                case "OPTIONS":
                    return RtspServerResponse.Ok() with { Headers = new Dictionary<string, string> { [RtspHeaders.Public] = PublicMethods } };
            }

            if (NotPaired(method) is { } refused)
            {
                return refused;
            }

            switch (method)
            {
                case "SETUP":
                    return await SetupAsync(connection, request).ConfigureAwait(false);
                case "RECORD":
                    return Record();
                case "SETPEERS":
                    return SetPeers(request);
                case "SETPEERSX":
                case "SETRATEANCHORTIME":
                case "SETRATEANCHORTI": // shairport registers the name truncated to 15 characters [sps:rtsp.c:3703]
                case "FLUSHBUFFERED":
                    // Buffered-stream / PTP methods: accepted and recorded (Request event); the buffered stream itself is not
                    // emulated (SPEC-control.md 4.6-4.8; shairport answers SETPEERSX with 200 without acting [sps:rtsp.c:2245-2250]).
                    return RtspServerResponse.Ok();
                case "SET_PARAMETER":
                    return SetParameter(request);
                case "GET_PARAMETER":
                    return GetParameter(request);
                case "FLUSH":
                    return Flush(request);
                case "TEARDOWN":
                    return await TeardownAsync(request).ConfigureAwait(false);
                default:
                    // shairport's default before a handler runs [sps:rtsp.c:4198].
                    owner.Logger.LogWarning("Fake receiver {Name}: unsupported method {Method} {Uri}", owner.Options.Name, request.Method, request.Uri);
                    return RtspServerResponse.Status(RtspStatus.NotImplemented);
            }
        }

        /// <summary>470 for a session method on a connection that has not paired (<see cref="FakeReceiverOptions.RequirePairing"/>).</summary>
        private RtspServerResponse? NotPaired(string what)
        {
            if (pairing.Session is not null || !owner.Options.RequirePairing)
            {
                return null;
            }

            owner.Logger.LogWarning("Fake receiver {Name}: {What} on control connection #{Id} before pairing; answering 470", owner.Options.Name, what, Id);
            return RtspServerResponse.Status(RtspStatus.ConnectionAuthorizationRequired);
        }

        /// <summary>
        /// <c>GET /info</c> (SPEC-control.md 1.8, 4.1): the stage-1 request (bplist body with <c>qualifier: [txtAirPlay]</c>)
        /// gets <c>txtAirPlay</c> added, as receiver A (the srcvers-366 speaker) does; a bare request (no body) the plain dictionary. A body that is
        /// not a bplist or lacks <c>qualifier.txtAirPlay</c> is answered 400, as shairport's stage-1 <c>user_fail</c>
        /// [sps:rtsp.c:1225-1273] (airplay2-receiver answers 404 [ap2r:ap2-receiver.py:1139-1179]; SPEC-control.md 2.4).
        /// </summary>
        private RtspServerResponse Info(RtspIncomingRequest request)
        {
            var withTxt = false;
            if (!request.Body.IsEmpty)
            {
                try
                {
                    withTxt = request.PlistBody().GetArray("qualifier")?.Contains("txtAirPlay") == true;
                }
                catch (FormatException)
                {
                    // withTxt stays false: answered 400 below.
                }

                if (!withTxt)
                {
                    owner.Logger.LogWarning("Fake receiver {Name}: GET /info stage 1 without a bplist qualifier [txtAirPlay]; answering 400", owner.Options.Name);
                    return RtspServerResponse.Status(RtspStatus.BadRequest);
                }
            }

            return RtspServerResponse.Plist(owner.BuildInfo(withTxt, owner.Options.InitialVolume is not null));
        }

        private async ValueTask<RtspServerResponse> PostAsync(RtspServerConnection connection, RtspIncomingRequest request)
        {
            switch (request.Uri)
            {
                case HapPairingEndpoints.PinStart or HapPairingEndpoints.PairSetup or HapPairingEndpoints.PairVerify:
                    return await PairingAsync(connection, request).ConfigureAwait(false);
                case "/auth-setup":
                    // SPEC-control.md 3.10: airplay2-receiver answers with a canned blob [ap2r:ap2-receiver.py:940-985].
                    return owner.Options.AnswerAuthSetup
                        ? RtspServerResponse.Bytes(RtspContentTypes.OctetStream, new byte[AuthSetupResponseSize])
                        : RtspServerResponse.Status(RtspStatus.NotFound);
                case "/feedback":
                    return NotPaired("POST /feedback") ?? Feedback();
                case "/command":
                case "/audioMode":
                    // Sender -> receiver MediaRemote commands and audio mode: accepted and ignored (SPEC-control.md 4.11).
                    return NotPaired("POST " + request.Uri) ?? RtspServerResponse.Ok();
                default:
                    owner.Logger.LogWarning("Fake receiver {Name}: unknown endpoint POST {Uri}", owner.Options.Name, request.Uri);
                    return RtspServerResponse.Status(RtspStatus.NotFound); // [ap2r:ap2-receiver.py:363-368]
            }
        }

        /// <summary>
        /// The pairing POSTs, answered by the library's <see cref="ConnectionPairing"/> (SPEC-receiver.md 4.2), which applies
        /// <see cref="FakeReceiverOptions.RequireHkpHeader"/> and <see cref="FakeReceiverOptions.EncryptAfterPairVerify"/>
        /// and switches the channel to encryption after a completed procedure [sps:rtsp.c:1810-1833]; the completion is
        /// recorded as <see cref="FakeReceiverEvent.Paired"/>.
        /// </summary>
        private async ValueTask<RtspServerResponse> PairingAsync(RtspServerConnection connection, RtspIncomingRequest request)
        {
            var outcome = await pairing.HandleAsync(connection, request).ConfigureAwait(false);
            if (outcome.Completed is { } session)
            {
                owner.Write(new FakeReceiverEvent.Paired(session.Kind.ToString(), session.SharedSecret.ToArray()));
                owner.Logger.LogDebug("Fake receiver {Name}: control connection #{Id} paired ({Kind})", owner.Options.Name, Id, session.Kind);
            }

            return outcome.Response;
        }

        /// <summary>
        /// <c>POST /feedback</c>: shairport answers <c>{streams: [{type, sr (real)}]}</c> while playing and an empty body
        /// otherwise [sps:rtsp.c:2033-2081].
        /// </summary>
        private RtspServerResponse Feedback()
        {
            if (!owner.HasStreamOwnedBy(this))
            {
                return RtspServerResponse.Ok();
            }

            var body = new PlistDictionary
            {
                ["streams"] = new List<object?> { new PlistDictionary { ["type"] = 96L, ["sr"] = 44100.0 } },
            };
            return RtspServerResponse.Plist(body);
        }

        private async ValueTask<RtspServerResponse> SetupAsync(RtspServerConnection connection, RtspIncomingRequest request)
        {
            PlistDictionary body;
            try
            {
                body = request.PlistBody();
            }
            catch (FormatException ex)
            {
                owner.Logger.LogWarning("Fake receiver {Name}: SETUP without a bplist body: {Error}", owner.Options.Name, ex.Message);
                return RtspServerResponse.Status(RtspStatus.BadRequest);
            }

            // The receiver tells the two SETUPs apart by the presence of "streams" [sps:rtsp.c:2407-2415; ap2r:ap2-receiver.py:579-595].
            return body.ContainsKey("streams")
                ? await StreamSetupAsync(body).ConfigureAwait(false)
                : await SessionSetupAsync(connection, body).ConfigureAwait(false);
        }

        /// <summary>
        /// Session SETUP (SPEC-control.md 4.2): NTP (501 with <see cref="FakeReceiverOptions.RefuseNtp"/>, as shairport-sync
        /// [sps:rtsp.c:2432-2438, 2683-2686]), PTP (answered with the receiver's <c>timingPeerInfo</c> [sps:rtsp.c:2572-2624])
        /// or <c>None</c> (remote-control session [sps:rtsp.c:2439-2470]); anything else stays at shairport's 501
        /// [sps:rtsp.c:2729-2737]. The event listener is opened before answering, as shairport does [sps:rtsp.c:2626-2638].
        /// </summary>
        private async ValueTask<RtspServerResponse> SessionSetupAsync(RtspServerConnection connection, PlistDictionary body)
        {
            var timingProtocol = body.GetString("timingProtocol");
            if (timingProtocol == "NTP" && owner.Options.RefuseNtp)
            {
                owner.Logger.LogWarning("Fake receiver {Name}: refusing an NTP session SETUP (RefuseNtp)", owner.Options.Name);
                return RtspServerResponse.Status(RtspStatus.NotImplemented);
            }

            if (timingProtocol is not ("NTP" or "PTP" or "None"))
            {
                owner.Logger.LogWarning("Fake receiver {Name}: session SETUP with timingProtocol {Protocol}; answering 501", owner.Options.Name, timingProtocol ?? "(none)");
                return RtspServerResponse.Status(RtspStatus.NotImplemented);
            }

            // A second session SETUP on the same connection replaces the first.
            await EndSessionAsync().ConfigureAwait(false);

            var keys = pairing.Session is { } paired ? ReceiverPairing.EventKeysFor(paired) : null;
            var channel = new EventChannelServer(owner._eventChannelHost, owner.EventChannelOptions(), keys, Id, owner.Logger);
            channel.Start();
            var senderTimingPort = (int)(body.GetInt64("timingPort") ?? 0);
            _session = new SessionState(timingProtocol, senderTimingPort, channel);
            owner.SetGroup(body.GetString("groupUUID"), body.GetBool("groupContainsGroupLeader") ?? false);
            owner.Write(new FakeReceiverEvent.SessionSetup(body));

            var response = new PlistDictionary
            {
                ["eventPort"] = (long)channel.Port,
                ["timingPort"] = 0L, // shairport and airplay2-receiver send a dummy 0 [sps:rtsp.c:2654; ap2r:ap2-receiver.py:220]
            };
            if (timingProtocol == "PTP")
            {
                var self = connection.LocalEndPoint.Address.ToString();
                response["timingPeerInfo"] = new PlistDictionary
                {
                    ["Addresses"] = new List<object?> { self },
                    ["ID"] = self,
                };
            }

            return RtspServerResponse.Plist(response);
        }

        /// <summary>
        /// Stream SETUP (SPEC-control.md 4.4, SPEC-audio.md 1.1): type 96 only (the buffered type 103 is answered 501; the
        /// buffered-stream package brings its own reader). A missing or wrong-length <c>shk</c> is accepted with a warning,
        /// as shairport does [sps:rtsp.c:2766-2785]; packets then fail as <see cref="FakeReceiverEvent.BadAudioPacket"/>.
        /// </summary>
        private async ValueTask<RtspServerResponse> StreamSetupAsync(PlistDictionary body)
        {
            if (_session is not { } session)
            {
                owner.Logger.LogWarning("Fake receiver {Name}: stream SETUP before the session SETUP; answering 451", owner.Options.Name);
                return RtspServerResponse.Status(RtspStatus.Unavailable);
            }

            if (body.GetArray("streams") is not [PlistDictionary stream, ..])
            {
                return RtspServerResponse.Status(RtspStatus.BadRequest);
            }

            var type = stream.GetInt64("type");
            if (type != 96)
            {
                owner.Logger.LogWarning("Fake receiver {Name}: stream type {Type} is not emulated; answering 501", owner.Options.Name, type);
                return RtspServerResponse.Status(RtspStatus.NotImplemented);
            }

            var key = stream.GetData("shk");
            if (key is not { Length: 32 })
            {
                owner.Logger.LogWarning("Fake receiver {Name}: stream SETUP shk has {Length} bytes, not 32; ignoring it", owner.Options.Name, key?.Length ?? 0);
                key = null;
            }

            var senderControl = new IPEndPoint(Remote.Address, (int)(stream.GetInt64("controlPort") ?? 0));
            var timingTarget = session.TimingProtocol == "NTP" && session.SenderTimingPort > 0
                ? new IPEndPoint(Remote.Address, session.SenderTimingPort)
                : null;
            uint? streamId = owner.Options.ReturnStreamId ? owner.NextStreamId() : null;
            await owner.StartStreamAsync(this, key, senderControl, streamId, timingTarget).ConfigureAwait(false);
            owner.Write(new FakeReceiverEvent.StreamSetup(body));

            var answer = new PlistDictionary
            {
                ["type"] = 96L,
                ["dataPort"] = (long)owner.DataPort,
                ["controlPort"] = (long)owner.ControlPort,
            };
            if (streamId is { } id)
            {
                answer["streamID"] = (long)id; // airplay2-receiver returns one, shairport none [ap2r:ap2/connections/stream.py:45-53; sps:rtsp.c:2911-2913]
            }

            return RtspServerResponse.Plist(new PlistDictionary { ["streams"] = new List<object?> { answer } });
        }

        /// <summary>RECORD: AirPlay 2 receivers answer 200 with <c>Audio-Latency: 0</c> [sps:rtsp.c:903-911; ap2r:ap2-receiver.py:717-732].</summary>
        private RtspServerResponse Record()
        {
            if (!owner.Options.UpdateInfoOnConnect)
            {
                // Apple TV sends updateInfo after RECORD [protocols.md:1767-1784].
                _session?.EventChannel.UpdateInfoAfterRecord();
            }

            return RtspServerResponse.Ok() with { Headers = new Dictionary<string, string> { [RtspHeaders.AudioLatency] = "0" } };
        }

        /// <summary>SETPEERS: a bplist array of address strings (SPEC-control.md 4.6); shairport answers 200 without acting [sps:rtsp.c:2207-2244].</summary>
        private RtspServerResponse SetPeers(RtspIncomingRequest request)
        {
            var addresses = new List<string>();
            try
            {
                var start = request.Body.Span.IndexOf("bplist00"u8);
                if (start >= 0 && BinaryPlist.Decode(request.Body.Span[start..]) is List<object?> list)
                {
                    addresses.AddRange(list.OfType<string>());
                }
            }
            catch (FormatException ex)
            {
                owner.Logger.LogWarning("Fake receiver {Name}: SETPEERS body is not a bplist array: {Error}", owner.Options.Name, ex.Message);
            }

            owner.Write(new FakeReceiverEvent.Peers(addresses));
            return RtspServerResponse.Ok();
        }

        /// <summary>
        /// SET_PARAMETER by Content-Type (SPEC-control.md 5); 200 without body [sps:rtsp.c:3430], except DMAP text when
        /// <see cref="FakeReceiverOptions.DmapStatus"/> says otherwise.
        /// </summary>
        private RtspServerResponse SetParameter(RtspIncomingRequest request)
        {
            var contentType = request.ContentType ?? string.Empty;
            var rtpTime = RequestParsing.ParseRtpInfo(request.Header(RtspHeaders.RtpInfo)).RtpTime;
            if (contentType.StartsWith(RtspContentTypes.Parameters, StringComparison.OrdinalIgnoreCase))
            {
                var text = Encoding.UTF8.GetString(request.Body.Span);
                foreach (var parameter in RequestParsing.ParseTextParameters(text))
                {
                    switch (parameter)
                    {
                        case RequestParsing.TextParameter.VolumeValue volume:
                            owner.SetVolume(volume.Db);
                            owner.Write(new FakeReceiverEvent.Volume(volume.Db));
                            break;
                        case RequestParsing.TextParameter.ProgressValue progress:
                            owner.Write(new FakeReceiverEvent.Progress(progress.Start, progress.Current, progress.End));
                            break;
                        case RequestParsing.TextParameter.Other other:
                            owner.Logger.LogWarning("Fake receiver {Name}: unknown text parameter {Line}", owner.Options.Name, other.Line);
                            break;
                    }
                }
            }
            else if (contentType.StartsWith(RtspContentTypes.DmapTagged, StringComparison.OrdinalIgnoreCase))
            {
                owner.Write(new FakeReceiverEvent.Metadata(RequestParsing.ParseDmapListingItem(request.Body.Span), rtpTime));
                if (owner.Options.DmapStatus != RtspStatus.Ok)
                {
                    return RtspServerResponse.Status(owner.Options.DmapStatus);
                }
            }
            else if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                owner.Write(new FakeReceiverEvent.Artwork(contentType, request.Body.ToArray(), rtpTime));
            }
            else
            {
                // A receiver that does not want a metadata kind silently drops it (SPEC-control.md 5).
                owner.Logger.LogWarning("Fake receiver {Name}: SET_PARAMETER with content type {ContentType} ignored", owner.Options.Name, contentType);
            }

            return RtspServerResponse.Ok();
        }

        /// <summary>
        /// GET_PARAMETER: only <c>volume\r\n</c> is answered with a value, in the shape of
        /// <see cref="FakeReceiverOptions.GetParameterShape"/> (SPEC-control.md 4.11); anything else gets 200 without body,
        /// as shairport does [sps:rtsp.c:3318-3338].
        /// </summary>
        private RtspServerResponse GetParameter(RtspIncomingRequest request)
        {
            if (!request.Body.Span.StartsWith("volume"u8))
            {
                return RtspServerResponse.Ok();
            }

            var db = owner.VolumeDb;
            var text = owner.Options.GetParameterShape == GetParameterShape.Shairport
                ? "\r\nvolume: " + db.ToString("0.000000", CultureInfo.InvariantCulture) + "\r\n"
                : "volume: " + RequestParsing.PythonFloat(db) + "\r\n";
            return RtspServerResponse.Bytes(RtspContentTypes.Parameters, Encoding.ASCII.GetBytes(text));
        }

        /// <summary>FLUSH (SPEC-control.md 4.8): <c>RTP-Info: seq=;rtptime=</c>; the next packet is expected at <c>seq</c>.</summary>
        private RtspServerResponse Flush(RtspIncomingRequest request)
        {
            var (seq, rtpTime) = RequestParsing.ParseRtpInfo(request.Header(RtspHeaders.RtpInfo));
            owner.FlushStream(this, seq);
            owner.Write(new FakeReceiverEvent.Flush(seq ?? 0, rtpTime ?? 0));
            return RtspServerResponse.Ok();
        }

        /// <summary>
        /// TEARDOWN (SPEC-control.md 4.9): <c>{streams: [...]}</c> stops the stream and keeps the session; any other plist
        /// (<c>{}</c>) answers with <c>Connection: close</c> and closes the connection, after which the session ends
        /// (<see cref="CleanupAsync"/>) [sps:rtsp.c:2295-2326];
        /// without a plist nothing is done [sps:rtsp.c:2322-2324].
        /// </summary>
        private async ValueTask<RtspServerResponse> TeardownAsync(RtspIncomingRequest request)
        {
            PlistDictionary body;
            try
            {
                body = request.PlistBody();
            }
            catch (FormatException)
            {
                owner.Logger.LogWarning("Fake receiver {Name}: TEARDOWN has no plist -- nothing done", owner.Options.Name);
                return RtspServerResponse.Ok();
            }

            if (body.ContainsKey("streams"))
            {
                await owner.StopStreamOwnedByAsync(this).ConfigureAwait(false);
                owner.Write(new FakeReceiverEvent.Teardown(false));
                return RtspServerResponse.Ok();
            }

            // As shairport (conn->stop only): the session ends in CleanupAsync once the connection has closed, so the wire
            // order is answer -> control socket close -> event channel close [sps:ap2_event_receiver.c:37-59].
            owner.Write(new FakeReceiverEvent.Teardown(true));
            return RtspServerResponse.Ok() with
            {
                Headers = new Dictionary<string, string> { [RtspHeaders.Connection] = "close" },
                CloseAfter = true,
            };
        }
    }
}
