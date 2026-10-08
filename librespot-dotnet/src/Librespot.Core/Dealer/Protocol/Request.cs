// Port of core/src/dealer/protocol/request.rs (data shapes). The JSON deserialization of the `endpoint`-tagged
// Command union and the proto-in-JSON / base64-proto field converters (upstream deserialize_with.rs ->
// DeserializeWith.cs) are added by package `core-dealer`; the records here are the contract that
// connect-state / connect-spirc consume.
//
// Namespace note: this file lives in Librespot.Core.Dealer (not .Dealer.Protocol), see Protocol.cs.
// TODO(port: core-dealer): JsonConverter for Command (tag "endpoint", snake_case, Unknown = untagged fallback),
//   base64_proto / json_proto / option_json_proto / vec_json_proto converters on the proto-typed properties,
//   Command.ToDisplayString() ("endpoint: {name}" / "endpoint: unknown {name|???}").
using System.Text.Json;
using System.Text.Json.Serialization;
// Upstream imports protocol::player::{PlayOrigin, ProvidedTrack} (player.proto -> Spotify.Connectstate) and
// protocol::context::Context / context_player_options (-> Spotify.Player.Proto); PlayOrigin also exists in
// Spotify.Player.Proto (play_origin.proto), hence the aliases.
using Context = Spotify.Player.Proto.Context;
using ContextPlayerOptionOverrides = Spotify.Player.Proto.ContextPlayerOptionOverrides;
using PlayOrigin = Spotify.Connectstate.PlayOrigin;
using ProvidedTrack = Spotify.Connectstate.ProvidedTrack;
using TransferState = Spotify.Player.Proto.Transfer.TransferState;

namespace Librespot.Core.Dealer;

/// <summary>A dealer request (upstream <c>dealer::protocol::Request</c>), decoded from the websocket envelope's payload.</summary>
public sealed record Request
{
    [JsonPropertyName("message_id")]
    public uint MessageId { get; init; }

    // upstream: target_alias_id was only ever seen as null and is ignored.

    [JsonPropertyName("sent_by_device_id")]
    public string SentByDeviceId { get; init; } = "";

    [JsonPropertyName("command")]
    public Command Command { get; init; } = new Command.Unknown(default);
}

/// <summary>Upstream <c>Command</c>: the request payload, tagged by its <c>endpoint</c> field.</summary>
public abstract record Command
{
    private Command()
    {
    }

    public sealed record Transfer(TransferCommand Value) : Command;

    public sealed record Play(PlayCommand Value) : Command;

    public sealed record Pause(PauseCommand Value) : Command;

    public sealed record SeekTo(SeekToCommand Value) : Command;

    public sealed record SetShufflingContext(SetValueCommand Value) : Command;

    public sealed record SetRepeatingTrack(SetValueCommand Value) : Command;

    public sealed record SetRepeatingContext(SetValueCommand Value) : Command;

    public sealed record AddToQueue(AddToQueueCommand Value) : Command;

    public sealed record SetQueue(SetQueueCommand Value) : Command;

    public sealed record SetOptions(SetOptionsCommand Value) : Command;

    public sealed record UpdateContext(UpdateContextCommand Value) : Command;

    public sealed record SkipNext(SkipNextCommand Value) : Command;

    /// <summary>Commands that don't send any context (at least not usually...).</summary>
    public sealed record SkipPrev(GenericCommand Value) : Command;

    public sealed record Resume(GenericCommand Value) : Command;

    /// <summary>Catch-all for unknown commands, so that we can implement them later.</summary>
    public sealed record Unknown(JsonElement Value) : Command;

    /// <summary>Upstream <c>Display</c>: <c>endpoint: {name}</c>, or <c>endpoint: unknown {name|???}</c>.</summary>
    public string ToDisplayString() => throw new NotImplementedException("TODO(port: core-dealer)");
}

public sealed record TransferCommand
{
    /// <summary>Base64-encoded <c>TransferState</c> protobuf (upstream <c>base64_proto</c>).</summary>
    [JsonPropertyName("data")]
    public TransferState? Data { get; init; }

    [JsonPropertyName("options")]
    public TransferOptions Options { get; init; } = new();

    [JsonPropertyName("from_device_identifier")]
    public string FromDeviceIdentifier { get; init; } = "";

    [JsonPropertyName("logging_params")]
    public LoggingParams LoggingParams { get; init; } = new();
}

public sealed record PlayCommand
{
    /// <summary>Protobuf-JSON (upstream <c>json_proto</c>).</summary>
    [JsonPropertyName("context")]
    public Context Context { get; init; } = new();

    /// <summary>Protobuf-JSON (upstream <c>json_proto</c>).</summary>
    [JsonPropertyName("play_origin")]
    public PlayOrigin PlayOrigin { get; init; } = new();

    [JsonPropertyName("options")]
    public PlayOptions Options { get; init; } = new();

    [JsonPropertyName("logging_params")]
    public LoggingParams LoggingParams { get; init; } = new();
}

public sealed record PauseCommand
{
    // upstream: does send options with it, but seems to be empty; investigate which options are sent here
    [JsonPropertyName("logging_params")]
    public LoggingParams LoggingParams { get; init; } = new();
}

public sealed record SeekToCommand
{
    [JsonPropertyName("value")]
    public uint Value { get; init; }

    [JsonPropertyName("position")]
    public uint Position { get; init; }

    [JsonPropertyName("logging_params")]
    public LoggingParams LoggingParams { get; init; } = new();
}

public sealed record SkipNextCommand
{
    /// <summary>Protobuf-JSON (upstream <c>option_json_proto</c>).</summary>
    [JsonPropertyName("track")]
    public ProvidedTrack? Track { get; init; }

    [JsonPropertyName("logging_params")]
    public LoggingParams LoggingParams { get; init; } = new();
}

public sealed record SetValueCommand
{
    [JsonPropertyName("value")]
    public bool Value { get; init; }

    [JsonPropertyName("logging_params")]
    public LoggingParams LoggingParams { get; init; } = new();
}

public sealed record AddToQueueCommand
{
    /// <summary>Protobuf-JSON (upstream <c>json_proto</c>).</summary>
    [JsonPropertyName("track")]
    public ProvidedTrack Track { get; init; } = new();

    [JsonPropertyName("logging_params")]
    public LoggingParams LoggingParams { get; init; } = new();
}

public sealed record SetQueueCommand
{
    /// <summary>Protobuf-JSON array (upstream <c>vec_json_proto</c>; unparsable elements are skipped).</summary>
    [JsonPropertyName("next_tracks")]
    public IReadOnlyList<ProvidedTrack> NextTracks { get; init; } = [];

    /// <summary>Protobuf-JSON array (upstream <c>vec_json_proto</c>; unparsable elements are skipped).</summary>
    [JsonPropertyName("prev_tracks")]
    public IReadOnlyList<ProvidedTrack> PrevTracks { get; init; } = [];

    /// <summary>
    /// Upstream comment: this queue revision is actually the last revision, so using it will not update the web UI;
    /// might be that internally they use the last revision to create the next revision.
    /// </summary>
    [JsonPropertyName("queue_revision")]
    public string QueueRevision { get; init; } = "";

    [JsonPropertyName("logging_params")]
    public LoggingParams LoggingParams { get; init; } = new();
}

public sealed record SetOptionsCommand
{
    [JsonPropertyName("shuffling_context")]
    public bool? ShufflingContext { get; init; }

    [JsonPropertyName("repeating_context")]
    public bool? RepeatingContext { get; init; }

    [JsonPropertyName("repeating_track")]
    public bool? RepeatingTrack { get; init; }

    [JsonPropertyName("options")]
    public OptionsOptions? Options { get; init; }

    [JsonPropertyName("logging_params")]
    public LoggingParams LoggingParams { get; init; } = new();
}

public sealed record UpdateContextCommand
{
    /// <summary>Protobuf-JSON (upstream <c>json_proto</c>).</summary>
    [JsonPropertyName("context")]
    public Context Context { get; init; } = new();

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}

public sealed record GenericCommand
{
    [JsonPropertyName("logging_params")]
    public LoggingParams LoggingParams { get; init; } = new();
}

/// <summary>Upstream <c>TransferOptions</c> (also serialized, by connect's transfer handling; nulls are omitted).</summary>
public sealed record TransferOptions
{
    [JsonPropertyName("restore_paused")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RestorePaused { get; init; }

    [JsonPropertyName("restore_position")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RestorePosition { get; init; }

    [JsonPropertyName("restore_track")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RestoreTrack { get; init; }

    [JsonPropertyName("retain_session")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RetainSession { get; init; }
}

public sealed record PlayOptions
{
    [JsonPropertyName("skip_to")]
    public SkipTo? SkipTo { get; init; }

    /// <summary>Protobuf-JSON (upstream <c>option_json_proto</c>).</summary>
    [JsonPropertyName("player_options_override")]
    public ContextPlayerOptionOverrides? PlayerOptionsOverride { get; init; }

    [JsonPropertyName("license")]
    public string? License { get; init; }

    /// <summary>Possible to send via web-api.</summary>
    [JsonPropertyName("seek_to")]
    public uint? SeekTo { get; init; }

    // mobile
    [JsonPropertyName("always_play_something")]
    public bool? AlwaysPlaySomething { get; init; }

    [JsonPropertyName("audio_stream")]
    public string? AudioStream { get; init; }

    [JsonPropertyName("initially_paused")]
    public bool? InitiallyPaused { get; init; }

    [JsonPropertyName("prefetch_level")]
    public string? PrefetchLevel { get; init; }

    [JsonPropertyName("system_initiated")]
    public bool? SystemInitiated { get; init; }
}

/// <summary>Upstream <c>OptionsOptions</c> (fields are private upstream; kept readable here for logging).</summary>
public sealed record OptionsOptions
{
    [JsonPropertyName("only_for_local_device")]
    public bool OnlyForLocalDevice { get; init; }

    [JsonPropertyName("override_restrictions")]
    public bool OverrideRestrictions { get; init; }

    [JsonPropertyName("system_initiated")]
    public bool SystemInitiated { get; init; }
}

public sealed record SkipTo
{
    [JsonPropertyName("track_uid")]
    public string? TrackUid { get; init; }

    [JsonPropertyName("track_uri")]
    public string? TrackUri { get; init; }

    [JsonPropertyName("track_index")]
    public uint? TrackIndex { get; init; }
}

public sealed record LoggingParams
{
    [JsonPropertyName("interaction_ids")]
    public IReadOnlyList<string>? InteractionIds { get; init; }

    [JsonPropertyName("device_identifier")]
    public string? DeviceIdentifier { get; init; }

    [JsonPropertyName("command_initiated_time")]
    public long? CommandInitiatedTime { get; init; }

    [JsonPropertyName("page_instance_ids")]
    public IReadOnlyList<string>? PageInstanceIds { get; init; }

    [JsonPropertyName("command_id")]
    public string? CommandId { get; init; }
}
