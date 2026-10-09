using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Airplay2.FakeReceiver;
using Airplay2.Sender.Crypto;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtsp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using TestReceiver = Airplay2.FakeReceiver.FakeReceiver;

namespace Airplay2.Sender.Tests.Fake;

/// <summary>Shared plumbing of the fake-receiver self-tests: a sender side built from the library's real client types.</summary>
internal static class FakeReceiverHarness
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    public static async Task<TestReceiver> StartAsync(FakeReceiverOptions? options = null)
    {
        var receiver = new TestReceiver(options ?? new FakeReceiverOptions());
        await receiver.StartAsync(CancellationToken.None);
        return receiver;
    }

    public static Task<RtspConnection> ConnectAsync(TestReceiver receiver) =>
        RtspConnection.ConnectAsync(receiver.ControlEndpoint, new RtspConnectionOptions { RequestTimeout = TimeSpan.FromSeconds(5) }, NullLogger.Instance, CancellationToken.None);

    public static HapPairingClient PairingClient() => new(NullLogger<HapPairingClient>.Instance);

    /// <summary>Waits for the next event of type <typeparamref name="T"/> (that matches), skipping others.</summary>
    public static async Task<T> NextAsync<T>(TestReceiver receiver, Func<T, bool>? match = null, TimeSpan? timeout = null)
        where T : FakeReceiverEvent
    {
        using var cts = new CancellationTokenSource(timeout ?? Patience);
        try
        {
            while (true)
            {
                var observed = await receiver.Events.ReadAsync(cts.Token);
                if (observed is T typed && (match?.Invoke(typed) ?? true))
                {
                    return typed;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"No {typeof(T).Name} event within {(timeout ?? Patience).TotalSeconds} s.");
            throw;
        }
    }

    /// <summary>Reads every event that arrives within <paramref name="quiet"/> of the previous one.</summary>
    public static async Task<List<FakeReceiverEvent>> DrainAsync(TestReceiver receiver, TimeSpan quiet)
    {
        var list = new List<FakeReceiverEvent>();
        while (true)
        {
            using var cts = new CancellationTokenSource(quiet);
            try
            {
                list.Add(await receiver.Events.ReadAsync(cts.Token));
            }
            catch (OperationCanceledException)
            {
                return list;
            }
        }
    }

    public static PlistDictionary NtpSessionBody(int timingPort = 0, string protocol = "NTP") => new()
    {
        ["deviceID"] = "AA:BB:CC:DD:EE:FF",
        ["sessionUUID"] = Guid.NewGuid().ToString().ToUpperInvariant(),
        ["timingPort"] = (long)timingPort,
        ["timingProtocol"] = protocol,
        ["macAddress"] = "AA:BB:CC:DD:EE:FF",
        ["name"] = "fake-receiver tests",
        ["groupContainsGroupLeader"] = false,
    };

    public static PlistDictionary StreamBody(byte[] key, int controlPort) => new()
    {
        ["streams"] = new List<object?>
        {
            new PlistDictionary
            {
                ["type"] = 96L,
                ["ct"] = 2L,
                ["audioFormat"] = 0x40000L,
                ["spf"] = 352L,
                ["sr"] = 44100L,
                ["shk"] = key,
                ["controlPort"] = (long)controlPort,
                ["latencyMin"] = 11025L,
                ["latencyMax"] = 88200L,
                ["audioMode"] = "default",
                ["isMedia"] = true,
                ["supportsDynamicStreamID"] = false,
                ["streamConnectionID"] = 1L,
            },
        },
    };

    /// <summary>The bit-exact ALAC packets from Apple's reference encoder (SPEC-audio.md 5.4) with their PCM.</summary>
    public static IReadOnlyList<(string Name, short[] Pcm, byte[] Alac)> AlacVectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestVectors", "alac-44100-s16-2ch-352.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("packets").EnumerateArray()
            .Select(p => (
                p.GetProperty("name").GetString()!,
                MemoryMarshal.Cast<byte, short>(Convert.FromHexString(p.GetProperty("pcm_le_hex").GetString()!)).ToArray(),
                Convert.FromHexString(p.GetProperty("alac_hex").GetString()!)))
            .ToList();
    }
}

/// <summary>A paired sender connection to a fake receiver.</summary>
internal sealed class SenderSide : IAsyncDisposable
{
    private SenderSide(RtspConnection connection, PairingSession pairing)
    {
        Connection = connection;
        Pairing = pairing;
    }

    public RtspConnection Connection { get; }

    public PairingSession Pairing { get; }

    public string Uri => Connection.SessionUri;

    /// <summary>Connects, runs transient pairing and switches to encryption.</summary>
    public static async Task<SenderSide> ConnectAsync(TestReceiver receiver)
    {
        var connection = await FakeReceiverHarness.ConnectAsync(receiver);
        var pairing = await FakeReceiverHarness.PairingClient().PairTransientAsync(connection, CancellationToken.None);
        connection.EnableEncryption(pairing.ControlKeys);
        return new SenderSide(connection, pairing);
    }

    public Task<RtspResponse> SendAsync(RtspRequest request) => Connection.SendAsync(request, CancellationToken.None);

    public async Task<PlistDictionary> SetupSessionAsync(PlistDictionary? body = null)
    {
        var response = await Connection.SendExpectOkAsync(RtspRequest.WithPlist("SETUP", Uri, body ?? FakeReceiverHarness.NtpSessionBody()), CancellationToken.None);
        return response.PlistBody();
    }

    public async Task<PlistDictionary> SetupStreamAsync(int controlPort, byte[]? key = null)
    {
        var response = await Connection.SendExpectOkAsync(RtspRequest.WithPlist("SETUP", Uri, FakeReceiverHarness.StreamBody(key ?? Pairing.DefaultStreamKey, controlPort)), CancellationToken.None);
        return (PlistDictionary)response.PlistBody().GetArray("streams")![0]!;
    }

    public Task<EventSide> ConnectEventsAsync(PlistDictionary sessionResponse, bool answer = true) =>
        EventSide.ConnectAsync((int)sessionResponse.GetInt64("eventPort")!.Value, Pairing.EventKeys, answer);

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}

/// <summary>
/// The sender's end of the event channel, built from <see cref="RtspServerConnection"/> (it serves the receiver's
/// requests): connects to <c>eventPort</c>, encrypts with the Events keys in the sender's view, answers every request
/// with <c>200</c> + <c>Audio-Latency: 0</c> like pyatv, or not at all.
/// </summary>
internal sealed class EventSide : IAsyncDisposable
{
    private readonly Channel<RtspIncomingRequest> _requests = Channel.CreateUnbounded<RtspIncomingRequest>();

    private EventSide()
    {
    }

    public RtspServerConnection Server { get; private set; } = null!;

    public ChannelReader<RtspIncomingRequest> Requests => _requests.Reader;

    public static async Task<EventSide> ConnectAsync(int port, HapChannelKeys keys, bool answer)
    {
        var side = new EventSide();
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
        // Encrypted before the read loop starts: the fake receiver may send updateInfo the moment it accepts.
        side.Server = RtspServerConnection.Start(
            socket,
            (_, request, _) =>
            {
                side._requests.Writer.TryWrite(request);
                return ValueTask.FromResult(answer
                    ? RtspServerResponse.Ok() with { Headers = new Dictionary<string, string> { [RtspHeaders.AudioLatency] = "0" } }
                    : RtspServerResponse.None());
            },
            keys: keys);
        return side;
    }

    public async Task<PlistDictionary> NextAsync(string? type = null)
    {
        using var cts = new CancellationTokenSource(FakeReceiverHarness.Patience);
        while (true)
        {
            var request = await _requests.Reader.ReadAsync(cts.Token);
            var body = request.PlistBody();
            if (type is null || body.GetString("type") == type)
            {
                return body;
            }
        }
    }

    public ValueTask DisposeAsync() => Server.DisposeAsync();
}
