using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Airplay2.FakeReceiver;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Airplay2.Sender.Tests.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using TestReceiver = Airplay2.FakeReceiver.FakeReceiver;

namespace Airplay2.Sender.Tests.Playback;

/// <summary>A PTP grandmaster stand-in: a fixed clock id and a record of the peers (the fake receiver records 0xD7 syncs without a PTP slave).</summary>
internal sealed class StubPtpMaster(ulong clockId = 0xFFFF_0000_1234_5678UL) : IPtpMaster
{
    public ulong ClockId { get; } = clockId;

    public ConcurrentQueue<string> Calls { get; } = new();

    public int Starts;

    public void AddPeer(IPAddress address) => Calls.Enqueue("add " + address);

    public void RemovePeer(IPAddress address) => Calls.Enqueue("remove " + address);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Starts);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>One loopback "receiver" for transport tests: a data and a control UDP socket.</summary>
internal sealed class LoopbackReceiverSockets : IDisposable
{
    public LoopbackReceiverSockets()
    {
        Data = Bind();
        Control = Bind();
    }

    public Socket Data { get; }

    public Socket Control { get; }

    public IPEndPoint DataEndpoint => (IPEndPoint)Data.LocalEndPoint!;

    public IPEndPoint ControlEndpoint => (IPEndPoint)Control.LocalEndPoint!;

    public byte[] Key { get; } = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    public ReceiverStreamEndpoint Endpoint(int offset = 0, bool raw = false) => new(DataEndpoint, ControlEndpoint, Key, offset, raw);

    public static async Task<byte[]?> ReceiveAsync(Socket socket, TimeSpan? timeout = null)
    {
        var buffer = new byte[2048];
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(2));
        try
        {
            var n = await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token);
            return buffer[..n];
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public static async Task<List<byte[]>> DrainAsync(Socket socket, TimeSpan quiet)
    {
        var list = new List<byte[]>();
        while (await ReceiveAsync(socket, quiet) is { } datagram)
        {
            list.Add(datagram);
        }

        return list;
    }

    public void Dispose()
    {
        Data.Dispose();
        Control.Dispose();
    }

    private static Socket Bind()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ReceiveBufferSize = 4 << 20 };
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }
}

/// <summary>
/// A group player over the real transport, timing responder and control port (bound to loopback) with fake receivers
/// as members; sessions come from <see cref="SessionTestSupport.ConnectAndPairAsync"/> (GET /info + transient pairing).
/// </summary>
internal sealed class GroupHarness : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TestReceiver> _fakes = new();

    private GroupHarness(MasterClock clock, TimingResponder timing, ControlPort control, RealtimeStreamTransport transport, GroupPlayerOptions options, ILoggerFactory? logging)
    {
        Clock = clock;
        Timing = timing;
        Control = control;
        Transport = transport;
        Player = new GroupPlayer(transport, ConnectAsync, options, logging?.CreateLogger<GroupPlayer>() ?? NullLogger<GroupPlayer>.Instance);
    }

    public MasterClock Clock { get; }

    public TimingResponder Timing { get; }

    public ControlPort Control { get; }

    public RealtimeStreamTransport Transport { get; }

    public GroupPlayer Player { get; }

    public List<TestReceiver> Fakes { get; } = [];

    public static async Task<GroupHarness> StartAsync(GroupPlayerOptions? options = null, IPtpMaster? ptp = null, ILoggerFactory? logging = null)
    {
        var clock = new MasterClock();
        var timing = new TimingResponder(clock, bindAddress: IPAddress.Loopback);
        var control = new ControlPort(bindAddress: IPAddress.Loopback);
        var transport = new RealtimeStreamTransport(clock, timing, control, RealtimeStreamConstants.DefaultLatencyFrames, ptp, logging?.CreateLogger<RealtimeStreamTransport>());
        var harness = new GroupHarness(clock, timing, control, transport, options ?? new GroupPlayerOptions(), logging);
        await transport.StartAsync(CancellationToken.None);
        return harness;
    }

    public async Task<TestReceiver> NewFakeAsync(FakeReceiverOptions? options = null)
    {
        var fake = await SessionTestSupport.StartAsync(options ?? new FakeReceiverOptions());
        _fakes[fake.DeviceId] = fake;
        Fakes.Add(fake);
        return fake;
    }

    public Task AddAsync(TestReceiver fake, int level = 50) => Player.AddReceiverAsync(SessionTestSupport.Describe(fake), level, CancellationToken.None);

    /// <summary>Writes PCM on a producer thread (Write blocks by design).</summary>
    public Task WriteAsync(short[] pcm)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Player.Write(pcm);
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        })
        { IsBackground = true };
        thread.Start();
        return done.Task;
    }

    public async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "condition not met in time");
            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Player.DisposeAsync();
        await Control.DisposeAsync();
        await Timing.DisposeAsync();
        foreach (var fake in Fakes)
        {
            await fake.DisposeAsync();
        }
    }

    private async Task<IReceiverSession> ConnectAsync(Airplay2.Sender.Discovery.DiscoveredReceiver receiver, CancellationToken cancellationToken) =>
        await SessionTestSupport.ConnectAndPairAsync(_fakes[receiver.Id]);
}

/// <summary>Distinct, non-silent test PCM: a slow sweep with a per-frame ramp so any misplaced packet shows.</summary>
internal static class TestPcm
{
    public static short[] Sweep(int frames, int seed = 0)
    {
        var pcm = new short[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var t = (i + seed) / 44100.0;
            pcm[2 * i] = (short)(8000 * Math.Sin(2 * Math.PI * (200 + (400 * t)) * t) + (i % 97));
            pcm[(2 * i) + 1] = (short)(6000 * Math.Cos(2 * Math.PI * 330 * t) - (i % 89));
        }

        return pcm;
    }
}
