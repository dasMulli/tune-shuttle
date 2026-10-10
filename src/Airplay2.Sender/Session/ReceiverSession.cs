using System.Globalization;
using System.Net;
using System.Text;
using Airplay2.Sender.Crypto;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Tlv8;
using Microsoft.Extensions.Logging;

namespace Airplay2.Sender.Session;

/// <summary>Options of a <see cref="ReceiverSession"/>.</summary>
public sealed record ReceiverSessionOptions
{
    /// <summary>Event-channel connect retries, answer delay (probe P13) and message capacity.</summary>
    public EventChannelOptions EventChannel { get; init; } = new();

    /// <summary>The M1 method byte of the PIN flow (SPEC.md D2: <c>PairSetup</c>; probe P6 / C-7.6 may change it for the TV).</summary>
    public TlvMethod PinPairingMethod { get; init; } = TlvMethod.PairSetup;
}

/// <summary>The answers of a TEARDOWN sequence (probe P5 inspects them).</summary>
/// <param name="StreamResponses">One answer per per-stream TEARDOWN, in order.</param>
/// <param name="SessionResponse">The answer to the session TEARDOWN <c>{}</c>, or null when the connection was already gone.</param>
public sealed record TeardownResult(IReadOnlyList<RtspResponse> StreamResponses, RtspResponse? SessionResponse)
{
    /// <summary>True when the session TEARDOWN was answered with <c>Connection: close</c> (shairport-sync does [sps:rtsp.c:2312-2320]).</summary>
    public bool ConnectionClose => string.Equals(SessionResponse?.Header(RtspHeaders.Connection), "close", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Implementation of <see cref="IReceiverSession"/> over one <see cref="IRtspConnection"/> (SPEC-control.md 3.10, 4, 5;
/// SPEC-audio.md 1.1, 6; SPEC.md D5-D7, D10, D12, D17). The caller drives the D5 start-up order:
/// <see cref="GetInfoAsync"/> → <see cref="PairAsync"/> → (<see cref="AuthSetupAsync"/>, run by
/// <see cref="SetupSessionAsync"/> when <see cref="ReceiverQuirks.SendAuthSetup"/> is set) → <see cref="SetupSessionAsync"/>
/// → <see cref="ConnectEventChannelAsync"/> → <see cref="RecordAsync"/> → (<see cref="SetPeersAsync"/>, PTP) →
/// <see cref="SetupStreamAsync"/> → <see cref="SetVolumeAsync"/> → audio → metadata → <see cref="FeedbackAsync(PlistDictionary?, CancellationToken)"/> every 2 s;
/// <see cref="TeardownAsync"/> ends it (D7).
/// </summary>
/// <remarks>
/// Requests are serialised by the connection (one in flight). Non-2xx answers throw <see cref="RtspStatusException"/>
/// except where the spec says to ignore them (<c>/auth-setup</c>, the per-stream TEARDOWN); transport failures surface as
/// <see cref="IOException"/>/<see cref="TimeoutException"/> from the connection and as its <see cref="IRtspConnection.Closed"/>.
/// </remarks>
public sealed class ReceiverSession : IReceiverSession
{
    /// <summary>Consecutive failed feedbacks after which the receiver counts as gone (SPEC-control.md 4.10).</summary>
    public const int FeedbackFailureLimit = 3;

    /// <summary>Artwork above this size is sent with a warning (SPEC.md D12: at most 200 KiB; shairport-sync buffers 256 KiB [sps:rtsp.c:594-598]).</summary>
    public const int RecommendedMaxArtworkBytes = 200 * 1024;

    private readonly Lock _lock = new();
    private readonly List<(uint StreamId, StreamType Type)> _streams = [];
    private readonly ReceiverSessionOptions _options;
    private SessionSetupResult? _sessionSetup;
    private EventChannel? _eventChannel;

    // Only touched on the request path, which one caller drives at a time (requests are serialised anyway).
    private bool _authSetupSent;
    private int _feedbackFailures;
    private int _dmapRefused;
    private int _disposed;

    /// <summary>Creates a session over an existing connection.</summary>
    public ReceiverSession(DiscoveredReceiver receiver, IRtspConnection connection, IPairingClient pairingClient, ReceiverQuirks quirks, ILogger<ReceiverSession> logger)
        : this(receiver, connection, pairingClient, quirks, logger, options: null)
    {
    }

    /// <summary>Creates a session over an existing connection with options.</summary>
    public ReceiverSession(DiscoveredReceiver receiver, IRtspConnection connection, IPairingClient pairingClient, ReceiverQuirks quirks, ILogger<ReceiverSession> logger, ReceiverSessionOptions? options)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(pairingClient);
        ArgumentNullException.ThrowIfNull(quirks);
        ArgumentNullException.ThrowIfNull(logger);
        Receiver = receiver;
        Connection = connection;
        PairingClient = pairingClient;
        Quirks = quirks;
        Logger = logger;
        _options = options ?? new ReceiverSessionOptions();
    }

    /// <inheritdoc />
    public DiscoveredReceiver Receiver { get; }

    /// <inheritdoc />
    public IRtspConnection Connection { get; }

    /// <inheritdoc />
    public PairingSession? Pairing { get; private set; }

    /// <summary>The quirks in effect.</summary>
    public ReceiverQuirks Quirks { get; }

    /// <summary>The answer of the last session SETUP, or null.</summary>
    public SessionSetupResult? SessionSetup
    {
        get
        {
            lock (_lock)
            {
                return _sessionSetup;
            }
        }
    }

    /// <summary>The connected event channel, or null.</summary>
    public EventChannel? EventChannel
    {
        get
        {
            lock (_lock)
            {
                return _eventChannel;
            }
        }
    }

    /// <summary>Consecutive <see cref="FeedbackAsync(PlistDictionary?, CancellationToken)"/> failures; at <see cref="FeedbackFailureLimit"/> the receiver counts as gone.</summary>
    public int ConsecutiveFeedbackFailures => Volatile.Read(ref _feedbackFailures);

    /// <summary>The <c>streamID</c>s returned by stream SETUPs that are not torn down yet.</summary>
    public IReadOnlyList<uint> StreamIds
    {
        get
        {
            lock (_lock)
            {
                return [.. _streams.Select(s => s.StreamId)];
            }
        }
    }

    private IPairingClient PairingClient { get; }

    private ILogger Logger { get; }

    private string Uri => Connection.SessionUri;

    /// <inheritdoc />
    public async Task<PlistDictionary> GetInfoAsync(bool withQualifier, CancellationToken cancellationToken)
    {
        var response = await Connection.SendExpectOkAsync(SessionRequests.Info(withQualifier), cancellationToken).ConfigureAwait(false);
        var info = response.PlistBody();
        Logger.LogDebug(
            "GET /info{Stage} from {Receiver}{Encrypted}: {Keys} keys, initialVolume {InitialVolume}",
            withQualifier ? " (stage 1)" : string.Empty,
            Receiver.Name,
            Connection.IsEncrypted ? " (encrypted)" : string.Empty,
            info.Count,
            info.GetDouble("initialVolume")?.ToString(CultureInfo.InvariantCulture) ?? "absent");
        return info;
    }

    /// <inheritdoc />
    /// <remarks>
    /// SPEC-control.md 3.8/3.9, 6.2 and SPEC.md D2: with stored credentials pair-verify; a rejection
    /// (<see cref="PairingFailure.CredentialsRejected"/>: TLV error, 470, wrong identity or signature) deletes the stored
    /// pairing and falls through. Then, per the TXT decision procedure (1.6), transient pair-setup; a refusal (470 or TLV
    /// error, <see cref="PairingFailure.TransientRefused"/>) falls back to the PIN flow. The PIN flow
    /// (<c>/pair-pin-start</c>, M1/M2, <paramref name="pinPrompt"/>, M3-M6, persist, pair-verify on the same connection as
    /// owntone does [owntone:airplay.c:3686-3693]) needs a prompt; without one a <see cref="PairingException"/> with
    /// <see cref="PairingFailure.TransientRefused"/> tells the caller that a PIN is required (the bridge emits
    /// <c>PairingRequired</c>). On success the connection is switched to encryption with the control keys (2.5).
    /// </remarks>
    public async Task<PairingSession> PairAsync(IPairingStore store, Func<CancellationToken, Task<string>>? pinPrompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (Pairing is not null || Connection.IsEncrypted)
        {
            throw new InvalidOperationException("This connection is already paired.");
        }

        var session = await PairCoreAsync(store, pinPrompt, cancellationToken).ConfigureAwait(false);
        Connection.EnableEncryption(session.ControlKeys);
        Pairing = session;
        Logger.LogInformation("Paired with {Receiver} ({Kind}); control channel encrypted", Receiver.Name, session.Kind);
        return session;
    }

    /// <inheritdoc />
    /// <remarks>
    /// SPEC-control.md 3.10: <c>POST /auth-setup RTSP/1.0</c>, <c>application/octet-stream</c>, 33 bytes = <c>0x01</c> + the
    /// public key of a fresh ephemeral X25519 key pair (nothing derived from it is ever used, pyatv sends a fixed key for
    /// the same reason [pyatv:pyatv/support/rtsp.py:40-49]). Any 2xx is accepted and the body discarded; a non-2xx is
    /// logged at Warning and ignored (best effort, as pyatv [pyatv:pyatv/protocols/raop/stream_client.py:354-363]).
    /// </remarks>
    public async Task AuthSetupAsync(CancellationToken cancellationToken)
    {
        var key = X25519KeyPair.Generate();
        var response = await Connection.SendAsync(SessionRequests.AuthSetup(key.PublicKey), cancellationToken).ConfigureAwait(false);
        _authSetupSent = true;

        if (response.IsSuccess)
        {
            Logger.LogDebug("POST /auth-setup to {Receiver}: {Status}, {Bytes} bytes ignored", Receiver.Name, response.StatusCode, response.Body.Length);
        }
        else
        {
            Logger.LogWarning("POST /auth-setup to {Receiver} answered {Status} {Reason}; ignored", Receiver.Name, response.StatusCode, response.ReasonPhrase);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The body is <see cref="SessionRequests.SessionSetupBody"/>. A non-2xx answer surfaces as
    /// <see cref="RtspStatusException"/>; a 2xx without a plist or without <c>eventPort</c> as
    /// <see cref="InvalidDataException"/> (owntone aborts without it [owntone:airplay.c:3216-3227]); that is how
    /// shairport-sync 5.5.2 refuses an NTP session in practice (200 with an empty body; the spec expected 501). The D1 gate
    /// (probe P1) treats both as "no NTP".
    /// </remarks>
    public async Task<SessionSetupResult> SetupSessionAsync(SessionSetupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // SPEC-control.md 3.10: after pairing, before the session SETUP, inside the encrypted channel.
        if (Quirks.SendAuthSetup && !_authSetupSent)
        {
            await AuthSetupAsync(cancellationToken).ConfigureAwait(false);
        }

        var body = SessionRequests.SessionSetupBody(request, Connection.LocalAddress);
        Logger.LogInformation(
            "SETUP session to {Receiver}: timing {Protocol}, timing port {TimingPort}, group {Group}",
            Receiver.Name,
            request.TimingProtocol,
            request.TimingPort,
            request.GroupUuid ?? "none");
        var response = await Connection.SendExpectOkAsync(RtspRequest.WithPlist("SETUP", Uri, body), cancellationToken).ConfigureAwait(false);
        PlistDictionary plist;
        try
        {
            plist = response.PlistBody();
        }
        catch (FormatException)
        {
            // shairport-sync 5.5.2 answers an NTP session SETUP with "200 OK" and an empty body (it logs "Shairport Sync
            // can not handle NTP streams"; live 2026-10-09, SPEC-control.md 4.2 note), not with 501.
            Logger.LogWarning("Session SETUP ({Protocol}) answered {Status} by {Receiver} without a plist: the receiver refused the session", request.TimingProtocol, response.StatusCode, Receiver.Name);
            throw new InvalidDataException($"{Receiver.Name} answered the {request.TimingProtocol} session SETUP with {response.StatusCode} but no plist (no eventPort): the session was refused.");
        }

        var eventPort = plist.GetInt64("eventPort");
        if (eventPort is not (> 0 and <= ushort.MaxValue))
        {
            throw new InvalidDataException($"The session SETUP answer of {Receiver.Name} carries no valid eventPort.");
        }

        var peers = new List<IPAddress>();
        if (plist.GetDictionary("timingPeerInfo")?.GetArray("Addresses") is { } addresses)
        {
            foreach (var entry in addresses)
            {
                if (entry is string text && IPAddress.TryParse(text, out var address))
                {
                    peers.Add(address);
                }
            }
        }

        var result = new SessionSetupResult((int)eventPort.Value, (int)(plist.GetInt64("timingPort") ?? 0), peers, plist);
        lock (_lock)
        {
            _sessionSetup = result;
        }

        Logger.LogDebug("Session SETUP answered by {Receiver}: eventPort {EventPort}, timingPort {TimingPort}, {Peers} PTP peer address(es)", Receiver.Name, result.EventPort, result.TimingPort, peers.Count);
        return result;
    }

    /// <inheritdoc />
    async Task<IEventChannel> IReceiverSession.ConnectEventChannelAsync(CancellationToken cancellationToken) =>
        await ConnectEventChannelAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Connects the event channel (SPEC-control.md 4.3) to the receiver's address and <c>eventPort</c>, with the Events keys
    /// in the swapped roles; retries a refused connect (<see cref="EventChannelOptions.ConnectAttempts"/> x
    /// <see cref="EventChannelOptions.ConnectRetryDelay"/>). Replaces (and closes) an earlier channel of this session.
    /// </summary>
    public async Task<EventChannel> ConnectEventChannelAsync(CancellationToken cancellationToken)
    {
        SessionSetupResult setup;
        lock (_lock)
        {
            setup = _sessionSetup ?? throw new InvalidOperationException("The event channel needs a session SETUP first.");
        }

        var remote = new IPEndPoint(Connection.RemoteEndPoint.Address, setup.EventPort);
        var channel = await EventChannel.ConnectAsync(remote, Pairing?.EventKeys, _options.EventChannel, Logger, cancellationToken).ConfigureAwait(false);
        EventChannel? previous;
        lock (_lock)
        {
            previous = _eventChannel;
            _eventChannel = channel;
        }

        if (previous is not null)
        {
            await previous.DisposeAsync().ConfigureAwait(false);
        }

        Logger.LogInformation("Event channel to {Receiver} connected ({Endpoint})", Receiver.Name, remote);
        return channel;
    }

    /// <inheritdoc />
    /// <remarks>
    /// No body (SPEC-control.md 4.5). <c>Audio-Latency</c> (frames) is returned as is; a non-zero value is that receiver's
    /// output delay for its <c>offset_frames</c>, never a change of <c>L</c> (SPEC.md C2). Absent or unparsable → 0.
    /// </remarks>
    public async Task<int> RecordAsync(CancellationToken cancellationToken)
    {
        var response = await Connection.SendExpectOkAsync(SessionRequests.Record(Uri), cancellationToken).ConfigureAwait(false);
        var header = response.Header(RtspHeaders.AudioLatency);
        var latency = 0;
        if (header is not null && !int.TryParse(header.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out latency))
        {
            Logger.LogWarning("RECORD answer of {Receiver} has an unparsable Audio-Latency '{Value}'", Receiver.Name, header);
            latency = 0;
        }

        Logger.LogInformation("RECORD to {Receiver}: Audio-Latency {Latency}", Receiver.Name, header ?? "absent");
        return latency;
    }

    /// <inheritdoc />
    public async Task SetPeersAsync(IReadOnlyList<IPAddress> peers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peers);
        Logger.LogDebug("SETPEERS to {Receiver}: {Peers}", Receiver.Name, string.Join(", ", peers.Select(SessionRequests.FormatAddress)));
        await Connection.SendExpectOkAsync(SessionRequests.SetPeers(Uri, peers), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Body per SPEC-audio.md 1.1 (<see cref="SessionRequests.StreamSetupBody"/>). The answer's <c>streams[0]</c> must carry
    /// non-zero <c>dataPort</c> and <c>controlPort</c> (owntone requires both [owntone:airplay.c:3071-3098]); a returned
    /// <c>streamID</c> is remembered for the per-stream TEARDOWN (D7).
    /// </remarks>
    /// <exception cref="InvalidOperationException">The connection is not encrypted (pair first).</exception>
    public async Task<StreamSetupResult> SetupStreamAsync(StreamSetupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Connection.IsEncrypted)
        {
            // shk travels in the clear inside the stream SETUP body: the connection must be encrypted (SPEC-control.md 4.4,
            // SPEC.md D4); refuse rather than leak the audio key.
            throw new InvalidOperationException("The stream SETUP carries shk; pair first (the connection must be encrypted).");
        }

        var body = SessionRequests.StreamSetupBody(request, Connection.SessionId);
        Logger.LogInformation("SETUP stream {StreamType} to {Receiver}, our control port {ControlPort}", (int)request.Type, Receiver.Name, request.ControlPort);
        var response = await Connection.SendExpectOkAsync(RtspRequest.WithPlist("SETUP", Uri, body), cancellationToken).ConfigureAwait(false);
        var plist = response.PlistBody();
        if (plist.GetArray("streams") is not [PlistDictionary stream, ..])
        {
            throw new InvalidDataException($"The stream SETUP answer of {Receiver.Name} has no streams[0].");
        }

        var type = stream.GetInt64("type") is { } t && Enum.IsDefined((StreamType)(int)t) ? (StreamType)(int)t : request.Type;
        var dataPort = (int)(stream.GetInt64("dataPort") ?? 0);
        var controlPort = (int)(stream.GetInt64("controlPort") ?? 0);
        if (dataPort is <= 0 or > ushort.MaxValue || (type == StreamType.Realtime && controlPort is <= 0 or > ushort.MaxValue))
        {
            throw new InvalidDataException($"The stream SETUP answer of {Receiver.Name} lacks a valid dataPort/controlPort ({dataPort}/{controlPort}).");
        }

        uint? streamId = stream.GetInt64("streamID") is { } id and >= 0 and <= uint.MaxValue ? (uint)id : null;
        var result = new StreamSetupResult(type, dataPort, controlPort, streamId, stream.GetInt64("audioBufferSize"), stream);
        if (streamId is { } sid)
        {
            lock (_lock)
            {
                _streams.Add((sid, type));
            }
        }

        Logger.LogInformation(
            "Stream SETUP answered by {Receiver}: data port {DataPort}, control port {ControlPort}, streamID {StreamId}",
            Receiver.Name,
            dataPort,
            controlPort,
            streamId?.ToString(CultureInfo.InvariantCulture) ?? "none");
        return result;
    }

    /// <inheritdoc />
    /// <remarks>Body <c>volume: %.6f\r\n</c> (SPEC-control.md 5.1, SPEC-audio.md 6.1); ordering and coalescing are the group player's (D10).</remarks>
    public async Task SetVolumeAsync(double volumeDb, CancellationToken cancellationToken)
    {
        var request = SessionRequests.SetVolume(Uri, volumeDb);
        Logger.LogDebug("SET_PARAMETER volume {Volume} dB to {Receiver}", volumeDb, Receiver.Name);
        await Connection.SendExpectOkAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidDataException">The answer carries no <c>volume:</c> line (the receiver does not support it).</exception>
    public async Task<double> GetVolumeAsync(CancellationToken cancellationToken) =>
        await TryGetVolumeAsync(cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidDataException($"{Receiver.Name} answered GET_PARAMETER volume without a volume line.");

    /// <summary>
    /// <c>GET_PARAMETER volume</c> (SPEC-control.md 4.11, SPEC-audio.md 6.2): the reply's text is parsed with
    /// <see cref="VolumeMapping.ParseParameter"/> (leading blank line skipped, <c>volume:</c> found anywhere); null when
    /// the reply has no volume line.
    /// </summary>
    public async Task<double?> TryGetVolumeAsync(CancellationToken cancellationToken)
    {
        var response = await Connection.SendExpectOkAsync(SessionRequests.GetVolume(Uri), cancellationToken).ConfigureAwait(false);
        var text = Encoding.UTF8.GetString(response.Body.Span);
        var volume = VolumeMapping.ParseParameter(text);
        Logger.LogDebug("GET_PARAMETER volume from {Receiver}: {Volume}", Receiver.Name, volume?.ToString(CultureInfo.InvariantCulture) ?? "none");
        return volume;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Metadata is best effort (SPEC-control.md 5: a receiver that does not want an item drops it): a non-2xx answer is
    /// logged at Warning and not thrown. The srcvers-366 speaker answers DMAP with 500, with and without audio and sometimes
    /// only after ~10 s (probe P9, 2026-10-09; SPEC.md 4.2). Since the control connection carries one request at a time, a
    /// volume change or FLUSH queued behind such a request waits that long, so after the first non-2xx answer to DMAP this
    /// session sends no more DMAP text (logged once; <see cref="DmapRefused"/>). Artwork and progress continue. Transport
    /// failures still throw.
    /// </remarks>
    public async Task SetMetadataAsync(TrackMetadata metadata, uint startFrame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (DmapRefused)
        {
            Logger.LogDebug("SET_PARAMETER DMAP to {Receiver} skipped: refused earlier in this session", Receiver.Name);
            return;
        }

        Logger.LogDebug("SET_PARAMETER DMAP to {Receiver}: '{Title}' / '{Artist}' / '{Album}' at rtptime {Start}", Receiver.Name, metadata.Title, metadata.Artist, metadata.Album, startFrame);
        var response = await SendMetadataItemAsync(SessionRequests.Metadata(Uri, metadata, startFrame), "DMAP", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess && Interlocked.Exchange(ref _dmapRefused, 1) == 0)
        {
            Logger.LogInformation("{Receiver} refused DMAP text ({Status}); no more DMAP on this session, artwork and progress continue", Receiver.Name, response.StatusCode);
        }
    }

    /// <summary>
    /// True after the receiver answered DMAP text with a non-2xx status on this session; <see cref="SetMetadataAsync"/>
    /// then sends nothing (SPEC.md 4.2, the srcvers-366 speaker's DMAP 500).
    /// </summary>
    public bool DmapRefused => Volatile.Read(ref _dmapRefused) != 0;

    /// <inheritdoc />
    /// <remarks>
    /// The image goes through the 1024-byte frames of the control channel (~200 frames for 200 KiB); one request at a time
    /// keeps other requests from interleaving (SPEC-control.md 5.3). Larger than <see cref="RecommendedMaxArtworkBytes"/> is
    /// sent with a warning; scaling is the caller's. Best effort like <see cref="SetMetadataAsync"/>: a non-2xx answer is
    /// logged, not thrown.
    /// </remarks>
    public async Task SetArtworkAsync(ReadOnlyMemory<byte> image, string contentType, uint startFrame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contentType);
        var request = SessionRequests.Artwork(Uri, image, contentType, startFrame);
        if (image.Length > RecommendedMaxArtworkBytes)
        {
            Logger.LogWarning("Artwork for {Receiver} is {Bytes} bytes, above the recommended {Max}", Receiver.Name, image.Length, RecommendedMaxArtworkBytes);
        }

        Logger.LogDebug("SET_PARAMETER {ContentType} ({Bytes} bytes) to {Receiver} at rtptime {Start}", contentType, image.Length, Receiver.Name, startFrame);
        await SendMetadataItemAsync(request, "artwork", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>Best effort like <see cref="SetMetadataAsync"/>: a non-2xx answer is logged, not thrown.</remarks>
    public async Task SetProgressAsync(ProgressInfo progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        Logger.LogDebug("SET_PARAMETER progress {Start}/{Current}/{End} to {Receiver}", progress.Start, progress.Current, progress.End, Receiver.Name);
        await SendMetadataItemAsync(SessionRequests.Progress(Uri, progress), "progress", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task FlushAsync(ushort nextSequence, uint nextTimestamp, CancellationToken cancellationToken)
    {
        Logger.LogDebug("FLUSH to {Receiver}: seq {Sequence}, rtptime {Timestamp}", Receiver.Name, nextSequence, nextTimestamp);
        await Connection.SendExpectOkAsync(SessionRequests.Flush(Uri, nextSequence, nextTimestamp), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetRateAnchorTimeAsync(int rate, uint rtpTime, ulong networkTimeSecs, ulong networkTimeFrac, ulong timelineId, CancellationToken cancellationToken)
    {
        Logger.LogDebug("SETRATEANCHORTIME to {Receiver}: rate {Rate}, rtpTime {RtpTime}, network time {Secs}+{Frac}, timeline {Timeline:X16}", Receiver.Name, rate, rtpTime, networkTimeSecs, networkTimeFrac, timelineId);
        await Connection.SendExpectOkAsync(SessionRequests.SetRateAnchorTime(Uri, rate, rtpTime, networkTimeSecs, networkTimeFrac, timelineId), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task FlushBufferedAsync(uint flushUntilSeq, uint flushUntilTimestamp, uint? flushFromSeq, uint? flushFromTimestamp, CancellationToken cancellationToken)
    {
        Logger.LogDebug("FLUSHBUFFERED to {Receiver}: until seq {Seq} ts {Ts}", Receiver.Name, flushUntilSeq, flushUntilTimestamp);
        await Connection.SendExpectOkAsync(SessionRequests.FlushBuffered(Uri, flushUntilSeq, flushUntilTimestamp, flushFromSeq, flushFromTimestamp), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<PlistDictionary?> FeedbackAsync(CancellationToken cancellationToken) => FeedbackAsync(body: null, cancellationToken);

    /// <summary>
    /// <c>POST /feedback</c> (SPEC-control.md 4.10) with an optional bplist body (probe P8 tries <c>{}</c> and
    /// <c>{streams: []}</c>). Any 2xx counts as alive and resets <see cref="ConsecutiveFeedbackFailures"/>; a non-2xx
    /// (<see cref="RtspStatusException"/>) or a transport error increments it and is rethrown. Returns the answer's bplist
    /// (shairport-sync: <c>{streams: [{type, sr}]}</c> while playing), or null for an empty or non-plist body.
    /// </summary>
    public async Task<PlistDictionary?> FeedbackAsync(PlistDictionary? body, CancellationToken cancellationToken)
    {
        RtspResponse response;
        try
        {
            response = await Connection.SendExpectOkAsync(SessionRequests.Feedback(body), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RtspStatusException or IOException or TimeoutException)
        {
            var failures = Interlocked.Increment(ref _feedbackFailures);
            Logger.Log(
                failures >= FeedbackFailureLimit ? LogLevel.Warning : LogLevel.Debug,
                "POST /feedback to {Receiver} failed ({Failures} in a row): {Error}",
                Receiver.Name,
                failures,
                ex.Message);
            throw;
        }

        Volatile.Write(ref _feedbackFailures, 0);
        if (response.Body.IsEmpty)
        {
            return null;
        }

        try
        {
            return response.PlistBody();
        }
        catch (FormatException)
        {
            Logger.LogDebug("POST /feedback answer of {Receiver} is not a bplist ({Bytes} bytes)", Receiver.Name, response.Body.Length);
            return null;
        }
    }

    /// <inheritdoc />
    public Task TeardownAsync(CancellationToken cancellationToken) => TeardownWithResultAsync(cancellationToken);

    /// <summary>
    /// SPEC.md D7 / SPEC-control.md 4.9, 6.4: a per-stream TEARDOWN <c>{streams: [{streamID, type}]}</c> for every
    /// <c>streamID</c> the receiver returned (non-2xx is logged and ignored), then the session TEARDOWN <c>{}</c>, then the
    /// event channel is closed. A connection that is already closed (the receiver ended it) is not an error: the remaining
    /// steps are skipped and the event channel is still closed. The control connection itself is closed by
    /// <see cref="DisposeAsync"/>.
    /// </summary>
    public async Task<TeardownResult> TeardownWithResultAsync(CancellationToken cancellationToken)
    {
        // Each stream is forgotten only once its TEARDOWN was answered, so a TEARDOWN that times out or is cancelled can be
        // retried with the per-stream form (D7).
        (uint StreamId, StreamType Type)[] streams;
        lock (_lock)
        {
            streams = [.. _streams];
        }

        var streamResponses = new List<RtspResponse>();
        RtspResponse? sessionResponse = null;
        try
        {
            foreach (var (streamId, type) in streams)
            {
                var response = await Connection.SendAsync(SessionRequests.TeardownStream(Uri, streamId, type), cancellationToken).ConfigureAwait(false);
                streamResponses.Add(response);
                lock (_lock)
                {
                    _streams.Remove((streamId, type));
                }

                if (!response.IsSuccess)
                {
                    Logger.LogWarning("Per-stream TEARDOWN of stream {StreamId} on {Receiver} answered {Status}", streamId, Receiver.Name, response.StatusCode);
                }
            }

            sessionResponse = await Connection.SendAsync(SessionRequests.TeardownSession(Uri), cancellationToken).ConfigureAwait(false);
            if (!sessionResponse.IsSuccess)
            {
                Logger.LogWarning("Session TEARDOWN on {Receiver} answered {Status}", Receiver.Name, sessionResponse.StatusCode);
            }

            Logger.LogInformation(
                "TEARDOWN {Receiver}: {Streams} stream(s), session {Status}{Close}",
                Receiver.Name,
                streams.Length,
                sessionResponse.StatusCode,
                string.Equals(sessionResponse.Header(RtspHeaders.Connection), "close", StringComparison.OrdinalIgnoreCase) ? ", Connection: close" : string.Empty);
        }
        catch (IOException ex) when (Connection.Closed.IsCompleted)
        {
            // The receiver ended the session with the connection: nothing is left to tear down.
            lock (_lock)
            {
                _streams.Clear();
            }

            Logger.LogInformation("TEARDOWN {Receiver}: the connection is already closed ({Reason})", Receiver.Name, ex.Message);
        }
        finally
        {
            await CloseEventChannelAsync().ConfigureAwait(false);
            lock (_lock)
            {
                _sessionSetup = null;
            }
        }

        return new TeardownResult(streamResponses, sessionResponse);
    }

    /// <inheritdoc />
    /// <remarks>Closes the event channel and the control connection; does not send TEARDOWN (call <see cref="TeardownAsync"/> first).</remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await CloseEventChannelAsync().ConfigureAwait(false);
        await Connection.DisposeAsync().ConfigureAwait(false);
    }

    private async Task CloseEventChannelAsync()
    {
        EventChannel? channel;
        lock (_lock)
        {
            channel = _eventChannel;
            _eventChannel = null;
        }

        if (channel is not null)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<PairingSession> PairCoreAsync(IPairingStore store, Func<CancellationToken, Task<string>>? pinPrompt, CancellationToken cancellationToken)
    {
        var receiverId = Receiver.Id;
        if (await store.GetAsync(receiverId, cancellationToken).ConfigureAwait(false) is { } stored)
        {
            try
            {
                var verified = await PairingClient.VerifyAsync(Connection, stored, cancellationToken).ConfigureAwait(false);
                Logger.LogDebug("Pair-verify with {Receiver} succeeded with the stored pairing", Receiver.Name);
                return verified;
            }
            catch (PairingException ex) when (ex.Failure == PairingFailure.CredentialsRejected)
            {
                // SPEC-control.md 3.8: drop the stored pairing and pair again [owntone:airplay.c:3575-3586].
                Logger.LogWarning("Pair-verify with {Receiver} rejected ({Reason}); deleting the stored pairing", Receiver.Name, ex.Message);
                await store.DeleteAsync(receiverId, cancellationToken).ConfigureAwait(false);
            }
        }

        var requirement = Receiver.Capabilities.Pairing;
        switch (requirement)
        {
            case PairingRequirement.Disabled:
                throw new PairingException(PairingFailure.Protocol, $"{Receiver.Name} admits only HomeKit home members (acl=1 or act=2); pairing is not attempted (SPEC-control.md 1.6).");
            case PairingRequirement.Transient:
                try
                {
                    return await PairingClient.PairTransientAsync(Connection, cancellationToken).ConfigureAwait(false);
                }
                catch (PairingException ex) when (ex.Failure == PairingFailure.TransientRefused)
                {
                    Logger.LogInformation("Transient pairing refused by {Receiver} ({Reason}); falling back to the PIN flow", Receiver.Name, ex.Message);
                }

                break;
        }

        return await PinPairAsync(store, pinPrompt, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PairingSession> PinPairAsync(IPairingStore store, Func<CancellationToken, Task<string>>? pinPrompt, CancellationToken cancellationToken)
    {
        if (pinPrompt is null)
        {
            throw new PairingException(PairingFailure.TransientRefused, $"{Receiver.Name} requires PIN pairing and no PIN prompt was given.");
        }

        var controller = await store.GetControllerIdentityAsync(cancellationToken).ConfigureAwait(false);
        Logger.LogInformation("PIN pairing with {Receiver}: the receiver shows its PIN", Receiver.Name);
        var started = await PairingClient.StartPinPairingAsync(Connection, controller, _options.PinPairingMethod, cancellationToken).ConfigureAwait(false);
        var pin = await pinPrompt(cancellationToken).ConfigureAwait(false);
        var credentials = await PairingClient.FinishPinPairingAsync(started, pin, cancellationToken).ConfigureAwait(false);
        await store.SaveAsync(Receiver.Id, credentials, cancellationToken).ConfigureAwait(false);
        Logger.LogInformation("PIN pairing with {Receiver} stored (accessory {AccessoryId}); verifying", Receiver.Name, credentials.AccessoryId);

        // SPEC-control.md 3.6 step 8: pair-verify on the same connection (owntone [owntone:airplay.c:3686-3693]).
        return await PairingClient.VerifyAsync(Connection, credentials, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RtspResponse> SendMetadataItemAsync(RtspRequest request, string what, CancellationToken cancellationToken)
    {
        var response = await Connection.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            Logger.LogWarning("SET_PARAMETER {What} to {Receiver} answered {Status} {Reason}; ignored", what, Receiver.Name, response.StatusCode, response.ReasonPhrase);
        }

        return response;
    }
}
