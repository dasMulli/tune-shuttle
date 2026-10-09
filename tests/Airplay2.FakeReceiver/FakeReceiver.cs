using System.Net;
using System.Threading.Channels;
using Airplay2.Sender.Alac;
using Airplay2.Sender.Plist;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airplay2.FakeReceiver;

/// <summary>Behaviour switches so tests can reproduce the quirks and failure modes listed in SPEC.md.</summary>
public sealed record FakeReceiverOptions
{
    /// <summary>Advertised name.</summary>
    public string Name { get; init; } = "Fake Receiver";

    /// <summary>Accept transient pair-setup (feature bit 48). When false, transient M1 is answered with 470.</summary>
    public bool AllowTransientPairing { get; init; } = true;

    /// <summary>The PIN shown for regular pair-setup.</summary>
    public string Pin { get; init; } = "1234";

    /// <summary>Require <c>X-Apple-HKP</c> on pairing POSTs (open question C-7.11).</summary>
    public bool RequireHkpHeader { get; init; }

    /// <summary>Answer an NTP session SETUP with 501 like shairport-sync (open question C-7.1 / A-Q3).</summary>
    public bool RefuseNtp { get; init; }

    /// <summary>Return a <c>streamID</c> in the stream SETUP response (shairport does not).</summary>
    public bool ReturnStreamId { get; init; } = true;

    /// <summary>Send <c>updateInfo</c> on the event channel right after it connects (shairport) instead of after RECORD (Apple TV).</summary>
    public bool UpdateInfoOnConnect { get; init; } = true;

    /// <summary>Include <c>initialVolume</c> in GET /info.</summary>
    public double? InitialVolume { get; init; } = -20.0;

    /// <summary>Drop every n-th RTP packet to provoke retransmit requests (0 = never).</summary>
    public int DropEveryNthPacket { get; init; }

    /// <summary>Send retransmit requests for gaps.</summary>
    public bool RequestRetransmits { get; init; } = true;

    /// <summary>Close the event channel when a request is not answered within this time.</summary>
    public TimeSpan EventAnswerTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The /info plist to serve; defaults to the recorded SpeakerA response with the name replaced.</summary>
    public PlistDictionary? InfoPlist { get; init; }

    /// <summary>Logger.</summary>
    public ILogger Logger { get; init; } = NullLogger.Instance;
}

/// <summary>What the fake receiver observed, for assertions.</summary>
public abstract record FakeReceiverEvent
{
    public sealed record Request(string Method, string Uri, bool Encrypted, IReadOnlyDictionary<string, string> Headers, byte[] Body) : FakeReceiverEvent;

    public sealed record Paired(string Kind, byte[] SharedSecret) : FakeReceiverEvent;

    public sealed record SessionSetup(PlistDictionary Body) : FakeReceiverEvent;

    public sealed record StreamSetup(PlistDictionary Body) : FakeReceiverEvent;

    public sealed record Volume(double Db) : FakeReceiverEvent;

    public sealed record Flush(ushort Sequence, uint Timestamp) : FakeReceiverEvent;

    public sealed record Teardown(bool Session) : FakeReceiverEvent;

    public sealed record AudioPacket(ushort Sequence, uint Timestamp, bool Marker, short[] Pcm) : FakeReceiverEvent;

    public sealed record Sync(bool First, uint NowFrame, ulong NtpTime, uint NextFrame) : FakeReceiverEvent;

    public sealed record TimingReply(ulong Origin, ulong Receive, ulong Transmit) : FakeReceiverEvent;

    public sealed record RetransmitAnswered(ushort Sequence, bool Wrapped) : FakeReceiverEvent;
}

/// <summary>
/// An in-process AirPlay 2 receiver on loopback: the receiver side of transient and regular HomeKit pairing (a thin
/// adapter over <c>Airplay2.Sender.Pairing.HapAccessoryPairing</c>, the internal accessory-side implementation owned by the
/// pairing package; the fake never re-implements SRP/Ed25519/X25519), the HAP-encrypted control channel, the event channel (sends <c>updateInfo</c> and
/// <c>sendMediaRemoteCommand</c>), SETUP/RECORD/SETPEERS/SET_PARAMETER/GET_PARAMETER/FLUSH/TEARDOWN/feedback, and
/// UDP control/timing/data ports that decrypt and ALAC-decode the realtime stream. It records everything it sees in
/// <see cref="Events"/> so tests can assert on the wire sequence. Not an mDNS responder: tests construct the
/// <c>DiscoveredReceiver</c> directly from <see cref="Describe"/>.
/// </summary>
public sealed class FakeReceiver : IAsyncDisposable
{
    private readonly Channel<FakeReceiverEvent> _events = Channel.CreateUnbounded<FakeReceiverEvent>();

    /// <summary>Creates a receiver with the given options.</summary>
    public FakeReceiver(FakeReceiverOptions options)
    {
        Options = options;
    }

    /// <summary>The options.</summary>
    public FakeReceiverOptions Options { get; }

    /// <summary>Everything observed, in order.</summary>
    public ChannelReader<FakeReceiverEvent> Events => _events.Reader;

    /// <summary>The TCP endpoint of the control port once started.</summary>
    public IPEndPoint ControlEndpoint { get; private set; } = new(IPAddress.Loopback, 0);

    /// <summary>The receiver's long-term Ed25519 public key (<c>pk</c>).</summary>
    public byte[] PublicKey { get; private set; } = [];

    /// <summary>The ALAC decoder used for the data port.</summary>
    public AlacDecoder Decoder { get; } = new(AlacMagicCookie.AirPlay);

    /// <summary>Binds the control TCP port and the UDP ports on loopback.</summary>
    public Task StartAsync(CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: fake-receiver)");

    /// <summary>The <c>_airplay._tcp</c> and <c>_raop._tcp</c> TXT strings this receiver would advertise, and its address/port.</summary>
    public (IReadOnlyList<string> AirplayTxt, IReadOnlyList<string> RaopTxt, IPEndPoint Endpoint) Describe() => throw new NotImplementedException("TODO(impl: fake-receiver)");

    /// <summary>Sends <c>{type: sendMediaRemoteCommand, value: command}</c> on the event channel (simulates the device's buttons).</summary>
    public Task SendRemoteCommandAsync(string command, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: fake-receiver)");

    /// <summary>Changes the receiver's own volume and sends <c>updateInfo</c> with the new <c>initialVolume</c> (one hypothesis for C-7.4).</summary>
    public Task ChangeVolumeAsync(double db, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: fake-receiver)");

    /// <summary>Closes the control connection as if another sender took over.</summary>
    public Task KickAsync(CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: fake-receiver)");

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
