using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Rtsp;

namespace Airplay2.Sender.Session;

/// <summary>
/// The requests of the session layer (SPEC-control.md 3.10, 4, 5; SPEC-audio.md 1.1), built in one place so the session,
/// the probes and the tests share the exact bytes. Identity headers, <c>CSeq</c>, <c>Content-Type</c> and
/// <c>Content-Length</c> are added by <see cref="RtspMessageCodec"/>.
/// </summary>
internal static class SessionRequests
{
    /// <summary>The MFi-SAP "unencrypted" marker in front of the public key of <c>/auth-setup</c> [pyatv:pyatv/support/rtsp.py:112-123].</summary>
    public const byte AuthSetupUnencrypted = 0x01;

    /// <summary>The <c>/auth-setup</c> endpoint (SPEC-control.md 3.10).</summary>
    public const string AuthSetupUri = "/auth-setup";

    /// <summary>The <c>/feedback</c> endpoint (SPEC-control.md 4.10).</summary>
    public const string FeedbackUri = "/feedback";

    /// <summary>The body of <c>GET_PARAMETER volume</c>: exactly <c>volume\r\n</c>, shairport-sync checks the length (8) [sps:rtsp.c:3318-3325].</summary>
    public static readonly byte[] GetVolumeBody = "volume\r\n"u8.ToArray();

    /// <summary><c>GET /info</c>, bare or with the stage-1 body <c>{qualifier: [txtAirPlay]}</c> (SPEC-control.md 1.8, 4.1).</summary>
    public static RtspRequest Info(bool withQualifier) => withQualifier
        ? RtspRequest.WithPlist("GET", "/info", new PlistDictionary { ["qualifier"] = new List<object?> { "txtAirPlay" } })
        : RtspRequest.Create("GET", "/info");

    /// <summary>
    /// <c>POST /auth-setup</c> (SPEC-control.md 3.10): <c>application/octet-stream</c>, 33 bytes = <c>0x01</c> followed by
    /// a 32-byte X25519 public key. Sent with the D6 protocol token <c>RTSP/1.0</c> (pyatv uses <c>HTTP/1.1</c> here
    /// [pyatv:pyatv/support/rtsp.py:117-123]; the srcvers-366 speaker echoes either token).
    /// </summary>
    public static RtspRequest AuthSetup(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != 32)
        {
            throw new ArgumentException("The /auth-setup public key is 32 bytes.", nameof(publicKey));
        }

        var body = new byte[33];
        body[0] = AuthSetupUnencrypted;
        publicKey.CopyTo(body.AsSpan(1));
        return RtspRequest.WithBody("POST", AuthSetupUri, RtspContentTypes.OctetStream, body);
    }

    /// <summary>
    /// The session SETUP body (SPEC-control.md 4.2): no <c>streams</c> key. Keys in pyatv's order
    /// [pyatv:pyatv/protocols/raop/protocols/airplayv2.py:56-74] plus <c>groupUUID</c> (owntone, SPEC.md D3) and, for PTP,
    /// <c>timingPeerInfo</c>/<c>timingPeerList</c> with <c>ClockID</c> written as a signed int64 (SPEC-audio.md 4.4;
    /// shairport-sync reads the addresses [sps:rtsp.c:2528-2569]).
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="localAddress">Our address on the control connection: the PTP peer address when <see cref="SessionSetupRequest.TimingPeerAddresses"/> is null.</param>
    public static PlistDictionary SessionSetupBody(SessionSetupRequest request, IPAddress localAddress)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Sender);
        ArgumentException.ThrowIfNullOrEmpty(request.SessionUuid);
        var sender = request.Sender;
        var body = new PlistDictionary
        {
            ["deviceID"] = sender.DeviceId,
            ["sessionUUID"] = request.SessionUuid,
        };

        switch (request.TimingProtocol)
        {
            case TimingProtocol.Ntp:
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.TimingPort, 0, nameof(request.TimingPort));
                ArgumentOutOfRangeException.ThrowIfGreaterThan(request.TimingPort, ushort.MaxValue, nameof(request.TimingPort));
                body["timingPort"] = (long)request.TimingPort;
                body["timingProtocol"] = "NTP";
                break;
            case TimingProtocol.Ptp:
                body["timingProtocol"] = "PTP";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.TimingProtocol, "Unknown timing protocol.");
        }

        body["isMultiSelectAirPlay"] = request.IsMultiSelectAirPlay;
        body["groupContainsGroupLeader"] = request.GroupContainsGroupLeader;
        if (request.GroupUuid is { Length: > 0 } group)
        {
            body["groupUUID"] = group;
        }

        body["macAddress"] = sender.MacAddress;
        body["model"] = sender.Model;
        body["name"] = sender.Name;
        body["osBuildVersion"] = sender.OsBuildVersion;
        body["osName"] = sender.OsName;
        body["osVersion"] = sender.OsVersion;
        body["senderSupportsRelay"] = false;
        body["sourceVersion"] = sender.SourceVersion;
        body["statsCollectionEnabled"] = false;

        if (request.TimingProtocol == TimingProtocol.Ptp)
        {
            var clockId = request.PtpClockId ?? throw new ArgumentException("A PTP session SETUP needs PtpClockId.", nameof(request));
            var addresses = new List<object?>();
            foreach (var address in request.TimingPeerAddresses ?? [localAddress])
            {
                addresses.Add(FormatAddress(address));
            }

            // owntone's minimal peer info [owntone:airplay.c:2733-2745, read for understanding]: ID (a UUID string; we use the
            // session UUID), DeviceType 0, ClockID as signed int64, SupportsClockPortMatchingOverride false, Addresses.
            PlistDictionary PeerInfo() => new()
            {
                ["ID"] = request.SessionUuid,
                ["DeviceType"] = 0L,
                ["ClockID"] = unchecked((long)clockId),
                ["SupportsClockPortMatchingOverride"] = false,
                ["Addresses"] = new List<object?>(addresses),
            };

            body["timingPeerInfo"] = PeerInfo();
            body["timingPeerList"] = new List<object?> { PeerInfo() };
        }

        return body;
    }

    /// <summary>
    /// The stream SETUP body (SPEC-audio.md 1.1, SPEC-control.md 4.4): <c>{streams: [{...}]}</c> with
    /// <c>ct</c> and <c>audioFormat</c> from the request (default 2 and 0x40000, ALAC 44100/16/2), <c>spf</c>, <c>sr</c>, <c>shk</c>, <c>controlPort</c>,
    /// <c>latencyMin/Max</c>, <c>audioMode default</c>, <c>isMedia</c>, <c>supportsDynamicStreamID false</c>,
    /// <c>streamConnectionID</c>. Type 103 omits <c>latencyMin/Max</c> (shairport-sync does not read them for the buffered
    /// stream [sps:rtsp.c:2921-2970]).
    /// </summary>
    public static PlistDictionary StreamSetupBody(StreamSetupRequest request, uint rtspSessionId)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.StreamKey is not { Length: 32 })
        {
            // shairport-sync rejects any other length [sps:rtsp.c:2766-2785].
            throw new ArgumentException("shk must be 32 bytes.", nameof(request));
        }

        if (request.Type is not (StreamType.Realtime or StreamType.Buffered))
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Type, "Unknown stream type.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(request.ControlPort, 0, nameof(request.ControlPort));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.ControlPort, ushort.MaxValue, nameof(request.ControlPort));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.SamplesPerFrame, 0, nameof(request.SamplesPerFrame));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.SampleRate, 0, nameof(request.SampleRate));

        var stream = new PlistDictionary
        {
            ["type"] = (long)(int)request.Type,
            ["ct"] = (long)request.CompressionType,
            ["audioFormat"] = (long)request.AudioFormat,
            ["spf"] = (long)request.SamplesPerFrame,
            ["sr"] = (long)request.SampleRate,
            ["shk"] = request.StreamKey.ToArray(),
            ["controlPort"] = (long)request.ControlPort,
        };
        if (request.Type == StreamType.Realtime)
        {
            stream["latencyMin"] = (long)request.LatencyMin;
            stream["latencyMax"] = (long)request.LatencyMax;
        }

        stream["audioMode"] = "default";
        stream["isMedia"] = true;
        stream["supportsDynamicStreamID"] = false;
        stream["streamConnectionID"] = request.StreamConnectionId != 0 ? request.StreamConnectionId : (ulong)rtspSessionId;
        return new PlistDictionary { ["streams"] = new List<object?> { stream } };
    }

    /// <summary><c>RECORD</c> without body (SPEC-control.md 4.5; iOS sends only the standard headers).</summary>
    public static RtspRequest Record(string uri) => RtspRequest.Create("RECORD", uri);

    /// <summary>
    /// <c>SETPEERS</c> (SPEC-control.md 4.6): <c>Content-Type: /peer-list-changed</c>, a bplist array of address strings
    /// (the receiver's own address first, then ours [owntone:airplay.c:2659-2688]); the caller supplies the order.
    /// </summary>
    public static RtspRequest SetPeers(string uri, IReadOnlyList<IPAddress> peers)
    {
        ArgumentNullException.ThrowIfNull(peers);
        var list = new List<object?>(peers.Count);
        foreach (var peer in peers)
        {
            list.Add(FormatAddress(peer));
        }

        return new RtspRequest("SETPEERS", uri, Empty, RtspContentTypes.PeerListChanged, BinaryPlist.Encode(list));
    }

    /// <summary><c>SET_PARAMETER volume: &lt;dB&gt;\r\n</c> (SPEC-control.md 5.1; six decimals, dot separator).</summary>
    public static RtspRequest SetVolume(string uri, double volumeDb)
    {
        if (double.IsNaN(volumeDb) || double.IsInfinity(volumeDb))
        {
            throw new ArgumentOutOfRangeException(nameof(volumeDb), volumeDb, "The volume must be a finite number of dB.");
        }

        if (volumeDb > VolumeMapping.MaxDb || (volumeDb < VolumeMapping.MinDb && volumeDb != VolumeMapping.MuteDb))
        {
            // shairport-sync treats anything outside 0..-30 and -144 as minimum [sps:common.c:1546-1605].
            throw new ArgumentOutOfRangeException(nameof(volumeDb), volumeDb, "The volume is 0..-30 dB or -144 (mute).");
        }

        return RtspRequest.WithBody("SET_PARAMETER", uri, RtspContentTypes.Parameters, Encoding.ASCII.GetBytes(VolumeMapping.FormatParameter(volumeDb)));
    }

    /// <summary><c>GET_PARAMETER</c> with body <c>volume\r\n</c> (SPEC-control.md 4.11).</summary>
    public static RtspRequest GetVolume(string uri) => RtspRequest.WithBody("GET_PARAMETER", uri, RtspContentTypes.Parameters, GetVolumeBody);

    /// <summary><c>SET_PARAMETER</c> DMAP (SPEC-control.md 5.2) with <c>RTP-Info: rtptime=&lt;start&gt;</c>.</summary>
    public static RtspRequest Metadata(string uri, TrackMetadata metadata, uint startFrame) =>
        RtspRequest.WithBody("SET_PARAMETER", uri, RtspContentTypes.DmapTagged, DmapEncoder.EncodeListingItem(metadata), RtpTime(startFrame));

    /// <summary><c>SET_PARAMETER</c> artwork (SPEC-control.md 5.3) with <c>RTP-Info: rtptime=&lt;start&gt;</c>.</summary>
    public static RtspRequest Artwork(string uri, ReadOnlyMemory<byte> image, string contentType, uint startFrame)
    {
        if (!string.Equals(contentType, RtspContentTypes.Jpeg, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(contentType, RtspContentTypes.Png, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Artwork is image/jpeg or image/png.", nameof(contentType));
        }

        if (image.IsEmpty)
        {
            throw new ArgumentException("The artwork is empty.", nameof(image));
        }

        return RtspRequest.WithBody("SET_PARAMETER", uri, contentType.ToLowerInvariant(), image, RtpTime(startFrame));
    }

    /// <summary>
    /// <c>SET_PARAMETER progress: start/current/end\r\n</c> (SPEC-control.md 5.4) with <c>RTP-Info: rtptime=</c> the
    /// track start (owntone adds the header to the progress request as to the other metadata items).
    /// </summary>
    public static RtspRequest Progress(string uri, ProgressInfo progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var text = string.Create(CultureInfo.InvariantCulture, $"progress: {progress.Start}/{progress.Current}/{progress.End}\r\n");
        return RtspRequest.WithBody("SET_PARAMETER", uri, RtspContentTypes.Parameters, Encoding.ASCII.GetBytes(text), RtpTime(progress.RtpTime ?? progress.Start));
    }

    /// <summary><c>FLUSH</c> with <c>RTP-Info: seq=&lt;n&gt;;rtptime=&lt;t&gt;</c>, no body (SPEC-control.md 4.8).</summary>
    public static RtspRequest Flush(string uri, ushort nextSequence, uint nextTimestamp) =>
        new("FLUSH", uri, new Dictionary<string, string>
        {
            [RtspHeaders.RtpInfo] = string.Create(CultureInfo.InvariantCulture, $"seq={nextSequence};rtptime={nextTimestamp}"),
        }, null, ReadOnlyMemory<byte>.Empty);

    /// <summary>
    /// <c>SETRATEANCHORTIME</c> (SPEC-control.md 4.7): bplist <c>{rate, rtpTime, networkTimeSecs, networkTimeFrac,
    /// networkTimeTimelineID}</c>; the timeline id (our PTP clock id) is written as a signed int64 like <c>ClockID</c>.
    /// </summary>
    public static RtspRequest SetRateAnchorTime(string uri, int rate, uint rtpTime, ulong networkTimeSecs, ulong networkTimeFrac, ulong timelineId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rate);
        var body = new PlistDictionary
        {
            ["rate"] = (long)rate,
            ["rtpTime"] = (long)rtpTime,
            ["networkTimeSecs"] = networkTimeSecs,
            ["networkTimeFrac"] = networkTimeFrac,
            ["networkTimeTimelineID"] = unchecked((long)timelineId),
        };
        return RtspRequest.WithPlist("SETRATEANCHORTIME", uri, body);
    }

    /// <summary>
    /// <c>FLUSHBUFFERED</c> (SPEC-control.md 4.8): <c>{flushUntilSeq, flushUntilTS}</c> for an immediate flush, with
    /// <c>flushFromSeq/flushFromTS</c> in front for a deferred one [sps:rtsp.c:1294-1395].
    /// </summary>
    public static RtspRequest FlushBuffered(string uri, uint flushUntilSeq, uint flushUntilTimestamp, uint? flushFromSeq, uint? flushFromTimestamp)
    {
        if (flushFromSeq.HasValue != flushFromTimestamp.HasValue)
        {
            throw new ArgumentException("flushFromSeq and flushFromTimestamp are given together or not at all.", nameof(flushFromSeq));
        }

        var body = new PlistDictionary();
        if (flushFromSeq is { } fromSeq && flushFromTimestamp is { } fromTs)
        {
            body["flushFromSeq"] = (long)fromSeq;
            body["flushFromTS"] = (long)fromTs;
        }

        body["flushUntilSeq"] = (long)flushUntilSeq;
        body["flushUntilTS"] = (long)flushUntilTimestamp;
        return RtspRequest.WithPlist("FLUSHBUFFERED", uri, body);
    }

    /// <summary>
    /// <c>POST /feedback</c> (SPEC-control.md 4.10): no body as iOS sends it; probe P8 tries the bplist bodies <c>{}</c> and
    /// <c>{streams: []}</c> (<c>keepAliveSendStatsAsBody</c>).
    /// </summary>
    public static RtspRequest Feedback(PlistDictionary? body = null) => body is null
        ? RtspRequest.Create("POST", FeedbackUri)
        : RtspRequest.WithPlist("POST", FeedbackUri, body);

    /// <summary>Per-stream <c>TEARDOWN</c> (SPEC-control.md 4.9): <c>{streams: [{streamID, type}]}</c>.</summary>
    public static RtspRequest TeardownStream(string uri, uint streamId, StreamType type) =>
        RtspRequest.WithPlist("TEARDOWN", uri, new PlistDictionary
        {
            ["streams"] = new List<object?> { new PlistDictionary { ["streamID"] = (long)streamId, ["type"] = (long)(int)type } },
        });

    /// <summary>Session <c>TEARDOWN</c> with the empty dictionary <c>{}</c> (SPEC.md D7; never body-less).</summary>
    public static RtspRequest TeardownSession(string uri) => RtspRequest.WithPlist("TEARDOWN", uri, new PlistDictionary());

    /// <summary>An address as the plists carry it: dotted IPv4 or IPv6 without a scope id.</summary>
    public static string FormatAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0
            ? new IPAddress(address.GetAddressBytes()).ToString()
            : address.ToString();
    }

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    private static Dictionary<string, string> RtpTime(uint frame) => new()
    {
        [RtspHeaders.RtpInfo] = string.Create(CultureInfo.InvariantCulture, $"rtptime={frame}"),
    };
}
