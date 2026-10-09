using System.Collections.Concurrent;
using System.Net;
using Airplay2.FakeReceiver;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Session;
using Microsoft.Extensions.Logging.Abstractions;
using TestReceiver = Airplay2.FakeReceiver.FakeReceiver;

namespace Airplay2.Sender.Tests.Session;

/// <summary>Builds sessions against the fake receiver.</summary>
internal static class SessionTestSupport
{
    public static readonly SenderIdentity Sender = new("session tests", "AA:BB:CC:DD:EE:01", "AA:BB:CC:DD:EE:02");

    public static async Task<TestReceiver> StartAsync(FakeReceiverOptions? options = null)
    {
        var receiver = new TestReceiver(options ?? new FakeReceiverOptions());
        await receiver.StartAsync(CancellationToken.None);
        return receiver;
    }

    /// <summary>The receiver as discovery would report it (TXT records from <see cref="TestReceiver.Describe"/>).</summary>
    public static DiscoveredReceiver Describe(TestReceiver receiver)
    {
        var (airplay, raop, endpoint) = receiver.Describe();
        return new DiscoveredReceiver(receiver.DeviceId, receiver.Options.Name, [endpoint.Address], endpoint.Port, AirplayTxtRecord.Parse(airplay), RaopTxtRecord.Parse(raop), DateTimeOffset.UtcNow);
    }

    public static async Task<ReceiverSession> ConnectAsync(TestReceiver receiver, ReceiverQuirks? quirks = null, ReceiverSessionOptions? options = null)
    {
        var connection = await RtspConnection.ConnectAsync(receiver.ControlEndpoint, new RtspConnectionOptions { RequestTimeout = TimeSpan.FromSeconds(5) }, NullLogger.Instance, CancellationToken.None);
        return new ReceiverSession(
            Describe(receiver),
            connection,
            new HapPairingClient(NullLogger<HapPairingClient>.Instance),
            quirks ?? ReceiverQuirks.Default,
            NullLogger<ReceiverSession>.Instance,
            options);
    }

    /// <summary>Connects, runs GET /info and the pairing (transient on the default fake).</summary>
    public static async Task<ReceiverSession> ConnectAndPairAsync(TestReceiver receiver, IPairingStore? store = null, ReceiverQuirks? quirks = null, ReceiverSessionOptions? options = null)
    {
        var session = await ConnectAsync(receiver, quirks, options);
        await session.GetInfoAsync(withQualifier: false, CancellationToken.None);
        await session.PairAsync(store ?? new InMemoryPairingStore(), pinPrompt: null, CancellationToken.None);
        return session;
    }

    public static SessionSetupRequest NtpSession(int timingPort = 6002, string? group = null) =>
        new(Sender, TimingProtocol.Ntp, timingPort, "11111111-2222-3333-4444-555555555555", group, GroupContainsGroupLeader: false, IsMultiSelectAirPlay: true, TimingPeerAddresses: null, PtpClockId: null);

    public static StreamSetupRequest Stream(ReceiverSession session, int controlPort = 6001) =>
        new(StreamType.Realtime, session.Pairing!.DefaultStreamKey, controlPort);

    /// <summary>All control-connection requests the fake recorded until now (waits <paramref name="quiet"/> for stragglers).</summary>
    public static async Task<List<FakeReceiverEvent.Request>> RequestsAsync(TestReceiver receiver, TimeSpan? quiet = null)
    {
        var events = await Fake.FakeReceiverHarness.DrainAsync(receiver, quiet ?? TimeSpan.FromMilliseconds(300));
        return events.OfType<FakeReceiverEvent.Request>().ToList();
    }

    public static string Line(FakeReceiverEvent.Request request) => request.Uri.StartsWith("rtsp://", StringComparison.Ordinal) ? request.Method : $"{request.Method} {request.Uri}";
}

/// <summary>An <see cref="IPairingStore"/> in memory.</summary>
internal sealed class InMemoryPairingStore : IPairingStore
{
    private readonly ConcurrentDictionary<string, PairingCredentials> _pairings = new();
    private readonly ControllerIdentity _controller = ControllerIdentity.Generate();

    public int Deletes { get; private set; }

    public IReadOnlyDictionary<string, PairingCredentials> Pairings => _pairings;

    public Task<ControllerIdentity> GetControllerIdentityAsync(CancellationToken cancellationToken) => Task.FromResult(_controller);

    public Task<PairingCredentials?> GetAsync(string receiverId, CancellationToken cancellationToken) =>
        Task.FromResult(_pairings.TryGetValue(receiverId, out var p) ? p : null);

    public Task SaveAsync(string receiverId, PairingCredentials credentials, CancellationToken cancellationToken)
    {
        _pairings[receiverId] = credentials;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string receiverId, CancellationToken cancellationToken)
    {
        Deletes++;
        _pairings.TryRemove(receiverId, out _);
        return Task.CompletedTask;
    }
}
