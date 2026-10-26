using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Airplay2.Receiver.Pairing;
using Airplay2.Receiver.Session;
using Airplay2.Sender.Alac;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airplay2.FakeReceiver;

/// <summary>Shape of the <c>GET_PARAMETER volume</c> answer (SPEC-control.md 4.11).</summary>
public enum GetParameterShape
{
    /// <summary>shairport-sync: <c>"\r\nvolume: %.6f\r\n"</c>, with a leading blank line [sps:rtsp.c:3327-3332].</summary>
    Shairport,

    /// <summary>airplay2-receiver: <c>"volume: &lt;python str(float)&gt;\r\n"</c>, no leading line [ap2r:ap2-receiver.py:636-665].</summary>
    Ap2r,
}

/// <summary>Behaviour switches so tests can reproduce the quirks and failure modes listed in SPEC.md.</summary>
public sealed record FakeReceiverOptions
{
    /// <summary>Advertised name.</summary>
    public string Name { get; init; } = "Fake Receiver";

    /// <summary>
    /// The MAC-style device id used as <c>deviceID</c>/<c>pi</c> in <c>/info</c>, as <c>deviceid</c>/<c>pi</c>/<c>gid</c> in
    /// the TXT records and as the accessory pairing identifier sent in M6 and pair-verify M2 (airplay2-receiver uses
    /// <c>pi</c> as its pairing id [ap2r:ap2-receiver.py:34-36,276]). Random (locally administered) when null.
    /// </summary>
    public string? DeviceId { get; init; }

    /// <summary>The 64-bit features word (<c>/info</c> <c>features</c>, TXT <c>features</c>/<c>ft</c>). Default: receiver A's <c>0x1C340445F8A00</c> (a third-party AirPlay 2 speaker, srcvers 366.0) (bit 48 transient pairing set).</summary>
    public ulong Features { get; init; } = 0x1C340445F8A00UL;

    /// <summary>The status flags (<c>/info</c> <c>statusFlags</c>, TXT <c>flags</c>/<c>sf</c>). Default: receiver A's <c>0x404</c> (no PIN flag).</summary>
    public uint StatusFlags { get; init; } = 0x404;

    /// <summary>Accept transient pair-setup (feature bit 48). When false, transient M1 is answered with 470.</summary>
    public bool AllowTransientPairing { get; init; } = true;

    /// <summary>The PIN shown for regular pair-setup.</summary>
    public string Pin { get; init; } = "1234";

    /// <summary>Accept <c>Method = 1</c> (PairSetupWithAuth) in M1 (open question C-7.6 / probe P6); pair_ap refuses it with 470.</summary>
    public bool AllowPairSetupWithAuth { get; init; }

    /// <summary>Require <c>X-Apple-HKP</c> on pairing POSTs (open question C-7.11); a POST without it is answered with 470.</summary>
    public bool RequireHkpHeader { get; init; }

    /// <summary>
    /// Switch the control channel to encryption after pair-verify M4 (Apple receivers per pyatv and owntone). False
    /// emulates shairport-sync 5.5.2, whose cipher set-up after pair-verify is commented out [sps:rtsp.c:1725-1741]
    /// (SPEC-control.md 3.7 note); transient M4 always switches.
    /// </summary>
    public bool EncryptAfterPairVerify { get; init; } = true;

    /// <summary>
    /// Answer the session methods (SETUP, RECORD, SET_PARAMETER, ..., <c>/feedback</c>) with 470 on a connection that has not
    /// completed pairing. shairport-sync does not check this; it is on by default so that a sender which forgets to pair
    /// (and would send <c>shk</c> in plaintext, SPEC-control.md 4.4) fails loudly.
    /// </summary>
    public bool RequirePairing { get; init; } = true;

    /// <summary>Answer <c>POST /auth-setup</c> with 200 and 1076 zero bytes (SPEC-control.md 3.10); when false it is answered with 404.</summary>
    public bool AnswerAuthSetup { get; init; } = true;

    /// <summary>Answer an NTP session SETUP with 501 like shairport-sync (open question C-7.1 / A-Q3).</summary>
    public bool RefuseNtp { get; init; }

    /// <summary>Return a <c>streamID</c> in the stream SETUP response (shairport does not).</summary>
    public bool ReturnStreamId { get; init; } = true;

    /// <summary>Send <c>updateInfo</c> on the event channel right after it connects (shairport) instead of after RECORD (Apple TV).</summary>
    public bool UpdateInfoOnConnect { get; init; } = true;

    /// <summary>
    /// Send the <c>updateInfo</c> request as one HAP frame larger than 1024 bytes (padding the plist when needed), as an
    /// Apple-SDK receiver may, to exercise the sender's receive rule (SPEC-control.md 2.5, D9).
    /// </summary>
    public bool LargeEventFrames { get; init; }

    /// <summary>Write <c>CSeq</c> on event-channel requests (Apple TV: <c>CSeq: 0</c>; shairport-sync: none, SPEC-control.md 4.3).</summary>
    public bool EventRequestsCarryCSeq { get; init; }

    /// <summary>Include <c>initialVolume</c> in GET /info (null: omit it, as receiver A does unauthenticated). Also the starting volume.</summary>
    public double? InitialVolume { get; init; } = -20.0;

    /// <summary>The shape of the <c>GET_PARAMETER volume</c> answer.</summary>
    public GetParameterShape GetParameterShape { get; init; } = GetParameterShape.Shairport;

    /// <summary>Drop every n-th RTP packet to provoke retransmit requests (0 = never). The first packet of a stream and retransmissions are never dropped.</summary>
    public int DropEveryNthPacket { get; init; }

    /// <summary>Send retransmit requests for gaps.</summary>
    public bool RequestRetransmits { get; init; } = true;

    /// <summary>
    /// Send the 16-byte retransmit request form with an NTP time (airplay2-receiver) instead of the 8-byte form
    /// (shairport-sync, SPEC-audio.md 2.4). The NTP field holds the receiver's clock at sending time, not the time of the
    /// first missing packet as airplay2-receiver puts there; senders ignore the field, so its value is not checkable here.
    /// </summary>
    public bool ExtendedRetransmitRequests { get; init; }

    /// <summary>
    /// Send retransmit requests from a second UDP socket instead of the announced control port, to exercise the sender's
    /// address-first lookup (SPEC-audio.md 2.4). Wrapped 0xD6 replies are accepted on both sockets.
    /// </summary>
    public bool RetransmitFromSecondSocket { get; init; }

    /// <summary>
    /// For NTP sessions, send 0xD2 timing requests to the sender's announced <c>timingPort</c> once a stream is set up: three
    /// requests 300 ms apart, then one every 3 s (shairport-sync's requester, SPEC-audio.md 2.2 [sps:rtp.c:595-664]).
    /// </summary>
    public bool SendTimingRequests { get; init; } = true;

    /// <summary>
    /// The status of the answer to <c>SET_PARAMETER</c> with DMAP text (200 by default; the srcvers-366 speaker answers 500,
    /// SPEC.md P9). The item is still recorded as a <see cref="FakeReceiverEvent.Metadata"/> event.
    /// </summary>
    public int DmapStatus { get; init; } = 200;

    /// <summary>Close the event channel when a request is not answered within this time.</summary>
    public TimeSpan EventAnswerTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The <c>Server</c> header on every response (receiver A's value by default).</summary>
    public string ServerHeader { get; init; } = "AirTunes/366.0";

    /// <summary>The /info plist to serve; defaults to the recorded receiver A response with the name replaced.</summary>
    public PlistDictionary? InfoPlist { get; init; }

    /// <summary>Logger.</summary>
    public ILogger Logger { get; init; } = NullLogger.Instance;
}

/// <summary>What the fake receiver observed, for assertions.</summary>
public abstract record FakeReceiverEvent
{
    /// <summary>A control-connection request, before it is handled. <paramref name="Encrypted"/> is the channel state it arrived in.</summary>
    public sealed record Request(string Method, string Uri, bool Encrypted, IReadOnlyDictionary<string, string> Headers, byte[] Body) : FakeReceiverEvent;

    /// <summary>A pairing procedure completed (<paramref name="Kind"/> = <c>Transient</c> or <c>Verified</c>).</summary>
    public sealed record Paired(string Kind, byte[] SharedSecret) : FakeReceiverEvent;

    /// <summary>An accepted session SETUP.</summary>
    public sealed record SessionSetup(PlistDictionary Body) : FakeReceiverEvent;

    /// <summary>An accepted stream SETUP.</summary>
    public sealed record StreamSetup(PlistDictionary Body) : FakeReceiverEvent;

    /// <summary><c>SET_PARAMETER volume</c>.</summary>
    public sealed record Volume(double Db) : FakeReceiverEvent;

    /// <summary><c>FLUSH</c> with its <c>RTP-Info</c> values (0 when absent).</summary>
    public sealed record Flush(ushort Sequence, uint Timestamp) : FakeReceiverEvent;

    /// <summary><c>TEARDOWN</c>: <paramref name="Session"/> true for the <c>{}</c> form, false for the per-stream form.</summary>
    public sealed record Teardown(bool Session) : FakeReceiverEvent;

    /// <summary>One decrypted and decoded RTP packet (also for answered retransmissions).</summary>
    public sealed record AudioPacket(ushort Sequence, uint Timestamp, bool Marker, short[] Pcm) : FakeReceiverEvent;

    /// <summary>A 0xD4 NTP sync packet.</summary>
    public sealed record Sync(bool First, uint NowFrame, ulong NtpTime, uint NextFrame) : FakeReceiverEvent;

    /// <summary>A 0xD3 reply to one of our timing requests.</summary>
    public sealed record TimingReply(ulong Origin, ulong Receive, ulong Transmit) : FakeReceiverEvent;

    /// <summary>A packet we asked for arrived, wrapped (0xD6 on the control port) or raw (on the data port).</summary>
    public sealed record RetransmitAnswered(ushort Sequence, bool Wrapped) : FakeReceiverEvent;

    /// <summary>A 0xD7 PTP sync packet (SPEC-audio.md 4.4).</summary>
    public sealed record PtpSync(bool First, uint NowFrame, ulong PtpTimeNanos, uint NextFrame, ulong ClockId) : FakeReceiverEvent;

    /// <summary>A control connection was accepted.</summary>
    public sealed record ConnectionOpened(IPEndPoint Remote) : FakeReceiverEvent;

    /// <summary>A control connection closed (sender closed it, TEARDOWN, kick, dispose).</summary>
    public sealed record ConnectionClosed(IPEndPoint Remote, string Reason) : FakeReceiverEvent;

    /// <summary>The sender connected to the event port.</summary>
    public sealed record EventChannelConnected(IPEndPoint Remote) : FakeReceiverEvent;

    /// <summary>The event channel closed.</summary>
    public sealed record EventChannelClosed(string Reason) : FakeReceiverEvent;

    /// <summary>
    /// A <c>POST /command</c> we sent on the event channel was answered (<paramref name="StatusCode"/> 0: no answer within
    /// the event timeout, the channel was closed). <paramref name="LargeFrame"/>: the request went out as one HAP frame above 1024 bytes.
    /// </summary>
    public sealed record EventCommandAnswered(string Type, int StatusCode, bool LargeFrame) : FakeReceiverEvent;

    /// <summary><c>SETPEERS</c> with its address list.</summary>
    public sealed record Peers(IReadOnlyList<string> Addresses) : FakeReceiverEvent;

    /// <summary><c>SET_PARAMETER progress: start/current/end</c>.</summary>
    public sealed record Progress(uint Start, uint Current, uint End) : FakeReceiverEvent;

    /// <summary><c>SET_PARAMETER</c> DMAP metadata: the items inside <c>mlit</c> (strings for text tags, integers by width, raw bytes otherwise).</summary>
    public sealed record Metadata(IReadOnlyDictionary<string, object> Items, uint? RtpTime) : FakeReceiverEvent;

    /// <summary><c>SET_PARAMETER</c> artwork.</summary>
    public sealed record Artwork(string ContentType, byte[] Image, uint? RtpTime) : FakeReceiverEvent;

    /// <summary>We sent a 0xD5 retransmit request.</summary>
    public sealed record RetransmitRequested(ushort FirstSequence, ushort Count) : FakeReceiverEvent;

    /// <summary>An RTP packet dropped on purpose (<see cref="FakeReceiverOptions.DropEveryNthPacket"/>).</summary>
    public sealed record PacketDropped(ushort Sequence) : FakeReceiverEvent;

    /// <summary>An RTP packet that failed authentication or ALAC decoding, or arrived without a usable <c>shk</c>.</summary>
    public sealed record BadAudioPacket(ushort Sequence, int Length, string Reason) : FakeReceiverEvent;

    /// <summary>A control-port datagram that is neither 0xD4, 0xD6 nor 0xD7 (logged and ignored, SPEC-audio.md 2.4).</summary>
    public sealed record UnknownControlPacket(byte Type, int Length) : FakeReceiverEvent;
}

/// <summary>
/// An in-process AirPlay 2 receiver on loopback: the receiver side of transient and regular HomeKit pairing (a thin
/// adapter over <c>Airplay2.Receiver.Pairing.ReceiverPairing</c> and the internal <c>HapAccessoryPairing</c> owned by the
/// pairing package; the fake never re-implements SRP/Ed25519/X25519), the HAP-encrypted control channel, the event channel (sends <c>updateInfo</c> and
/// <c>sendMediaRemoteCommand</c>), SETUP/RECORD/SETPEERS/SET_PARAMETER/GET_PARAMETER/FLUSH/TEARDOWN/feedback, and
/// UDP control/timing/data ports that decrypt and ALAC-decode the realtime stream. It records everything it sees in
/// <see cref="Events"/> so tests can assert on the wire sequence. Not an mDNS responder: tests construct the
/// <c>DiscoveredReceiver</c> directly from <see cref="Describe"/>.
/// </summary>
/// <remarks>
/// <para>
/// Several control connections may be open at once (pairing on one, session on the next); each has its own pairing
/// state, session and event channel. The UDP ports are receiver-wide and serve the most recent stream SETUP, as a
/// receiver plays one stream at a time.
/// </para>
/// <para>
/// Background work (accept loops, connection monitors, timing requests, event-channel sends that must not block a
/// response) is tracked, its failures logged, and awaited by <see cref="DisposeAsync"/>.
/// </para>
/// </remarks>
public sealed partial class FakeReceiver : IAsyncDisposable
{
    private const string SpeakerAInfoResource = "Airplay2.FakeReceiver.info-response.bplist";

    private static readonly Lazy<byte[]> SpeakerAInfo = new(LoadSpeakerAInfo);

    private readonly Channel<FakeReceiverEvent> _events = Channel.CreateUnbounded<FakeReceiverEvent>();
    private readonly ILogger _logger;
    private readonly ReceiverPairing _pairing;
    private readonly MasterClock _clock = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _lock = new();
    private readonly List<ControlConnection> _connections = [];
    private readonly ConcurrentDictionary<Task, string> _background = new();
    private readonly string _serialNumber = Guid.NewGuid().ToString();
    private Socket? _listener;
    private int _nextConnectionId;
    private double _volumeDb;
    private readonly EventChannelHost _eventChannelHost;
    private EventChannelServer? _latestEventChannel;
    private string _groupId;
    private bool _groupContainsGroupLeader;
    private int _started;
    private int _disposed;

    /// <summary>Creates a receiver with the given options.</summary>
    public FakeReceiver(FakeReceiverOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.DropEveryNthPacket);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.EventAnswerTimeout, TimeSpan.Zero);
        ArgumentException.ThrowIfNullOrEmpty(options.Pin);
        Options = options;
        _logger = options.Logger;
        DeviceId = options.DeviceId ?? RandomDeviceId();
        _groupId = DeviceId;
        _volumeDb = options.InitialVolume ?? 0.0;
        _eventChannelHost = new EventChannelHost(this);
        _realtime = CreateRealtimeReceiver(options);
        var identity = new ControllerIdentity(DeviceId, RandomNumberGenerator.GetBytes(32));
        PublicKey = identity.KeyPair.PublicKey;
        var accessory = new HapAccessoryOptions(identity, options.Pin, options.AllowTransientPairing)
        {
            AllowPairSetupWithAuth = options.AllowPairSetupWithAuth,
        };
        var pairingOptions = new ReceiverPairingOptions
        {
            RequireHkpHeader = options.RequireHkpHeader,
            EncryptAfterPairVerify = options.EncryptAfterPairVerify,
            Name = options.Name,
        };
        _pairing = new ReceiverPairing(accessory, new InMemoryControllerStore(), pairingOptions, _logger);
    }

    /// <summary>The options.</summary>
    public FakeReceiverOptions Options { get; }

    /// <summary>Everything observed, in order.</summary>
    public ChannelReader<FakeReceiverEvent> Events => _events.Reader;

    /// <summary>The TCP endpoint of the control port once started.</summary>
    public IPEndPoint ControlEndpoint { get; private set; } = new(IPAddress.Loopback, 0);

    /// <summary>The receiver's long-term Ed25519 public key (<c>pk</c>).</summary>
    public byte[] PublicKey { get; }

    /// <summary>The ALAC decoder used for the data port (not thread-safe: the receiver uses it under its audio lock; do not decode with it while a stream runs).</summary>
    public AlacDecoder Decoder => _realtime.Decoder;

    /// <summary>The device id (<c>deviceid</c>, <c>pi</c>, and the accessory pairing identifier).</summary>
    public string DeviceId { get; }

    /// <summary>The receiver's current volume in dB (the last <c>SET_PARAMETER volume</c>, or <see cref="ChangeVolumeAsync"/>).</summary>
    public double VolumeDb
    {
        get
        {
            lock (_lock)
            {
                return _volumeDb;
            }
        }
    }

    /// <summary>The number of controllers that completed regular pair-setup with this receiver.</summary>
    public int PairedControllerCount => _pairing.ControllerCount;

    /// <summary>The UDP data port announced in stream SETUP responses (0 before <see cref="StartAsync"/>).</summary>
    public int DataPort => _realtime.DataPort;

    /// <summary>The UDP control port announced in stream SETUP responses (0 before <see cref="StartAsync"/>).</summary>
    public int ControlPort => _realtime.ControlPort;

    /// <summary>The number of open control connections.</summary>
    public int OpenConnections
    {
        get
        {
            lock (_lock)
            {
                return _connections.Count;
            }
        }
    }

    /// <summary>Forgets a paired controller (as after a factory reset), so its next pair-verify fails; false when it was not paired.</summary>
    public bool RemovePairing(string controllerId) => _pairing.RemoveController(controllerId);

    /// <summary>Binds the control TCP port and the UDP ports on loopback.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return Task.CompletedTask;
        }

        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(16);
        }
        catch
        {
            listener.Dispose();
            throw;
        }

        _listener = listener;
        ControlEndpoint = (IPEndPoint)listener.LocalEndPoint!;
        StartUdp();
        Track(AcceptLoopAsync(listener, _stopping.Token), "control accept loop");
        _logger.LogInformation(
            "Fake receiver {Name} ({DeviceId}) listening on {Endpoint}, data UDP {DataPort}, control UDP {ControlPort}",
            Options.Name,
            DeviceId,
            ControlEndpoint,
            DataPort,
            ControlPort);
        return Task.CompletedTask;
    }

    /// <summary>The <c>_airplay._tcp</c> and <c>_raop._tcp</c> TXT strings this receiver would advertise, and its address/port.</summary>
    public (IReadOnlyList<string> AirplayTxt, IReadOnlyList<string> RaopTxt, IPEndPoint Endpoint) Describe() => DescribeFrom(BuildBaseInfo());

    /// <summary><see cref="Describe"/> from an already built <c>/info</c> dictionary (model, versions, manufacturer).</summary>
    private (IReadOnlyList<string> AirplayTxt, IReadOnlyList<string> RaopTxt, IPEndPoint Endpoint) DescribeFrom(PlistDictionary info)
    {
        string gid;
        bool gcgl;
        lock (_lock)
        {
            gid = _groupId;
            gcgl = _groupContainsGroupLeader;
        }

        var features = FormatFeatures(Options.Features);
        var flags = "0x" + Options.StatusFlags.ToString("X", CultureInfo.InvariantCulture);
        var model = info.GetString("model") ?? Options.Name;
        var srcvers = info.GetString("sourceVersion") ?? "366.0";
        var fv = "p20." + (info.GetString("firmwareRevision") ?? "1.0");
        var pk = Convert.ToHexStringLower(PublicKey);

        // Receiver A's records (docs/airplay2/probes/browse.json, SPEC-control.md 1.7) with this receiver's identity.
        IReadOnlyList<string> airplay =
        [
            "acl=0",
            "deviceid=" + DeviceId,
            "features=" + features,
            "rsf=0x0",
            "fv=" + fv,
            "flags=" + flags,
            "model=" + model,
            "manufacturer=" + (info.GetString("manufacturer") ?? "Fake"),
            "serialNumber=" + _serialNumber,
            "protovers=" + (info.GetString("protocolVersion") ?? "1.1"),
            "srcvers=" + srcvers,
            "pi=" + DeviceId,
            "gid=" + gid,
            "gcgl=" + (gcgl ? "1" : "0"),
            "pk=" + pk,
        ];
        IReadOnlyList<string> raop =
        [
            "cn=0,1",
            "da=true",
            "et=0,4",
            "ft=" + features,
            "fv=" + fv,
            "md=0,1,2",
            "am=" + model,
            "sf=" + flags,
            "tp=UDP",
            "vn=65537",
            "vs=" + srcvers,
            "pk=" + pk,
        ];
        return (airplay, raop, ControlEndpoint);
    }

    /// <summary>Sends <c>{type: sendMediaRemoteCommand, value: command}</c> on the event channel (simulates the device's buttons).</summary>
    /// <exception cref="InvalidOperationException">No event channel is connected.</exception>
    /// <exception cref="TimeoutException">The sender did not answer within <see cref="FakeReceiverOptions.EventAnswerTimeout"/>; the channel was closed.</exception>
    /// <exception cref="IOException">The channel closed while waiting for the answer.</exception>
    public async Task SendRemoteCommandAsync(string command, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);
        var channel = CurrentEventChannel() ?? throw new InvalidOperationException("No event channel is connected.");
        var body = new PlistDictionary { ["type"] = "sendMediaRemoteCommand", ["value"] = command };
        await channel.SendCommandAsync(body, large: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Changes the receiver's own volume and sends <c>updateInfo</c> with the new <c>initialVolume</c> (one hypothesis for C-7.4).</summary>
    /// <remarks>Without a connected event channel only the stored volume changes (visible through <c>GET_PARAMETER</c>).</remarks>
    public async Task ChangeVolumeAsync(double db, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _volumeDb = db;
        }

        if (CurrentEventChannel() is { } channel)
        {
            await channel.SendUpdateInfoAsync(forceVolume: true, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _logger.LogDebug("Fake receiver {Name}: volume changed to {Db} dB without an event channel", Options.Name, db);
        }
    }

    /// <summary>Closes the control connection as if another sender took over.</summary>
    public async Task KickAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ControlConnection[] connections;
        lock (_lock)
        {
            connections = [.. _connections];
        }

        foreach (var connection in connections)
        {
            await connection.CloseAsync("kicked: another sender took over").ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener?.Dispose();

        ControlConnection[] connections;
        lock (_lock)
        {
            connections = [.. _connections];
        }

        foreach (var connection in connections)
        {
            await connection.CloseAsync("receiver disposed").ConfigureAwait(false);
        }

        await DrainBackgroundAsync().ConfigureAwait(false);
        await StopUdpAsync().ConfigureAwait(false);
        await DrainBackgroundAsync().ConfigureAwait(false);
        _stopping.Dispose();
        _events.Writer.TryComplete();
    }

    /// <summary>Records an observation.</summary>
    internal void Write(FakeReceiverEvent observation) => _events.Writer.TryWrite(observation);

    /// <summary>Tracks a background task: its failure is logged, and <see cref="DisposeAsync"/> awaits it.</summary>
    internal void Track(Task task, string what)
    {
        _background[task] = what;
        _ = task.ContinueWith(
            static (t, state) =>
            {
                var (self, name) = ((FakeReceiver, string))state!;
                self._background.TryRemove(t, out _);
                if (t.IsFaulted)
                {
                    self._logger.LogError(t.Exception, "Fake receiver {Name}: {What} failed", self.Options.Name, name);
                }
            },
            (this, what),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>The logger.</summary>
    internal ILogger Logger => _logger;

    /// <summary>
    /// The <c>/info</c> dictionary: the recorded receiver A plist (SPEC-control.md 1.8) with <c>name</c>, <c>deviceID</c>,
    /// <c>pi</c>, <c>features</c> and <c>statusFlags</c> replaced, or <see cref="FakeReceiverOptions.InfoPlist"/> as given;
    /// plus <c>initialVolume</c> (shairport adds the current volume [sps:rtsp.c:1137]) and, for the stage-1 request and
    /// the event channel, <c>txtAirPlay</c> (Pascal strings of the TXT record [sps:rtsp.c:962-993, 999-1104]).
    /// </summary>
    internal PlistDictionary BuildInfo(bool includeTxt, bool includeVolume)
    {
        var info = BuildBaseInfo();

        // From the base dictionary, so the TXT record matches Describe() and the plist is decoded only once.
        var txt = includeTxt ? TxtAirPlay(info) : null;
        if (includeVolume)
        {
            info["initialVolume"] = VolumeDb;
        }

        if (txt is not null)
        {
            info["txtAirPlay"] = txt;
        }

        return info;
    }

    /// <summary>The <c>/info</c> dictionary without <c>initialVolume</c> and <c>txtAirPlay</c> (see <see cref="BuildInfo"/>).</summary>
    private PlistDictionary BuildBaseInfo()
    {
        if (Options.InfoPlist is { } custom)
        {
            return new PlistDictionary(custom);
        }

        var info = BinaryPlist.DecodeDictionary(SpeakerAInfo.Value);
        info["name"] = Options.Name;
        info["deviceID"] = DeviceId;
        info["pi"] = DeviceId;
        info["features"] = unchecked((long)Options.Features);
        info["statusFlags"] = (long)Options.StatusFlags;
        return info;
    }

    /// <summary>The event channel of the most recent session whose channel is connected, or null.</summary>
    internal EventChannelServer? CurrentEventChannel()
    {
        lock (_lock)
        {
            return _latestEventChannel is { IsConnected: true } channel ? channel : null;
        }
    }

    /// <summary>Remembers the event channel that connected last (the target of the public event methods).</summary>
    internal void SetLatestEventChannel(EventChannelServer channel)
    {
        lock (_lock)
        {
            _latestEventChannel = channel;
        }
    }

    /// <summary>The library event channel's options from <see cref="Options"/> (SPEC-receiver.md 4.4).</summary>
    internal EventChannelServerOptions EventChannelOptions() => new()
    {
        BindAddress = IPAddress.Loopback,
        ServerHeader = Options.ServerHeader,
        Name = Options.Name,
        AnswerTimeout = Options.EventAnswerTimeout,
        SendCSeqOnRequests = Options.EventRequestsCarryCSeq,
        UpdateInfoOnConnect = Options.UpdateInfoOnConnect,
        LargeUpdateInfoFrames = Options.LargeEventFrames,
    };

    /// <summary>Stores the volume of a <c>SET_PARAMETER volume</c>.</summary>
    internal void SetVolume(double db)
    {
        lock (_lock)
        {
            _volumeDb = db;
        }
    }

    /// <summary>Applies the group fields of a session SETUP to the advertised <c>gid</c>/<c>gcgl</c> [sps:rtsp.c:2501-2526, 2663-2669].</summary>
    internal void SetGroup(string? groupUuid, bool containsGroupLeader)
    {
        lock (_lock)
        {
            _groupId = groupUuid ?? DeviceId;
            _groupContainsGroupLeader = containsGroupLeader;
        }
    }

    private async Task AcceptLoopAsync(Socket listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptAsync(stop).ConfigureAwait(false);
            }
            catch (Exception ex) when ((ex is OperationCanceledException or ObjectDisposedException) && stop.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex) when (stop.IsCancellationRequested || ex.SocketErrorCode == SocketError.OperationAborted)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "Fake receiver {Name}: accept failed", Options.Name);
                continue;
            }

            var connection = new ControlConnection(this, _pairing.ForConnection(), Interlocked.Increment(ref _nextConnectionId));
            lock (_lock)
            {
                _connections.Add(connection);
            }

            connection.Start(socket);
            Write(new FakeReceiverEvent.ConnectionOpened(connection.Remote));
            Track(MonitorAsync(connection), $"control connection #{connection.Id} monitor");
        }
    }

    private async Task MonitorAsync(ControlConnection connection)
    {
        var reason = await connection.Closed.ConfigureAwait(false);
        _logger.LogDebug("Fake receiver {Name}: control connection #{Id} from {Remote} closed: {Reason}", Options.Name, connection.Id, connection.Remote, reason);
        Write(new FakeReceiverEvent.ConnectionClosed(connection.Remote, reason));

        // Then the receiver's view of the session ends: event channel closed, stream stopped [sps:ap2_event_receiver.c:37-59].
        await connection.CleanupAsync().ConfigureAwait(false);
        lock (_lock)
        {
            _connections.Remove(connection);
        }
    }

    private async Task DrainBackgroundAsync()
    {
        for (var round = 0; round < 8; round++)
        {
            var pending = _background.Keys.ToArray();
            if (pending.Length == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Failures were logged by Track's continuation.
            }
        }
    }

    private byte[] TxtAirPlay(PlistDictionary info)
    {
        var (airplay, _, _) = DescribeFrom(info);
        var output = new List<byte>(512);
        foreach (var entry in airplay)
        {
            var bytes = Encoding.UTF8.GetBytes(entry);
            var length = Math.Min(bytes.Length, byte.MaxValue);
            output.Add((byte)length);
            output.AddRange(bytes.AsSpan(0, length));
        }

        return [.. output];
    }

    private static string FormatFeatures(ulong features)
    {
        var low = (uint)features;
        var high = (uint)(features >> 32);
        var text = "0x" + low.ToString("X", CultureInfo.InvariantCulture);
        return high == 0 ? text : text + ",0x" + high.ToString("X", CultureInfo.InvariantCulture);
    }

    private static string RandomDeviceId()
    {
        var mac = RandomNumberGenerator.GetBytes(6);
        mac[0] = (byte)((mac[0] & 0xFC) | 0x02); // locally administered, unicast
        return string.Join(':', mac.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
    }

    /// <summary>Connects the library's <see cref="EventChannelServer"/> to this receiver's info, background tracking and <see cref="Events"/>.</summary>
    private sealed class EventChannelHost(FakeReceiver owner) : IEventChannelHost
    {
        public PlistDictionary BuildUpdateInfo(bool forceVolume) =>
            owner.BuildInfo(includeTxt: true, includeVolume: forceVolume || owner.Options.InitialVolume is not null);

        public void Track(Task task, string what) => owner.Track(task, what);

        public void OnConnected(EventChannelServer channel, IPEndPoint remote)
        {
            owner.SetLatestEventChannel(channel);
            owner.Write(new FakeReceiverEvent.EventChannelConnected(remote));
        }

        public void OnClosed(EventChannelServer channel, string reason) => owner.Write(new FakeReceiverEvent.EventChannelClosed(reason));

        public void OnCommandAnswered(EventChannelServer channel, string type, int statusCode, bool largeFrame) =>
            owner.Write(new FakeReceiverEvent.EventCommandAnswered(type, statusCode, largeFrame));
    }

    private static byte[] LoadSpeakerAInfo()
    {
        using var stream = typeof(FakeReceiver).Assembly.GetManifestResourceStream(SpeakerAInfoResource)
            ?? throw new InvalidOperationException($"Embedded resource {SpeakerAInfoResource} is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
