// Port of core/src/dealer/protocol/request.rs.
//
// serde -> System.Text.Json: the `endpoint`-tagged Command union is read by CommandJsonConverter (an unknown
// endpoint, or a known endpoint whose body does not deserialize, falls back to Command.Unknown with the raw JSON,
// exactly like upstream's `#[serde(untagged)] Unknown(Value)`); proto-typed properties use the converters of
// DeserializeWith.cs. Properties that are neither `Option<T>` nor `#[serde(default)]` upstream are [JsonRequired],
// and DealerJson.Options sets RespectNullableAnnotations, so a body that upstream rejects is rejected here too.
//
// Namespace note: this file lives in Librespot.Core.Dealer (not .Dealer.Protocol), see Protocol.cs.
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
    /// <summary>Upstream <c>message_id</c>.</summary>
    [JsonPropertyName("message_id")]
    [JsonRequired]
    public uint MessageId { get; init; }

    // upstream: target_alias_id was only ever seen as null and is ignored.

    /// <summary>Upstream <c>sent_by_device_id</c>.</summary>
    [JsonPropertyName("sent_by_device_id")]
    [JsonRequired]
    public string SentByDeviceId { get; init; } = "";

    /// <summary>Upstream <c>command</c>.</summary>
    [JsonPropertyName("command")]
    [JsonRequired]
    public Command Command { get; init; } = new Command.Unknown(default);
}

/// <summary>Upstream <c>Command</c>: the request payload, tagged by its <c>endpoint</c> field.</summary>
[JsonConverter(typeof(CommandJsonConverter))]
public abstract record Command
{
    private Command()
    {
    }

    /// <summary>Endpoint <c>transfer</c>.</summary>
    public sealed record Transfer(TransferCommand Value) : Command;

    /// <summary>Endpoint <c>play</c> (upstream boxes the large payload; records are references already).</summary>
    public sealed record Play(PlayCommand Value) : Command;

    /// <summary>Endpoint <c>pause</c>.</summary>
    public sealed record Pause(PauseCommand Value) : Command;

    /// <summary>Endpoint <c>seek_to</c>.</summary>
    public sealed record SeekTo(SeekToCommand Value) : Command;

    /// <summary>Endpoint <c>set_shuffling_context</c>.</summary>
    public sealed record SetShufflingContext(SetValueCommand Value) : Command;

    /// <summary>Endpoint <c>set_repeating_track</c>.</summary>
    public sealed record SetRepeatingTrack(SetValueCommand Value) : Command;

    /// <summary>Endpoint <c>set_repeating_context</c>.</summary>
    public sealed record SetRepeatingContext(SetValueCommand Value) : Command;

    /// <summary>Endpoint <c>add_to_queue</c>.</summary>
    public sealed record AddToQueue(AddToQueueCommand Value) : Command;

    /// <summary>Endpoint <c>set_queue</c>.</summary>
    public sealed record SetQueue(SetQueueCommand Value) : Command;

    /// <summary>Endpoint <c>set_options</c>.</summary>
    public sealed record SetOptions(SetOptionsCommand Value) : Command;

    /// <summary>Endpoint <c>update_context</c>.</summary>
    public sealed record UpdateContext(UpdateContextCommand Value) : Command;

    /// <summary>Endpoint <c>skip_next</c>.</summary>
    public sealed record SkipNext(SkipNextCommand Value) : Command;

    /// <summary>Endpoint <c>skip_prev</c>; commands that don't send any context (at least not usually...).</summary>
    public sealed record SkipPrev(GenericCommand Value) : Command;

    /// <summary>Endpoint <c>resume</c>.</summary>
    public sealed record Resume(GenericCommand Value) : Command;

    /// <summary>Catch-all for unknown commands, so that we can implement them later.</summary>
    public sealed record Unknown(JsonElement Value) : Command;

    /// <summary>Upstream <c>Display</c>: <c>endpoint: {name}</c>, or <c>endpoint: unknown {name|???}</c>.</summary>
    public string ToDisplayString()
    {
        var name = this switch
        {
            Transfer => "transfer",
            Play => "play",
            Pause => "pause",
            SeekTo => "seek_to",
            SetShufflingContext => "set_shuffling_context",
            SetRepeatingContext => "set_repeating_context",
            SetRepeatingTrack => "set_repeating_track",
            AddToQueue => "add_to_queue",
            SetQueue => "set_queue",
            SetOptions => "set_options",
            UpdateContext => "update_context",
            SkipNext => "skip_next",
            SkipPrev => "skip_prev",
            Resume => "resume",
            Unknown u => UnknownEndpoint(u.Value),
            _ => throw new InvalidOperationException("closed hierarchy"),
        };

        return $"endpoint: {(this is Unknown ? "unknown " : "")}{name}";
    }

    private static string UnknownEndpoint(JsonElement json) =>
        json.ValueKind == JsonValueKind.Object
        && json.TryGetProperty("endpoint", out var endpoint)
        && endpoint.ValueKind == JsonValueKind.String
            ? endpoint.GetString()!
            : "???";
}

/// <summary>
/// Reads the <c>endpoint</c>-tagged <see cref="Command"/> union (upstream <c>#[serde(tag = "endpoint",
/// rename_all = "snake_case")]</c> with an untagged <c>Unknown(Value)</c> fallback). serde first tries the tagged
/// variants and, if that fails for any reason (missing or unknown tag, or a body that does not deserialize),
/// the untagged one; any JSON value therefore yields a <see cref="Command"/>.
/// </summary>
internal sealed class CommandJsonConverter : JsonConverter<Command>
{
    public override bool HandleNull => true;

    public override Command Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = DeserializeWith.ReadValue(ref reader);
        return TryReadTagged(value, options) ?? new Command.Unknown(value);
    }

    private static Command? TryReadTagged(JsonElement value, JsonSerializerOptions options)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("endpoint", out var tag)
            || tag.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            return tag.GetString() switch
            {
                "transfer" => new Command.Transfer(Body<TransferCommand>(value, options)),
                "play" => new Command.Play(Body<PlayCommand>(value, options)),
                "pause" => new Command.Pause(Body<PauseCommand>(value, options)),
                "seek_to" => new Command.SeekTo(Body<SeekToCommand>(value, options)),
                "set_shuffling_context" => new Command.SetShufflingContext(Body<SetValueCommand>(value, options)),
                "set_repeating_track" => new Command.SetRepeatingTrack(Body<SetValueCommand>(value, options)),
                "set_repeating_context" => new Command.SetRepeatingContext(Body<SetValueCommand>(value, options)),
                "add_to_queue" => new Command.AddToQueue(Body<AddToQueueCommand>(value, options)),
                "set_queue" => new Command.SetQueue(Body<SetQueueCommand>(value, options)),
                "set_options" => new Command.SetOptions(Body<SetOptionsCommand>(value, options)),
                "update_context" => new Command.UpdateContext(Body<UpdateContextCommand>(value, options)),
                "skip_next" => new Command.SkipNext(Body<SkipNextCommand>(value, options)),
                "skip_prev" => new Command.SkipPrev(Body<GenericCommand>(value, options)),
                "resume" => new Command.Resume(Body<GenericCommand>(value, options)),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static T Body<T>(JsonElement value, JsonSerializerOptions options) where T : class =>
        value.Deserialize<T>(options) ?? throw new JsonException($"{typeof(T).Name} was null");

    /// <summary>Not supported: upstream <c>Command</c> is deserialize-only.</summary>
    public override void Write(Utf8JsonWriter writer, Command value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Command is deserialize-only (as upstream)");
}

/// <summary>Upstream <c>TransferCommand</c>.</summary>
public sealed record TransferCommand
{
    /// <summary>Base64-encoded <c>TransferState</c> protobuf (upstream <c>base64_proto</c>, default <c>None</c>).</summary>
    [JsonPropertyName("data")]
    [JsonConverter(typeof(Base64ProtoConverter<TransferState>))]
    public TransferState? Data { get; init; }

    /// <summary>Upstream <c>options</c>.</summary>
    [JsonPropertyName("options")]
    [JsonRequired]
    public TransferOptions Options { get; init; } = new();

    /// <summary>Upstream <c>from_device_identifier</c>.</summary>
    [JsonPropertyName("from_device_identifier")]
    [JsonRequired]
    public string FromDeviceIdentifier { get; init; } = "";

    /// <summary>Upstream <c>logging_params</c>.</summary>
    [JsonPropertyName("logging_params")]
    [JsonRequired]
    public LoggingParams LoggingParams { get; init; } = new();
}

/// <summary>Upstream <c>PlayCommand</c>.</summary>
public sealed record PlayCommand
{
    /// <summary>Protobuf-JSON (upstream <c>json_proto</c>).</summary>
    [JsonPropertyName("context")]
    [JsonRequired]
    [JsonConverter(typeof(JsonProtoConverter<Context>))]
    public Context Context { get; init; } = new();

    /// <summary>Protobuf-JSON (upstream <c>json_proto</c>).</summary>
    [JsonPropertyName("play_origin")]
    [JsonRequired]
    [JsonConverter(typeof(JsonProtoConverter<PlayOrigin>))]
    public PlayOrigin PlayOrigin { get; init; } = new();

    /// <summary>Upstream <c>options</c>.</summary>
    [JsonPropertyName("options")]
    [JsonRequired]
    public PlayOptions Options { get; init; } = new();

    /// <summary>Upstream <c>logging_params</c>.</summary>
    [JsonPropertyName("logging_params")]
    [JsonRequired]
    public LoggingParams LoggingParams { get; init; } = new();
}

/// <summary>Upstream <c>PauseCommand</c>.</summary>
public sealed record PauseCommand
{
    // upstream: does send options with it, but seems to be empty; investigate which options are sent here

    /// <summary>Upstream <c>logging_params</c>.</summary>
    [JsonPropertyName("logging_params")]
    [JsonRequired]
    public LoggingParams LoggingParams { get; init; } = new();
}

/// <summary>Upstream <c>SeekToCommand</c>.</summary>
public sealed record SeekToCommand
{
    /// <summary>Upstream <c>value</c>.</summary>
    [JsonPropertyName("value")]
    [JsonRequired]
    public uint Value { get; init; }

    /// <summary>Upstream <c>position</c>.</summary>
    [JsonPropertyName("position")]
    [JsonRequired]
    public uint Position { get; init; }

    /// <summary>Upstream <c>logging_params</c>.</summary>
    [JsonPropertyName("logging_params")]
    [JsonRequired]
    public LoggingParams LoggingParams { get; init; } = new();
}

/// <summary>Upstream <c>SkipNextCommand</c>.</summary>
public sealed record SkipNextCommand
{
    /// <summary>Protobuf-JSON (upstream <c>option_json_proto</c>, default <c>None</c>).</summary>
    [JsonPropertyName("track")]
    [JsonConverter(typeof(OptionJsonProtoConverter<ProvidedTrack>))]
    public ProvidedTrack? Track { get; init; }

    /// <summary>Upstream <c>logging_params</c>.</summary>
    [JsonPropertyName("logging_params")]
    [JsonRequired]
    public LoggingParams LoggingParams { get; init; } = new();
}

/// <summary>Upstream <c>SetValueCommand</c>.</summary>
public sealed record SetValueCommand
{
    /// <summary>Upstream <c>value</c>.</summary>
    [JsonPropertyName("value")]
    [JsonRequired]
    public bool Value { get; init; }

    /// <summary>Upstream <c>logging_params</c>.</summary>
    [JsonPropertyName("logging_params")]
    [JsonRequired]
    public LoggingParams LoggingParams { get; init; } = new();
}

/// <summary>Upstream <c>AddToQueueCommand</c>.</summary>
public sealed record AddToQueueCommand
{
    /// <summary>Protobuf-JSON (upstream <c>json_proto</c>).</summary>
    [JsonPropertyName("track")]
    [JsonRequired]
    [JsonConverter(typeof(JsonProtoConverter<ProvidedTrack>))]
    public ProvidedTrack Track { get; init; } = new();

    /// <summary>Upstream <c>logging_params</c>.</summary>
    [JsonPropertyName("logging_params")]
    [JsonRequired]
    public LoggingParams LoggingParams { get; init; } = new();
}

/// <summary>Upstream <c>SetQueueCommand</c>.</summary>
public sealed record SetQueueCommand
{
    /// <summary>Protobuf-JSON array (upstream <c>vec_json_proto</c>; unparsable elements are skipped).</summary>
    [JsonPropertyName("next_tracks")]
    [JsonRequired]
    [JsonConverter(typeof(VecJsonProtoConverter<ProvidedTrack>))]
    public IReadOnlyList<ProvidedTrack> NextTracks { get; init; } = [];

    /// <summary>Protobuf-JSON array (upstream <c>vec_json_proto</c>; unparsable elements are skipped).</summary>
    [JsonPropertyName("prev_tracks")]
    [JsonRequired]
    [JsonConverter(typeof(VecJsonProtoConverter<ProvidedTrack>))]
    public IReadOnlyList<ProvidedTrack> PrevTracks { get; init; } = [];

    /// <summary>
    /// Upstream comment: this queue revision is actually the last revision, so using it will not update the web UI;
    /// might be that internally they use the last revision to create the next revision.
    /// </summary>
    [JsonPropertyName("queue_revision")]
    [JsonRequired]
    public string QueueRevision { get; init; } = "";

    /// <summary>Upstream <c>logging_params</c>.</summary>
    [JsonPropertyName("logging_params")]
    [JsonRequired]
    public LoggingParams LoggingParams { get; init; } = new();
}

/// <summary>Upstream <c>SetOptionsCommand</c>.</summary>
public sealed record SetOptionsCommand
{
    /// <summary>Upstream <c>shuffling_context</c>.</summary>
    [JsonPropertyName("shuffling_context")]
    public bool? ShufflingContext { get; init; }

    /// <summary>Upstream <c>repeating_context</c>.</summary>
    [JsonPropertyName("repeating_context")]
    public bool? RepeatingContext { get; init; }

    /// <summary>Upstream <c>repeating_track</c>.</summary>
    [JsonPropertyName("repeating_track")]
    public bool? RepeatingTrack { get; init; }

    /// <summary>Upstream <c>options</c>.</summary>
    [JsonPropertyName("options")]
    public OptionsOptions? Options { get; init; }

    /// <summary>Upstream <c>logging_params</c>.</summary>
    [JsonPropertyName("logging_params")]
    [JsonRequired]
    public LoggingParams LoggingParams { get; init; } = new();
}

/// <summary>Upstream <c>UpdateContextCommand</c>.</summary>
public sealed record UpdateContextCommand
{
    /// <summary>Protobuf-JSON (upstream <c>json_proto</c>).</summary>
    [JsonPropertyName("context")]
    [JsonRequired]
    [JsonConverter(typeof(JsonProtoConverter<Context>))]
    public Context Context { get; init; } = new();

    /// <summary>Upstream <c>session_id</c>.</summary>
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}

/// <summary>Upstream <c>GenericCommand</c>: commands that only carry logging parameters.</summary>
public sealed record GenericCommand
{
    /// <summary>Upstream <c>logging_params</c>.</summary>
    [JsonPropertyName("logging_params")]
    [JsonRequired]
    public LoggingParams LoggingParams { get; init; } = new();
}

/// <summary>Upstream <c>TransferOptions</c> (also serialized, by connect's transfer handling; nulls are omitted).</summary>
public sealed record TransferOptions
{
    /// <summary>Upstream <c>restore_paused</c>.</summary>
    [JsonPropertyName("restore_paused")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RestorePaused { get; init; }

    /// <summary>Upstream <c>restore_position</c>.</summary>
    [JsonPropertyName("restore_position")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RestorePosition { get; init; }

    /// <summary>Upstream <c>restore_track</c>.</summary>
    [JsonPropertyName("restore_track")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RestoreTrack { get; init; }

    /// <summary>Upstream <c>retain_session</c>.</summary>
    [JsonPropertyName("retain_session")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RetainSession { get; init; }
}

/// <summary>Upstream <c>PlayOptions</c>.</summary>
public sealed record PlayOptions
{
    /// <summary>Upstream <c>skip_to</c>.</summary>
    [JsonPropertyName("skip_to")]
    public SkipTo? SkipTo { get; init; }

    /// <summary>Protobuf-JSON (upstream <c>option_json_proto</c>, default <c>None</c>).</summary>
    [JsonPropertyName("player_options_override")]
    [JsonConverter(typeof(OptionJsonProtoConverter<ContextPlayerOptionOverrides>))]
    public ContextPlayerOptionOverrides? PlayerOptionsOverride { get; init; }

    /// <summary>Upstream <c>license</c>.</summary>
    [JsonPropertyName("license")]
    public string? License { get; init; }

    /// <summary>Possible to send via web-api.</summary>
    [JsonPropertyName("seek_to")]
    public uint? SeekTo { get; init; }

    // mobile

    /// <summary>Upstream <c>always_play_something</c> (mobile).</summary>
    [JsonPropertyName("always_play_something")]
    public bool? AlwaysPlaySomething { get; init; }

    /// <summary>Upstream <c>audio_stream</c> (mobile).</summary>
    [JsonPropertyName("audio_stream")]
    public string? AudioStream { get; init; }

    /// <summary>Upstream <c>initially_paused</c> (mobile).</summary>
    [JsonPropertyName("initially_paused")]
    public bool? InitiallyPaused { get; init; }

    /// <summary>Upstream <c>prefetch_level</c> (mobile).</summary>
    [JsonPropertyName("prefetch_level")]
    public string? PrefetchLevel { get; init; }

    /// <summary>Upstream <c>system_initiated</c> (mobile).</summary>
    [JsonPropertyName("system_initiated")]
    public bool? SystemInitiated { get; init; }
}

/// <summary>Upstream <c>OptionsOptions</c> (fields are private upstream; kept readable here for logging).</summary>
public sealed record OptionsOptions
{
    /// <summary>Upstream <c>only_for_local_device</c>.</summary>
    [JsonPropertyName("only_for_local_device")]
    [JsonRequired]
    public bool OnlyForLocalDevice { get; init; }

    /// <summary>Upstream <c>override_restrictions</c>.</summary>
    [JsonPropertyName("override_restrictions")]
    [JsonRequired]
    public bool OverrideRestrictions { get; init; }

    /// <summary>Upstream <c>system_initiated</c>.</summary>
    [JsonPropertyName("system_initiated")]
    [JsonRequired]
    public bool SystemInitiated { get; init; }
}

/// <summary>Upstream <c>SkipTo</c>.</summary>
public sealed record SkipTo
{
    /// <summary>Upstream <c>track_uid</c>.</summary>
    [JsonPropertyName("track_uid")]
    public string? TrackUid { get; init; }

    /// <summary>Upstream <c>track_uri</c>.</summary>
    [JsonPropertyName("track_uri")]
    public string? TrackUri { get; init; }

    /// <summary>Upstream <c>track_index</c>.</summary>
    [JsonPropertyName("track_index")]
    public uint? TrackIndex { get; init; }
}

/// <summary>Upstream <c>LoggingParams</c>.</summary>
public sealed record LoggingParams
{
    /// <summary>Upstream <c>interaction_ids</c>.</summary>
    [JsonPropertyName("interaction_ids")]
    public IReadOnlyList<string>? InteractionIds { get; init; }

    /// <summary>Upstream <c>device_identifier</c>.</summary>
    [JsonPropertyName("device_identifier")]
    public string? DeviceIdentifier { get; init; }

    /// <summary>Upstream <c>command_initiated_time</c>.</summary>
    [JsonPropertyName("command_initiated_time")]
    public long? CommandInitiatedTime { get; init; }

    /// <summary>Upstream <c>page_instance_ids</c>.</summary>
    [JsonPropertyName("page_instance_ids")]
    public IReadOnlyList<string>? PageInstanceIds { get; init; }

    /// <summary>Upstream <c>command_id</c>.</summary>
    [JsonPropertyName("command_id")]
    public string? CommandId { get; init; }
}
