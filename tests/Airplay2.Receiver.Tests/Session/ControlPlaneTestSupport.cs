using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Airplay2.Receiver.Pairing;
using Airplay2.Receiver.Playout;
using Airplay2.Receiver.Session;
using Airplay2.Receiver.Timing;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Airplay2.Receiver.Tests.Session;

/// <summary>
/// Loopback harness for the control plane (SPEC-receiver.md 11.1): the product <see cref="ReceiverControlPlane"/> on
/// 127.0.0.1 port 0 with a recording <see cref="IControlPlaneHost"/>, driven by our own sender's RTSP connection and HAP
/// pairing client. Identities are synthetic.
/// </summary>
internal static class ControlPlaneTestSupport
{
    public static readonly SenderIdentity Sender = new("loopback sender", "02:00:00:00:0A:01", "02:00:00:00:0A:02", Model: "TestPhone1,1");

    public const ulong SenderClockId = 0x0200_00FF_FE00_0A01UL;

    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    public static AirplayReceiverOptions Options(string? password = null, IControllerStore? store = null, TimeSpan? timeout = null, ReceiverIdentity? identity = null) => new()
    {
        Name = "Loopback Receiver",
        Identity = identity ?? ReceiverIdentity.Create(),
        Password = password,
        BindAddress = IPAddress.Loopback,
        RtspPort = 0,
        SessionTimeout = timeout ?? TimeSpan.FromSeconds(30),
        ControllerStore = store,
    };

    public static async Task<(ReceiverControlPlane Plane, TestHost Host)> StartAsync(AirplayReceiverOptions? options = null, TestHost? host = null)
    {
        host ??= new TestHost();
        var plane = new ReceiverControlPlane(options ?? Options(), host, NullLoggerFactory.Instance);
        await plane.StartAsync(CancellationToken.None);
        return (plane, host);
    }

    public static SessionSetupRequest PtpSession() =>
        new(Sender, TimingProtocol.Ptp, 0, "11111111-2222-3333-4444-555555555555", null, GroupContainsGroupLeader: false, IsMultiSelectAirPlay: true, [IPAddress.Loopback], SenderClockId);

    public static SessionSetupRequest NtpSession() =>
        new(Sender, TimingProtocol.Ntp, 6002, "11111111-2222-3333-4444-555555555555", null, GroupContainsGroupLeader: false, IsMultiSelectAirPlay: true, null, null);

    public static async Task<T> WaitForAsync<T>(Func<T?> probe, string what)
        where T : class
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } value)
            {
                return value;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }
}

/// <summary>Our sender's side of one control connection (SPEC-receiver.md 11.1: <c>RtspConnection</c>, <c>HapPairingClient</c>, <c>SessionRequests</c>).</summary>
internal sealed class TestSender : IAsyncDisposable
{
    private TestSender(RtspConnection connection)
    {
        Connection = connection;
    }

    public RtspConnection Connection { get; }

    public HapPairingClient Client { get; } = new(NullLogger<HapPairingClient>.Instance);

    public PairingSession? Pairing { get; private set; }

    public string Uri => Connection.SessionUri;

    public static async Task<TestSender> ConnectAsync(ReceiverControlPlane plane)
    {
        var connection = await RtspConnection.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, plane.Port),
            new RtspConnectionOptions { RequestTimeout = TimeSpan.FromSeconds(10) },
            NullLogger.Instance,
            CancellationToken.None);
        return new TestSender(connection);
    }

    public static async Task<TestSender> ConnectPairedAsync(ReceiverControlPlane plane)
    {
        var sender = await ConnectAsync(plane);
        await sender.PairTransientAsync();
        return sender;
    }

    public async Task PairTransientAsync()
    {
        var session = await Client.PairTransientAsync(Connection, CancellationToken.None);
        Use(session);
    }

    public void Use(PairingSession session)
    {
        Connection.EnableEncryption(session.ControlKeys);
        Pairing = session;
    }

    public Task<RtspResponse> SendAsync(RtspRequest request) => Connection.SendAsync(request, CancellationToken.None);

    public Task<RtspResponse> SessionSetupAsync(SessionSetupRequest? request = null) =>
        SendAsync(RtspRequest.WithPlist("SETUP", Uri, SessionRequests.SessionSetupBody(request ?? ControlPlaneTestSupport.PtpSession(), Connection.LocalAddress)));

    public Task<RtspResponse> StreamSetupAsync(StreamSetupRequest request) =>
        SendAsync(RtspRequest.WithPlist("SETUP", Uri, SessionRequests.StreamSetupBody(request, Connection.SessionId)));

    public Task<RtspResponse> StreamSetupAsync(PlistDictionary body) => SendAsync(RtspRequest.WithPlist("SETUP", Uri, body));

    public StreamSetupRequest Realtime(int controlPort = 6001) => new(StreamType.Realtime, Pairing!.DefaultStreamKey, controlPort);

    public async Task<PlistDictionary> SetUpSessionAsync()
    {
        var response = await SessionSetupAsync();
        Assert.Equal(200, response.StatusCode);
        return response.PlistBody();
    }

    public async Task<PlistDictionary> SetUpRealtimeStreamAsync()
    {
        var response = await StreamSetupAsync(Realtime());
        Assert.Equal(200, response.StatusCode);
        return (PlistDictionary)response.PlistBody().GetArray("streams")![0]!;
    }

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}

/// <summary>A recording <see cref="IControlPlaneHost"/> whose streams bind real loopback sockets on port 0.</summary>
internal sealed class TestHost : IControlPlaneHost
{
    private readonly ConcurrentQueue<ReceiverEvent> _events = new();

    public bool AacAvailable { get; set; } = true;

    public RecordingOutput Recorder { get; } = new();

    public IReceiverAudioOutput Output => Recorder;

    public int? OutputLevel { get; set; }

    public ConcurrentQueue<FakeTiming> Timings { get; } = new();

    public ConcurrentQueue<FakeStream> Streams { get; } = new();

    public Func<ReceiverStreamRequest, Exception?>? FailStream { get; set; }

    public IReadOnlyList<ReceiverEvent> Events => [.. _events];

    public IEnumerable<T> EventsOf<T>()
        where T : ReceiverEvent => _events.OfType<T>();

    public Task<T> WaitForEventAsync<T>(Func<T, bool>? match = null)
        where T : ReceiverEvent => ControlPlaneTestSupport.WaitForAsync(() => EventsOf<T>().FirstOrDefault(e => match?.Invoke(e) ?? true), typeof(T).Name);

    public PlistDictionary BuildInfo(IPEndPoint requester, bool includeTxt, bool streamActive)
    {
        var info = new PlistDictionary
        {
            ["name"] = "Loopback Receiver",
            ["statusFlags"] = streamActive ? 0x4L | (1L << 17) : 0x4L,
            ["senderAddress"] = requester.ToString(),
        };
        if (includeTxt)
        {
            info["txtAirPlay"] = new byte[] { 0x05, (byte)'a', (byte)'c', (byte)'l', (byte)'=', (byte)'0' };
        }

        return info;
    }

    public ValueTask<int?> GetOutputLevelAsync(CancellationToken cancellationToken) => ValueTask.FromResult(OutputLevel);

    public void Publish(ReceiverEvent receiverEvent) => _events.Enqueue(receiverEvent);

    public ValueTask<ISenderTiming?> StartTimingAsync(SenderTimingPeer peer, CancellationToken cancellationToken)
    {
        var timing = new FakeTiming(peer);
        Timings.Enqueue(timing);
        return ValueTask.FromResult<ISenderTiming?>(timing);
    }

    public ValueTask<IReceiverStream> CreateStreamAsync(ReceiverStreamRequest request, ISenderTiming? timing, CancellationToken cancellationToken)
    {
        if (FailStream?.Invoke(request) is { } failure)
        {
            throw failure;
        }

        var stream = new FakeStream(request);
        Streams.Enqueue(stream);
        return ValueTask.FromResult<IReceiverStream>(stream);
    }
}

/// <summary>A PTP side that records its peers.</summary>
internal sealed class FakeTiming(SenderTimingPeer peer) : ISenderTiming
{
    public SenderTimingPeer Peer { get; } = peer;

    public ConcurrentQueue<IPAddress> Peers { get; } = new();

    public bool Disposed { get; private set; }

    public IPtpSlave? Slave => null;

    public void AddPeers(IReadOnlyList<IPAddress> addresses)
    {
        foreach (var address in addresses)
        {
            Peers.Enqueue(address);
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>A stream that binds real loopback sockets (UDP data/control for 96, TCP data and UDP control for 103) and records the playout calls.</summary>
internal sealed class FakeStream : IReceiverStream
{
    private readonly Socket _data;
    private readonly Socket _control;
    private readonly ConcurrentQueue<string> _calls = new();

    public FakeStream(ReceiverStreamRequest request)
    {
        Request = request;
        _control = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _control.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        if (request.Type == StreamType.Realtime)
        {
            _data = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _data.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        }
        else
        {
            _data = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _data.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _data.Listen(1);
        }
    }

    public ReceiverStreamRequest Request { get; }

    public IReadOnlyList<string> Calls => [.. _calls];

    public bool Disposed { get; private set; }

    public StreamType Type => Request.Type;

    public int DataPort => ((IPEndPoint)_data.LocalEndPoint!).Port;

    public int ControlPort => ((IPEndPoint)_control.LocalEndPoint!).Port;

    public long? AudioBufferSize => Request.Type == StreamType.Buffered ? 8388608 : null;

    public void SetAnchor(uint rtpTime, ulong networkTimeNanos, ulong timelineId) => _calls.Enqueue(FormattableString.Invariant($"anchor {rtpTime} {networkTimeNanos} {timelineId:X}"));

    public void Pause() => _calls.Enqueue("pause");

    public void Resume() => _calls.Enqueue("resume");

    public void Flush(ushort? nextSequence) => _calls.Enqueue(FormattableString.Invariant($"flush {nextSequence}"));

    public void FlushBuffered(uint untilSequence) => _calls.Enqueue(FormattableString.Invariant($"flushbuffered {untilSequence}"));

    public void Splice(uint fromSequence, uint untilSequence) => _calls.Enqueue(FormattableString.Invariant($"splice {fromSequence} {untilSequence}"));

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _data.Dispose();
        _control.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>An <see cref="IReceiverAudioOutput"/> that records the calls the control plane makes.</summary>
internal sealed class RecordingOutput : IReceiverAudioOutput
{
    private readonly ConcurrentQueue<string> _calls = new();

    public IReadOnlyList<string> Calls => [.. _calls];

    public int QueuedFrames => 0;

    public ValueTask WriteAsync(ReadOnlyMemory<short> frames, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public Task PauseAsync(CancellationToken cancellationToken) => Record("pause");

    public Task ResumeAsync(CancellationToken cancellationToken) => Record("resume");

    public Task FlushAsync(CancellationToken cancellationToken) => Record("flush");

    /// <summary>When set, <see cref="SetMasterLevelAsync"/> records its call and then waits for this task (a slow speaker).</summary>
    public Task? LevelGate { get; set; }

    public async Task SetMasterLevelAsync(int level, CancellationToken cancellationToken)
    {
        await Record(FormattableString.Invariant($"level {level}"));
        if (LevelGate is { } gate)
        {
            await gate;
        }
    }

    public Task SetNowPlayingAsync(TrackMetadata metadata, ReadOnlyMemory<byte>? artwork, string? artworkContentType, TimeSpan elapsed, CancellationToken cancellationToken) => Record("nowplaying");

    public Task ReleaseAsync(CancellationToken cancellationToken) => Record("release");

    private Task Record(string call)
    {
        _calls.Enqueue(call);
        return Task.CompletedTask;
    }
}
