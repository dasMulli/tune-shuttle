using System.Net;
using System.Threading.Channels;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtsp;
using Microsoft.Extensions.Logging;

namespace Airplay2.Sender.Session;

/// <summary>Timing protocol named in the session SETUP (SPEC-control.md 4.2).</summary>
public enum TimingProtocol
{
    /// <summary>RAOP-style timing: the receiver sends 0xD2 requests to our timing port; we send 0xD4 syncs.</summary>
    Ntp,

    /// <summary>IEEE 1588 with the sender as grandmaster; 0xD7 syncs or SETRATEANCHORTIME.</summary>
    Ptp,
}

/// <summary>Stream types (SPEC-control.md 4.4).</summary>
public enum StreamType
{
    /// <summary>Realtime UDP stream (type 96).</summary>
    Realtime = 96,

    /// <summary>Buffered TCP stream (type 103).</summary>
    Buffered = 103,
}

/// <summary>Who we say we are in the session SETUP (SPEC-control.md 4.2).</summary>
public sealed record SenderIdentity(string Name, string DeviceId, string MacAddress, string Model = "iPhone14,3", string OsName = "iPhone OS", string OsVersion = "16.5", string OsBuildVersion = "20F66", string SourceVersion = "690.7.1");

/// <summary>The session-level SETUP request. <c>PtpClockId</c> is <see cref="Streams.IPtpMaster.ClockId"/>; it is written to the plist as a signed int64 (SPEC-audio.md 4.4).</summary>
public sealed record SessionSetupRequest(
    SenderIdentity Sender,
    TimingProtocol TimingProtocol,
    int TimingPort,
    string SessionUuid,
    string? GroupUuid,
    bool GroupContainsGroupLeader,
    bool IsMultiSelectAirPlay,
    IReadOnlyList<IPAddress>? TimingPeerAddresses,
    ulong? PtpClockId);

/// <summary>The session-level SETUP response.</summary>
public sealed record SessionSetupResult(int EventPort, int TimingPort, IReadOnlyList<IPAddress> TimingPeerAddresses, PlistDictionary Raw);

/// <summary>The stream-level SETUP request for type 96 (SPEC-control.md 4.4, SPEC-audio.md 1.1).</summary>
public sealed record StreamSetupRequest(
    StreamType Type,
    byte[] StreamKey,
    int ControlPort,
    int LatencyMin = 11025,
    int LatencyMax = 88200,
    int SamplesPerFrame = 352,
    int SampleRate = 44100,
    ulong StreamConnectionId = 0)
{
    /// <summary><c>ct</c> value for ALAC.</summary>
    public const int CompressionTypeAlac = 2;

    /// <summary><c>audioFormat</c> bit 18: ALAC 44100/16/2.</summary>
    public const uint AudioFormatAlac44100S16Stereo = 0x40000;
}

/// <summary>The stream-level SETUP response.</summary>
public sealed record StreamSetupResult(StreamType Type, int DataPort, int ControlPort, uint? StreamId, long? AudioBufferSize, PlistDictionary Raw);

/// <summary>Text metadata for SET_PARAMETER DMAP (SPEC-control.md 5.2).</summary>
public sealed record TrackMetadata(string? Title, string? Artist, string? Album, TimeSpan? Duration, int? TrackNumber = null, int? DiscNumber = null, string? Genre = null);

/// <summary>Progress triple in RTP frames (SPEC-control.md 5.4).</summary>
public sealed record ProgressInfo(uint Start, uint Current, uint End);

/// <summary>Messages the receiver sends on the event channel (SPEC-control.md 4.3, 6.5).</summary>
public abstract record ReceiverEventMessage
{
    /// <summary><c>{type: updateInfo, value: {...}}</c>: the /info dictionary; may carry <c>initialVolume</c>.</summary>
    public sealed record UpdateInfo(PlistDictionary Value) : ReceiverEventMessage;

    /// <summary><c>{type: sendMediaRemoteCommand, value: play|paus|nitm|pitm}</c>: the receiver's own buttons.</summary>
    public sealed record MediaRemoteCommand(string Command) : ReceiverEventMessage;

    /// <summary>Anything else, kept verbatim for open question C-7.4 (device-side volume changes).</summary>
    public sealed record Unknown(string? Type, PlistDictionary? Body, RtspIncomingRequest Request) : ReceiverEventMessage;

    /// <summary>The receiver closed the event channel (its view of the session ended).</summary>
    public sealed record Closed(string Reason) : ReceiverEventMessage;
}

/// <summary>The reverse TCP connection to the receiver's <c>eventPort</c>, HAP-encrypted with the swapped Events keys.</summary>
public interface IEventChannel : IAsyncDisposable
{
    /// <summary>Parsed messages; every incoming request is answered with 200 immediately by the channel itself.</summary>
    ChannelReader<ReceiverEventMessage> Messages { get; }
}

/// <summary>Per-receiver behaviour switches learned from live tests (SPEC.md decisions and open questions).</summary>
public sealed record ReceiverQuirks
{
    /// <summary>Answer retransmit requests with the raw packet on the data port instead of the wrapped 0xD6 (A-Q6).</summary>
    public bool RetransmitOnDataPort { get; init; }

    /// <summary>Re-send volume after the stream SETUP (Sonos quirk; our default order already does this).</summary>
    public bool VolumeAfterStreamSetup { get; init; } = true;

    /// <summary>Send <c>POST /auth-setup</c> (SPEC-control.md 3.10, the 33-byte body) right after pairing and before the session SETUP. AirPort Express AirPlay 1 heritage; not needed with HAP; off by default (SPEC.md D17).</summary>
    public bool SendAuthSetup { get; init; }

    /// <summary>Per-receiver output delay compensation in frames (SPEC-audio.md 3.3).</summary>
    public int OffsetFrames { get; init; }

    /// <summary>Defaults.</summary>
    public static ReceiverQuirks Default { get; } = new();
}

/// <summary>
/// The control-plane state machine for one receiver (SPEC-control.md 4-6): GET /info, pairing, session SETUP, event
/// channel, RECORD, SETPEERS, stream SETUP, SET_PARAMETER, FLUSH, TEARDOWN, feedback. Owns one
/// <see cref="IRtspConnection"/> and at most one <see cref="IEventChannel"/>.
/// </summary>
public interface IReceiverSession : IAsyncDisposable
{
    /// <summary>The receiver.</summary>
    DiscoveredReceiver Receiver { get; }

    /// <summary>The underlying connection.</summary>
    IRtspConnection Connection { get; }

    /// <summary>The pairing result of this connection, null before pairing.</summary>
    PairingSession? Pairing { get; }

    /// <summary><c>GET /info</c> (plain or inside the encrypted channel). <paramref name="withQualifier"/> sends the stage-1 body.</summary>
    Task<PlistDictionary> GetInfoAsync(bool withQualifier, CancellationToken cancellationToken);

    /// <summary>Runs the pairing the receiver requires: pair-verify with stored credentials, else transient, else the PIN flow via <paramref name="pinPrompt"/>.</summary>
    Task<PairingSession> PairAsync(IPairingStore store, Func<CancellationToken, Task<string>>? pinPrompt, CancellationToken cancellationToken);

    /// <summary><c>POST /auth-setup</c> (3.10): body <c>0x01 || 32-byte X25519 public key</c>, response ignored. Only sent when <see cref="ReceiverQuirks.SendAuthSetup"/> is on; callable directly for probes.</summary>
    Task AuthSetupAsync(CancellationToken cancellationToken);

    /// <summary>Session SETUP (4.2); returns <c>eventPort</c> and PTP peer info. Runs <see cref="AuthSetupAsync"/> first when the quirk is set.</summary>
    Task<SessionSetupResult> SetupSessionAsync(SessionSetupRequest request, CancellationToken cancellationToken);

    /// <summary>Connects the event channel (4.3), retrying a refused connect up to 5 times 1 s apart.</summary>
    Task<IEventChannel> ConnectEventChannelAsync(CancellationToken cancellationToken);

    /// <summary><c>RECORD</c> (4.5); returns the <c>Audio-Latency</c> header value in frames (0 on AirPlay 2 receivers).</summary>
    Task<int> RecordAsync(CancellationToken cancellationToken);

    /// <summary><c>SETPEERS</c> (4.6), PTP sessions only.</summary>
    Task SetPeersAsync(IReadOnlyList<IPAddress> peers, CancellationToken cancellationToken);

    /// <summary>Stream SETUP (4.4); returns the receiver's data and control ports.</summary>
    Task<StreamSetupResult> SetupStreamAsync(StreamSetupRequest request, CancellationToken cancellationToken);

    /// <summary><c>SET_PARAMETER volume</c> in dB: 0 = full, -30 = quietest, -144 = mute (5.1).</summary>
    Task SetVolumeAsync(double volumeDb, CancellationToken cancellationToken);

    /// <summary><c>GET_PARAMETER volume</c> (4.11): the receiver's current level in dB. The body may start with a blank line (shairport-sync answers <c>\r\nvolume: %.6f\r\n</c>); parse with <see cref="Playback.VolumeMapping.ParseParameter"/>, which skips leading CRLF and finds the <c>volume:</c> line anywhere.</summary>
    Task<double> GetVolumeAsync(CancellationToken cancellationToken);

    /// <summary><c>SET_PARAMETER</c> DMAP text (5.2) with <c>RTP-Info: rtptime=&lt;startFrame&gt;</c>.</summary>
    Task SetMetadataAsync(TrackMetadata metadata, uint startFrame, CancellationToken cancellationToken);

    /// <summary><c>SET_PARAMETER</c> artwork (5.3); <paramref name="contentType"/> is image/jpeg or image/png.</summary>
    Task SetArtworkAsync(ReadOnlyMemory<byte> image, string contentType, uint startFrame, CancellationToken cancellationToken);

    /// <summary><c>SET_PARAMETER progress</c> (5.4).</summary>
    Task SetProgressAsync(ProgressInfo progress, CancellationToken cancellationToken);

    /// <summary><c>FLUSH</c> with <c>RTP-Info: seq=;rtptime=</c> (4.8): the receiver discards everything before the given packet.</summary>
    Task FlushAsync(ushort nextSequence, uint nextTimestamp, CancellationToken cancellationToken);

    /// <summary><c>SETRATEANCHORTIME</c> (4.7), buffered/PTP only. <paramref name="timelineId"/> is the PTP clock id (<see cref="Streams.IPtpMaster.ClockId"/>), written as a signed int64 in the plist.</summary>
    Task SetRateAnchorTimeAsync(int rate, uint rtpTime, ulong networkTimeSecs, ulong networkTimeFrac, ulong timelineId, CancellationToken cancellationToken);

    /// <summary><c>FLUSHBUFFERED</c> (4.8), buffered only.</summary>
    Task FlushBufferedAsync(uint flushUntilSeq, uint flushUntilTimestamp, uint? flushFromSeq, uint? flushFromTimestamp, CancellationToken cancellationToken);

    /// <summary><c>POST /feedback</c> (4.10); returns the body (may be empty). Call every 2 s while a session is up.</summary>
    Task<PlistDictionary?> FeedbackAsync(CancellationToken cancellationToken);

    /// <summary>Per-stream TEARDOWN when a <c>streamID</c> is known, then the session TEARDOWN <c>{}</c>, then closes the event channel (4.9, 6.4).</summary>
    Task TeardownAsync(CancellationToken cancellationToken);
}

/// <summary>Implementation of <see cref="IReceiverSession"/>.</summary>
public sealed class ReceiverSession : IReceiverSession
{
    /// <summary>Creates a session over an existing connection.</summary>
    public ReceiverSession(DiscoveredReceiver receiver, IRtspConnection connection, IPairingClient pairingClient, ReceiverQuirks quirks, ILogger<ReceiverSession> logger)
    {
        Receiver = receiver;
        Connection = connection;
        PairingClient = pairingClient;
        Quirks = quirks;
        Logger = logger;
    }

    /// <inheritdoc />
    public DiscoveredReceiver Receiver { get; }

    /// <inheritdoc />
    public IRtspConnection Connection { get; }

    /// <inheritdoc />
    public PairingSession? Pairing { get; private set; }

    /// <summary>The quirks in effect.</summary>
    public ReceiverQuirks Quirks { get; }

    private IPairingClient PairingClient { get; }

    private ILogger Logger { get; }

    /// <inheritdoc />
    public Task<PlistDictionary> GetInfoAsync(bool withQualifier, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: session-streams)");

    /// <inheritdoc />
    public Task<PairingSession> PairAsync(IPairingStore store, Func<CancellationToken, Task<string>>? pinPrompt, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: session-streams)");

    /// <inheritdoc />
    public Task AuthSetupAsync(CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: session-streams)");

    /// <inheritdoc />
    public Task<SessionSetupResult> SetupSessionAsync(SessionSetupRequest request, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: session-streams)");

    /// <inheritdoc />
    public Task<IEventChannel> ConnectEventChannelAsync(CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: session-streams)");

    /// <inheritdoc />
    public Task<int> RecordAsync(CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: session-streams)");

    /// <inheritdoc />
    public Task SetPeersAsync(IReadOnlyList<IPAddress> peers, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: session-streams)");

    /// <inheritdoc />
    public Task<StreamSetupResult> SetupStreamAsync(StreamSetupRequest request, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: session-streams)");

    /// <inheritdoc />
    public Task SetVolumeAsync(double volumeDb, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: session-streams)");

    /// <inheritdoc />
    public Task<double> GetVolumeAsync(CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: session-streams)");

    /// <inheritdoc />
    public Task SetMetadataAsync(TrackMetadata metadata, uint startFrame, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: session-streams)");

    /// <inheritdoc />
    public Task SetArtworkAsync(ReadOnlyMemory<byte> image, string contentType, uint startFrame, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: session-streams)");

    /// <inheritdoc />
    public Task SetProgressAsync(ProgressInfo progress, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: session-streams)");

    /// <inheritdoc />
    public Task FlushAsync(ushort nextSequence, uint nextTimestamp, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: session-streams)");

    /// <inheritdoc />
    public Task SetRateAnchorTimeAsync(int rate, uint rtpTime, ulong networkTimeSecs, ulong networkTimeFrac, ulong timelineId, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: session-streams)");

    /// <inheritdoc />
    public Task FlushBufferedAsync(uint flushUntilSeq, uint flushUntilTimestamp, uint? flushFromSeq, uint? flushFromTimestamp, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: session-streams)");

    /// <inheritdoc />
    public Task<PlistDictionary?> FeedbackAsync(CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: session-streams)");

    /// <inheritdoc />
    public Task TeardownAsync(CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: session-streams)");

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}

/// <summary>DMAP (<c>application/x-dmap-tagged</c>) encoder for the text metadata item (SPEC-control.md 5.2).</summary>
public static class DmapEncoder
{
    /// <summary>Encodes <c>mlit { minm, asar, asal, astm?, astn?, asdn?, asgn? }</c>.</summary>
    public static byte[] EncodeListingItem(TrackMetadata metadata) => throw new NotImplementedException("TODO(impl: session-streams)");
}
