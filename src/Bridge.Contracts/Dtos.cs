using System.Text.Json.Serialization;

namespace Bridge.Contracts;

/// <summary>REST route constants shared by the service and the web app.</summary>
public static class ApiRoutes
{
    public const string Base = "/api";
    public const string Receivers = Base + "/receivers";
    public const string Receiver = Base + "/receivers/{id}";
    public const string ReceiverPair = Base + "/receivers/{id}/pair";
    public const string ReceiverPairPin = Base + "/receivers/{id}/pair/pin";
    public const string ReceiverUnpair = Base + "/receivers/{id}/pairing";
    public const string ReceiverConnect = Base + "/receivers/{id}/connect";
    public const string ReceiverDisconnect = Base + "/receivers/{id}/disconnect";
    public const string ReceiverVolume = Base + "/receivers/{id}/volume";
    public const string ReceiverOffset = Base + "/receivers/{id}/offset";
    public const string Groups = Base + "/groups";
    public const string Group = Base + "/groups/{id}";
    public const string GroupMembers = Base + "/groups/{id}/members";
    public const string GroupMember = Base + "/groups/{id}/members/{receiverId}";
    public const string GroupVolume = Base + "/groups/{id}/volume";
    public const string GroupActivate = Base + "/groups/{id}/activate";
    public const string Output = Base + "/output";
    public const string Playback = Base + "/playback";
    public const string Settings = Base + "/settings";
    public const string Status = Base + "/status";
    public const string Diagnostics = Base + "/diagnostics";
    public const string Spotify = Base + "/spotify";
    public const string Events = "/ws/events";
}

/// <summary>Pairing state of a receiver as the bridge knows it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PairingState>))]
public enum PairingState
{
    /// <summary>Transient pairing on every connection; nothing to store.</summary>
    Transient,

    /// <summary>Needs a PIN pairing that has not been done.</summary>
    PinRequired,

    /// <summary>A PIN pairing is stored.</summary>
    Paired,

    /// <summary>Only HomeKit home members may connect (acl=1).</summary>
    Disabled,
}

/// <summary>Connection state of a receiver.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReceiverConnectionState>))]
public enum ReceiverConnectionState
{
    Offline,
    Available,
    Connecting,
    Connected,
    Streaming,
    Error,
}

/// <summary>A receiver: discovery data merged with the store.</summary>
/// <remarks>
/// <c>LastSeen</c> (added after the first release, hence optional): when discovery last saw the receiver; for an online
/// receiver its latest mDNS record, for an offline one the stored time it was last seen. Null when unknown.
/// </remarks>
public sealed record ReceiverDto(
    string Id,
    string Name,
    string? Model,
    string? Manufacturer,
    string? Address,
    int Port,
    bool Online,
    PairingState Pairing,
    ReceiverConnectionState State,
    int Level,
    int OffsetFrames,
    string? GroupId,
    string? Error,
    ReceiverCapabilitiesDto Capabilities,
    DateTimeOffset? LastSeen = null);

/// <summary>Capabilities decoded from the TXT records.</summary>
public sealed record ReceiverCapabilitiesDto(bool AirPlay2, bool Transient, bool Pin, bool Buffered, bool Ptp, bool Alac, bool Text, bool Artwork, bool Progress, string Features, string Flags);

/// <summary>A group and its members.</summary>
public sealed record GroupDto(string Id, string Name, int MasterLevel, bool Active, IReadOnlyList<GroupMemberDto> Members);

/// <summary>A group member with its level r_i.</summary>
public sealed record GroupMemberDto(string ReceiverId, string Name, int Level, ReceiverConnectionState State);

/// <summary>Request to create or rename a group.</summary>
public sealed record GroupRequest(string Name);

/// <summary>Request to add a member.</summary>
public sealed record GroupMemberRequest(string ReceiverId, int? Level);

/// <summary>Request to set a level (0..100).</summary>
public sealed record VolumeRequest(int Level);

/// <summary>Request to set a per-receiver offset in frames.</summary>
public sealed record OffsetRequest(int OffsetFrames);

/// <summary>Response of <c>POST /receivers/{id}/pair</c>: the receiver now shows a PIN.</summary>
public sealed record PairingStartedDto(string PairingToken, string Message);

/// <summary>Request to finish a PIN pairing.</summary>
public sealed record PairingPinRequest(string PairingToken, string Pin);

/// <summary>What is currently selected as the output: a receiver or a group.</summary>
public sealed record OutputDto(string? GroupId, string? ReceiverId, int MasterLevel, IReadOnlyList<GroupMemberDto> Members);

/// <summary>Request to select the output.</summary>
public sealed record OutputRequest(string? GroupId, string? ReceiverId);

/// <summary>Playback status mirrored from the Spotify side.</summary>
public sealed record PlaybackDto(bool Playing, string? TrackId, string? Title, string? Artist, string? Album, long PositionMs, long DurationMs, string? ArtworkUrl, int SpotifyVolume);

/// <summary>Playback control request.</summary>
public sealed record PlaybackCommandRequest(string Command);

/// <summary>Settings.</summary>
/// <param name="DeviceName">The Spotify Connect device name.</param>
/// <param name="LatencyFrames">L in frames.</param>
/// <param name="PollReceiverVolume">Poll the receivers' volume while playing.</param>
/// <param name="PreferredStream">"Realtime" or "Buffered".</param>
/// <param name="ForgetOfflineAfterDays">
/// Offline receivers without user state are forgotten after this many days (0 = never). Always set in answers; null in a
/// <c>PUT</c> keeps the current value (clients that predate the field).
/// </param>
public sealed record SettingsDto(string DeviceName, int LatencyFrames, bool PollReceiverVolume, string PreferredStream, double? ForgetOfflineAfterDays = null);

/// <summary>Answer of <c>DELETE /receivers?offline=true</c>: the ids of the receivers that were forgotten.</summary>
public sealed record ForgottenReceiversDto(IReadOnlyList<string> Removed);

/// <summary>Service status.</summary>
public sealed record StatusDto(string Version, bool SpotifyConnected, string? SpotifyUser, string DeviceName, bool OutputConnected, int Receivers, DateTimeOffset StartedAt);

/// <summary>Diagnostics page data.</summary>
public sealed record DiagnosticsDto(IReadOnlyList<string> RecentLog, IReadOnlyList<string> LocalAddresses, int TimingPort, int ControlPort, long PacketsSent, long RetransmitRequests, long LateWakeups, IReadOnlyList<string> OpenQuestionsPending);

/// <summary>WebSocket messages from the service to the web app, discriminated by <c>type</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ReceiverChanged), "receiverChanged")]
[JsonDerivedType(typeof(ReceiverRemoved), "receiverRemoved")]
[JsonDerivedType(typeof(GroupChanged), "groupChanged")]
[JsonDerivedType(typeof(GroupRemoved), "groupRemoved")]
[JsonDerivedType(typeof(OutputChanged), "outputChanged")]
[JsonDerivedType(typeof(PlaybackChanged), "playbackChanged")]
[JsonDerivedType(typeof(VolumeChanged), "volumeChanged")]
[JsonDerivedType(typeof(PairingRequired), "pairingRequired")]
[JsonDerivedType(typeof(StatusChanged), "statusChanged")]
[JsonDerivedType(typeof(LogLine), "log")]
[JsonDerivedType(typeof(Pong), "pong")]
public abstract record BridgeEvent
{
    public sealed record ReceiverChanged(ReceiverDto Receiver) : BridgeEvent;

    public sealed record ReceiverRemoved(string Id) : BridgeEvent;

    public sealed record GroupChanged(GroupDto Group) : BridgeEvent;

    public sealed record GroupRemoved(string Id) : BridgeEvent;

    public sealed record OutputChanged(OutputDto Output) : BridgeEvent;

    public sealed record PlaybackChanged(PlaybackDto Playback) : BridgeEvent;

    /// <summary>A level changed: the group master (ReceiverId null) or one receiver (from the UI, Spotify, or the device itself).</summary>
    public sealed record VolumeChanged(string? GroupId, string? ReceiverId, int Level, string Source) : BridgeEvent;

    /// <summary>Connecting to a receiver needs a PIN; the UI opens the pairing dialog.</summary>
    public sealed record PairingRequired(string ReceiverId, string PairingToken) : BridgeEvent;

    public sealed record StatusChanged(StatusDto Status) : BridgeEvent;

    public sealed record LogLine(DateTimeOffset At, string Level, string Category, string Message) : BridgeEvent;

    /// <summary>The answer to a client's <see cref="BridgeClientMessage.Ping"/> (added by bridge-service).</summary>
    public sealed record Pong(DateTimeOffset At) : BridgeEvent;
}

/// <summary>Messages from the web app to the service over the same WebSocket (subscription control only; actions use REST).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Subscribe), "subscribe")]
[JsonDerivedType(typeof(Ping), "ping")]
public abstract record BridgeClientMessage
{
    /// <summary>Selects whether log lines are streamed (diagnostics page).</summary>
    public sealed record Subscribe(bool Log) : BridgeClientMessage;

    public sealed record Ping : BridgeClientMessage;
}
