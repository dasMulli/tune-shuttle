using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Airplay2.Receiver;
using Airplay2.Receiver.Playout;
using Airplay2.Receiver.Timing;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Session;
using Bridge.Contracts;
using Bridge.Service.Store;

namespace Bridge.Service.Api;

/// <summary>
/// Conversions between the store rows (<see cref="ReceiverRow"/>, <see cref="GroupRow"/>), discovery
/// (<see cref="DiscoveredReceiver"/>) and the API DTOs of <c>Bridge.Contracts</c>. Pure functions without I/O; the
/// runtime state the store does not know (online, connection state, errors, the active output) is passed in.
/// Extend it when a DTO gains a field.
/// </summary>
public static class Mapping
{
    /// <summary>Maps <c>receivers.pairing_state</c> to the DTO enum. Throws <see cref="FormatException"/> for an unknown value.</summary>
    public static PairingState ToPairingState(string value) => value switch
    {
        PairingStates.Transient => PairingState.Transient,
        PairingStates.PinRequired => PairingState.PinRequired,
        PairingStates.Paired => PairingState.Paired,
        PairingStates.Disabled => PairingState.Disabled,
        _ => throw new FormatException($"Unknown pairing state '{value}'."),
    };

    /// <summary>Maps the DTO enum to the <c>receivers.pairing_state</c> value.</summary>
    public static string ToStoreValue(this PairingState state) => state switch
    {
        PairingState.Transient => PairingStates.Transient,
        PairingState.PinRequired => PairingStates.PinRequired,
        PairingState.Paired => PairingStates.Paired,
        PairingState.Disabled => PairingStates.Disabled,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown pairing state."),
    };

    /// <summary>
    /// The pairing state the bridge shows for a receiver (SPEC-control.md 1.6 step 2 and 3.8): transient receivers need
    /// nothing stored; PIN and password receivers are <see cref="PairingState.Paired"/> once a pairing is stored and
    /// <see cref="PairingState.PinRequired"/> before.
    /// </summary>
    public static PairingState PairingStateFor(PairingRequirement requirement, bool hasStoredPairing) => requirement switch
    {
        PairingRequirement.Disabled => PairingState.Disabled,
        // A stored pairing wins over transient: it exists only because transient was refused (470, SPEC-control.md 3.9).
        _ when hasStoredPairing => PairingState.Paired,
        PairingRequirement.Transient => PairingState.Transient,
        _ => PairingState.PinRequired,
    };

    /// <summary>The DTO view of the decoded capabilities plus the raw feature and flag words.</summary>
    public static ReceiverCapabilitiesDto ToCapabilitiesDto(ReceiverCapabilities capabilities, AirplayFeatures features, AirplayStatusFlags flags)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return new ReceiverCapabilitiesDto(
            AirPlay2: capabilities.IsAirPlay2,
            Transient: capabilities.Pairing == PairingRequirement.Transient,
            Pin: capabilities.Pairing is PairingRequirement.Pin or PairingRequirement.Password,
            Buffered: capabilities.SupportsBufferedStream,
            Ptp: capabilities.HasPtpClock,
            Alac: capabilities.SupportsAlac,
            Text: capabilities.WantsTextMetadata,
            Artwork: capabilities.WantsArtwork,
            Progress: capabilities.WantsProgress,
            Features: AirplayTxtValues.FormatFeatures(features),
            Flags: FormatFlags(flags));
    }

    /// <summary>
    /// Capabilities reconstructed from the stored feature and flag words, for receivers that are not currently
    /// discovered. <c>acl</c>/<c>act</c> and the <c>_raop._tcp</c> record are not stored, so <see cref="PairingRequirement.Disabled"/>
    /// and RAOP-only details are not recovered; the stored <c>pairing_state</c> remains authoritative.
    /// </summary>
    public static ReceiverCapabilities CapabilitiesOf(ReceiverRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var txt = AirplayTxtRecord.Parse([
            "features=" + AirplayTxtValues.FormatFeatures(FeaturesOf(row)),
            "flags=" + FormatFlags(FlagsOf(row)),
        ]);
        return ReceiverCapabilities.From(txt, null);
    }

    /// <summary>The stored feature word.</summary>
    public static AirplayFeatures FeaturesOf(ReceiverRow row) => (AirplayFeatures)unchecked((ulong)row.Features);

    /// <summary>The stored status flags.</summary>
    public static AirplayStatusFlags FlagsOf(ReceiverRow row) => (AirplayStatusFlags)unchecked((uint)row.StatusFlags);

    /// <summary>
    /// Merges a discovery result into the stored row: name, model, address, port, features, flags, <c>pk</c>, pairing state
    /// and last-seen come from discovery; level, offset and quirk overrides are kept from <paramref name="existing"/> (or
    /// default to 50, 0, none). A <c>pk</c> different from the stored one means the receiver was reset (SPEC-control.md
    /// 3.8); the stored pairing is then expected to fail pair-verify and be dropped by the session.
    /// </summary>
    public static ReceiverRow ToReceiverRow(this DiscoveredReceiver discovered, ReceiverRow? existing, bool hasStoredPairing)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        var airplay = discovered.Airplay;
        var address = PreferredAddress(discovered);
        return new ReceiverRow(
            Id: discovered.Id,
            Name: discovered.Name,
            Model: airplay.Model ?? discovered.Raop?.Model ?? existing?.Model,
            Manufacturer: airplay.Manufacturer ?? existing?.Manufacturer,
            LastAddress: address?.ToString() ?? existing?.LastAddress,
            LastPort: discovered.Port,
            Features: unchecked((long)(ulong)airplay.Features),
            StatusFlags: (uint)airplay.Flags,
            PublicKey: airplay.PublicKey ?? existing?.PublicKey,
            PairingState: PairingStateFor(discovered.Capabilities.Pairing, hasStoredPairing).ToStoreValue(),
            Level: existing?.Level ?? SqliteBridgeStore.DefaultLevel,
            OffsetFrames: existing?.OffsetFrames ?? 0,
            Quirks: existing?.Quirks,
            LastSeen: FormatTimestamp(discovered.LastSeen));
    }

    /// <summary>
    /// The API view of a receiver. Address, port, capabilities and last-seen come from <paramref name="discovered"/> when
    /// the receiver is currently on the network (it is then <c>Online</c>), otherwise from the stored row.
    /// </summary>
    public static ReceiverDto ToDto(this ReceiverRow row, DiscoveredReceiver? discovered, ReceiverConnectionState state, string? groupId = null, string? error = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ReceiverCapabilitiesDto capabilities;
        string? address;
        int port;
        if (discovered is not null)
        {
            capabilities = ToCapabilitiesDto(discovered.Capabilities, discovered.Airplay.Features, discovered.Airplay.Flags);
            address = PreferredAddress(discovered)?.ToString() ?? row.LastAddress;
            port = discovered.Port;
        }
        else
        {
            capabilities = ToCapabilitiesDto(CapabilitiesOf(row), FeaturesOf(row), FlagsOf(row));
            address = row.LastAddress;
            port = row.LastPort;
        }

        return new ReceiverDto(
            Id: row.Id,
            Name: discovered?.Name ?? row.Name,
            Model: row.Model,
            Manufacturer: row.Manufacturer,
            Address: address,
            Port: port,
            Online: discovered is not null,
            Pairing: ToPairingState(row.PairingState),
            State: state,
            Level: row.Level,
            OffsetFrames: row.OffsetFrames,
            GroupId: groupId,
            Error: error,
            Capabilities: capabilities,
            LastSeen: discovered is not null ? discovered.LastSeen : ParseTimestamp(row.LastSeen));
    }

    /// <summary>
    /// The store row behind a DTO (the reverse of <see cref="ToDto(ReceiverRow, DiscoveredReceiver?, ReceiverConnectionState, string?, string?)"/>).
    /// Columns the DTO does not carry (<c>pk</c>, quirks, last-seen) are kept from <paramref name="existing"/>.
    /// </summary>
    public static ReceiverRow ToRow(this ReceiverDto dto, ReceiverRow? existing)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var features = AirplayTxtValues.ParseFeatures(dto.Capabilities.Features) ?? (existing is null ? AirplayFeatures.None : FeaturesOf(existing));
        var flags = AirplayTxtValues.ParseFlags(dto.Capabilities.Flags) ?? (existing is null ? AirplayStatusFlags.None : FlagsOf(existing));
        return new ReceiverRow(
            Id: dto.Id,
            Name: dto.Name,
            Model: dto.Model,
            Manufacturer: dto.Manufacturer,
            LastAddress: dto.Address ?? existing?.LastAddress,
            LastPort: dto.Port,
            Features: unchecked((long)(ulong)features),
            StatusFlags: (uint)flags,
            PublicKey: existing?.PublicKey,
            PairingState: dto.Pairing.ToStoreValue(),
            Level: dto.Level,
            OffsetFrames: dto.OffsetFrames,
            Quirks: existing?.Quirks,
            LastSeen: existing?.LastSeen);
    }

    /// <summary>A member DTO: the name comes from the receiver row (the id when the receiver is unknown).</summary>
    public static GroupMemberDto ToDto(this GroupMemberRow member, ReceiverRow? receiver, ReceiverConnectionState state)
    {
        ArgumentNullException.ThrowIfNull(member);
        return new GroupMemberDto(member.ReceiverId, receiver?.Name ?? member.ReceiverId, member.Level, state);
    }

    /// <summary>
    /// A group DTO with its members in position order. <paramref name="stateOf"/> supplies each member's connection state
    /// (default: <see cref="ReceiverConnectionState.Offline"/>).
    /// </summary>
    public static GroupDto ToDto(this GroupRow group, IReadOnlyDictionary<string, ReceiverRow> receivers, bool active, Func<string, ReceiverConnectionState>? stateOf = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(receivers);
        return new GroupDto(group.Id, group.Name, group.MasterLevel, active, MembersOf(group, receivers, stateOf));
    }

    /// <summary>The store row behind a group DTO; positions follow the DTO's member order.</summary>
    public static GroupRow ToRow(this GroupDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return new GroupRow(dto.Id, dto.Name, dto.MasterLevel, dto.Members.Select((m, i) => new GroupMemberRow(dto.Id, m.ReceiverId, m.Level, i)).ToList());
    }

    /// <summary>
    /// The selected output: a group (its G and members), a single receiver (its r_i as the master level, itself as the
    /// only member), or nothing (level 0, no members).
    /// </summary>
    public static OutputDto ToOutputDto(GroupRow? group, ReceiverRow? receiver, IReadOnlyDictionary<string, ReceiverRow> receivers, Func<string, ReceiverConnectionState>? stateOf = null)
    {
        ArgumentNullException.ThrowIfNull(receivers);
        if (group is not null)
        {
            return new OutputDto(group.Id, null, group.MasterLevel, MembersOf(group, receivers, stateOf));
        }

        if (receiver is not null)
        {
            var state = stateOf?.Invoke(receiver.Id) ?? ReceiverConnectionState.Offline;
            return new OutputDto(null, receiver.Id, receiver.Level, [new GroupMemberDto(receiver.Id, receiver.Name, receiver.Level, state)]);
        }

        return new OutputDto(null, null, 0, []);
    }

    /// <summary>Formats a timestamp like the schema's <c>strftime('%Y-%m-%dT%H:%M:%fZ')</c> default (UTC, milliseconds).</summary>
    public static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Parses a stored timestamp (<see cref="FormatTimestamp"/>, or any ISO-8601 form, UTC when unmarked); null when absent or unreadable.</summary>
    public static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;

    /// <summary>The API name of a receiver state (SPEC-receiver.md 9.1): <c>off</c>, <c>starting</c>, <c>advertising</c>, <c>connected</c>, <c>streaming</c>, <c>error</c>.</summary>
    public static string ToApiName(ReceiverStatus status) => status switch
    {
        ReceiverStatus.Off => "off",
        ReceiverStatus.Starting => "starting",
        ReceiverStatus.Advertising => "advertising",
        ReceiverStatus.Connected => "connected",
        ReceiverStatus.Streaming => "streaming",
        _ => "error",
    };

    /// <summary>The API name of a stream type (SPEC-receiver.md 9.1 <c>AirplayDiagnosticsDto.StreamType</c>): <c>realtime</c> or <c>buffered</c>, null without a stream.</summary>
    public static string? ToApiName(StreamType? type) => type switch
    {
        StreamType.Realtime => "realtime",
        StreamType.Buffered => "buffered",
        _ => null,
    };

    /// <summary>The artwork URL of an AirPlay track (SPEC-receiver.md 9.1): <c>/api/playback/artwork?v=&lt;rtptime&gt;</c>.</summary>
    public static string AirplayArtworkUrl(uint key) => ApiRoutes.PlaybackArtwork + "?v=" + key.ToString(CultureInfo.InvariantCulture);

    /// <summary>The receiver as the status reports it (SPEC-receiver.md 9.1 <c>StatusDto.Airplay</c>).</summary>
    public static AirplayReceiverDto ToAirplayReceiverDto(ReceiverStatus status, string name, SenderInfo? sender, bool aacAvailable) =>
        new(ToApiName(status), name, sender?.Name, sender?.Model, aacAvailable);

    /// <summary>
    /// The receiver's diagnostics (SPEC-receiver.md 9.1 <c>DiagnosticsDto.Airplay</c>) from the playout statistics (5.3)
    /// and the PTP slave statistics (6.2); without a stream the counters are 0 and the stream fields null.
    /// </summary>
    public static AirplayDiagnosticsDto ToAirplayDiagnosticsDto(int rtspPort, bool ptpBound, PlayoutStatistics? playout, PtpSlaveStatistics? ptp, string? decoderStatus)
    {
        var p = playout ?? PlayoutStatistics.Empty;
        return new AirplayDiagnosticsDto(
            rtspPort,
            ptpBound,
            ptp?.OffsetNanos,
            ToApiName(p.StreamType),
            p.Decoder,
            p.BufferedMs,
            (int)Math.Round(p.StreamOffset.TotalMilliseconds, MidpointRounding.AwayFromZero),
            p.LateFrames,
            p.MissingFrames,
            p.DriftCorrections,
            decoderStatus);
    }

    /// <summary>
    /// The AirPlay playback view (SPEC-receiver.md 8.3, 9.1): the sender's metadata, <paramref name="elapsed"/> as the
    /// position, <c>TrackId</c> null, <c>Source = "airplay"</c> and the artwork URL of <paramref name="artworkKey"/>.
    /// </summary>
    public static PlaybackDto ToAirplayPlaybackDto(TrackMetadata? metadata, bool playing, TimeSpan elapsed, uint? artworkKey, int level) =>
        new(
            playing,
            null,
            metadata?.Title,
            metadata?.Artist,
            metadata?.Album,
            Math.Max(0, (long)elapsed.TotalMilliseconds),
            Math.Max(0, (long)(metadata?.Duration?.TotalMilliseconds ?? 0)),
            artworkKey is { } key ? AirplayArtworkUrl(key) : null,
            level,
            Output.OutputManager.SourceAirplay);

    /// <summary>The address the bridge shows and stores for a receiver: its IPv4 address if it has one, else the first.</summary>
    private static IPAddress? PreferredAddress(DiscoveredReceiver discovered) =>
        discovered.AddressOf(AddressFamily.InterNetwork) ?? discovered.Addresses.FirstOrDefault();

    private static string FormatFlags(AirplayStatusFlags flags) => "0x" + ((uint)flags).ToString("X", CultureInfo.InvariantCulture);

    private static List<GroupMemberDto> MembersOf(GroupRow group, IReadOnlyDictionary<string, ReceiverRow> receivers, Func<string, ReceiverConnectionState>? stateOf) =>
        group.Members
            .OrderBy(m => m.Position)
            .Select(m => m.ToDto(receivers.GetValueOrDefault(m.ReceiverId), stateOf?.Invoke(m.ReceiverId) ?? ReceiverConnectionState.Offline))
            .ToList();
}
