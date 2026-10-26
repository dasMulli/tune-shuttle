using System.Net;
using Airplay2.Receiver.Codecs;
using Airplay2.Receiver.Pairing;
using Airplay2.Receiver.Playout;
using Airplay2.Receiver.Rtsp;
using Airplay2.Receiver.Timing;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Streams;
using Librespot.Discovery.Mdns;

namespace Airplay2.Receiver;

/// <summary>
/// Options of an <see cref="AirplayReceiver"/> (SPEC-receiver.md section 14): name, identity, password, ports, formats
/// and timing. The bridge builds them from its settings and <c>Bridge:Airplay</c> (SPEC-receiver.md 8.3, 8.5); tests use
/// loopback and port 0 (R13).
/// </summary>
public sealed record AirplayReceiverOptions
{
    /// <summary>The advertised name: the <c>_airplay._tcp</c> instance name and <c>/info</c> <c>name</c> (SPEC-receiver.md 3.1, 3.2, R11).</summary>
    public string Name { get; init; } = "TuneShuttle";

    /// <summary>The receiver identity: device id, <c>psi</c> and the Ed25519 seed (SPEC-receiver.md R11).</summary>
    public required ReceiverIdentity Identity { get; init; }

    /// <summary>
    /// The password senders must enter, or null for the open mode (SPEC-receiver.md R7): null means transient pair-setup
    /// with a random PIN per start and status flags <c>0x4</c>; a password means regular pair-setup with it as the SRP PIN
    /// and status flags <c>0x84</c>.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>The TXT <c>model</c> / <c>am</c> value (SPEC-receiver.md R15).</summary>
    public string Model { get; init; } = Discovery.ReceiverFeatures.Model;

    /// <summary>The TXT <c>manufacturer</c> value (SPEC-receiver.md R15).</summary>
    public string Manufacturer { get; init; } = Discovery.ReceiverFeatures.Manufacturer;

    /// <summary>Local address of every listener (RTSP, event channel, stream sockets); null binds every interface (SPEC-receiver.md 8.5 <c>Bridge:LocalAddress</c>).</summary>
    public IPAddress? BindAddress { get; init; }

    /// <summary>The RTSP listener port: 7000 in the product, 0 in tests (SPEC-receiver.md R13, 4.1).</summary>
    public int RtspPort { get; init; } = 7000;

    /// <summary>
    /// The AAC decoder factory (SPEC-receiver.md R4): null builds a <see cref="FfmpegAacDecoderFactory"/> for
    /// <see cref="FfmpegPath"/>. When <c>TryCreate</c> fails the receiver advertises the ALAC-only variant (R3) and logs the reason once.
    /// </summary>
    public IAacDecoderFactory? AacDecoderFactory { get; init; }

    /// <summary>Directory of the libavcodec libraries, or null for the default search path (SPEC-receiver.md 7.2, 8.5 <c>Bridge:Airplay:FfmpegPath</c>).</summary>
    public string? FfmpegPath { get; init; }

    /// <summary>
    /// The shared PTP port the session's <see cref="PtpSlave"/> registers with (SPEC-receiver.md R5, 6.1), or null to run in
    /// anchor-only timing (R6) without a slave.
    /// </summary>
    public IPtpPort? PtpPort { get; init; }

    /// <summary>Options of the per-session PTP slave (SPEC-receiver.md 6.2).</summary>
    public PtpSlaveOptions PtpSlave { get; init; } = new();

    /// <summary>
    /// The process's one clock (SPEC-receiver.md 5.3, 6.2): the bridge passes <c>AirplaySender.Clock</c>, so the playout
    /// buffer and the group player's pacer are on the same timeline; null creates a private <see cref="MasterClock"/>.
    /// </summary>
    public IMasterClock? Clock { get; init; }

    /// <summary>Playout timing: <c>Lpipe</c>, <c>AnchorLead</c>, <c>LateThreshold</c>, <c>DriftThreshold</c> (SPEC-receiver.md R6, 5.3-5.4).</summary>
    public PlayoutOptions Playout { get; init; } = new();

    /// <summary>A sender that sends nothing on the control connection for this long after a stream SETUP is gone (SPEC-receiver.md R16; <c>Bridge:Airplay:SessionTimeoutSeconds</c>).</summary>
    public TimeSpan SessionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Control-connection options (<c>Server</c> header, frame size; SPEC-receiver.md 4.1).</summary>
    public RtspServerConnectionOptions Connection { get; init; } = new();

    /// <summary>Pairing answer switches (SPEC-receiver.md 4.2); the defaults are the product behaviour.</summary>
    public ReceiverPairingOptions Pairing { get; init; } = new();

    /// <summary>Where registered controllers are persisted (SPEC-receiver.md 4.2, 8.4); null uses an <see cref="InMemoryControllerStore"/>.</summary>
    public IControllerStore? ControllerStore { get; init; }

    /// <summary>
    /// The current master level G of the selected output, or null while no output is selected (SPEC-receiver.md R9: the bridge
    /// reads <c>OutputManager.CurrentLevelAsync</c>); answered by <c>GET_PARAMETER volume</c> and <c>/info</c> <c>initialVolume</c>.
    /// Null means no output level is known: the sender's last level in the session is answered, else 50.
    /// </summary>
    public Func<CancellationToken, ValueTask<int?>>? OutputLevel { get; init; }

    /// <summary>Register the two DNS-SD services on start (SPEC-receiver.md 3.1); false runs the listener without advertising (tests that connect directly).</summary>
    public bool Advertise { get; init; } = true;

    /// <summary>The mDNS groups, port and hop limit of the advertiser (SPEC-receiver.md 3.1, 11.1); null for the standard mDNS endpoint. Tests pass a private one.</summary>
    public MdnsEndpoint? Mdns { get; init; }

    /// <summary>The SRV target host name of the advertisement (single label); null for the machine name (SPEC-receiver.md 3.1).</summary>
    public string? MdnsHostname { get; init; }

    /// <summary>The addresses the advertisement publishes, or null for every interface's (SPEC-receiver.md 3.1, 8.5 <c>Bridge:LocalAddress</c>).</summary>
    public IReadOnlyList<IPAddress>? AdvertisedAddresses { get; init; }

    /// <summary>How often <see cref="ReceiverEvent.Statistics"/> is raised while a stream is set up (SPEC-receiver.md 5.3, 9.1: every 5 s).</summary>
    public TimeSpan StatisticsInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long DMAP text, artwork and progress with one <c>rtptime</c> are collected before the now-playing call is made with
    /// what arrived (SPEC-receiver.md 5.5: 500 ms after the first).
    /// </summary>
    public TimeSpan NowPlayingWindow { get; init; } = TimeSpan.FromMilliseconds(500);
}
