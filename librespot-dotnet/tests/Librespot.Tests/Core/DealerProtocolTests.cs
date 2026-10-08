using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Librespot.Core;
using Librespot.Core.Dealer;
using Spotify.Connectstate;
using Xunit;

namespace Librespot.Tests.Core;

// Upstream core/src/dealer/protocol.rs and protocol/request.rs have no tests; these use the wire shapes they define.
public class DealerProtocolTests
{
    internal static string GzipBase64(string text)
    {
        using var buffer = new MemoryStream();
        using (var gz = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            gz.Write(Encoding.UTF8.GetBytes(text));
        }

        return Convert.ToBase64String(buffer.ToArray());
    }

    internal static string RequestEnvelope(string innerJson, string messageIdent = "hm://connect-state/v1/player/command", string key = "key-1") =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "request",
            ["message_ident"] = messageIdent,
            ["key"] = key,
            ["headers"] = new Dictionary<string, string> { ["Transfer-Encoding"] = "gzip" },
            ["payload"] = new Dictionary<string, string> { ["compressed"] = GzipBase64(innerJson) },
        });

    private static Command ParseCommand(string commandJson)
    {
        var envelope = RequestEnvelope($$"""{"message_id":7,"sent_by_device_id":"dev","command":{{commandJson}}}""");
        var parsed = Assert.IsType<MessageOrRequest.Request>(MessageOrRequest.Parse(envelope));
        var request = parsed.Value.HandlePayload();
        Assert.Equal(7u, request.MessageId);
        Assert.Equal("dev", request.SentByDeviceId);
        return request.Command;
    }

    private const string Logging =
        "\"logging_params\":{\"command_id\":\"c1\",\"interaction_ids\":[\"i1\"],\"command_initiated_time\":1700000000000}";

    [Fact]
    public void Request_envelope_with_gzip_payload_decodes_play_command()
    {
        var command = ParseCommand($$"""
            {
              "endpoint": "play",
              "context": {"uri": "spotify:playlist:abc", "url": "context://spotify:playlist:abc", "metadata": {"k": "v"}, "unknown_field": 1},
              "play_origin": {"feature_identifier": "harmony", "feature_version": "4.1.0"},
              "options": {
                "skip_to": {"track_uid": "uid1", "track_index": 3},
                "player_options_override": {"shuffling_context": true},
                "license": "premium",
                "seek_to": 1234
              },
              {{Logging}}
            }
            """);

        var play = Assert.IsType<Command.Play>(command).Value;
        Assert.Equal("spotify:playlist:abc", play.Context.Uri);
        Assert.Equal("v", play.Context.Metadata["k"]);
        Assert.Equal("harmony", play.PlayOrigin.FeatureIdentifier);
        Assert.Equal("uid1", play.Options.SkipTo!.TrackUid);
        Assert.Equal(3u, play.Options.SkipTo.TrackIndex);
        Assert.True(play.Options.PlayerOptionsOverride!.ShufflingContext);
        Assert.Equal(1234u, play.Options.SeekTo);
        Assert.Null(play.Options.InitiallyPaused);
        Assert.Equal("c1", play.LoggingParams.CommandId);
        Assert.Equal(new[] { "i1" }, play.LoggingParams.InteractionIds);
        Assert.Equal(1700000000000L, play.LoggingParams.CommandInitiatedTime);
        Assert.Equal("endpoint: play", command.ToDisplayString());
    }

    [Fact]
    public void Transfer_command_decodes_base64_proto()
    {
        var state = new Spotify.Player.Proto.Transfer.TransferState
        {
            Options = new Spotify.Player.Proto.ContextPlayerOptions { ShufflingContext = true },
        };
        var data = Convert.ToBase64String(state.ToByteArray());

        var command = ParseCommand($$"""
            {"endpoint":"transfer","data":"{{data}}","options":{"restore_paused":"pause","retain_session":"true"},
             "from_device_identifier":"phone",{{Logging}}}
            """);

        var transfer = Assert.IsType<Command.Transfer>(command).Value;
        Assert.Equal(state, transfer.Data);
        Assert.Equal("pause", transfer.Options.RestorePaused);
        Assert.Null(transfer.Options.RestoreTrack);
        Assert.Equal("phone", transfer.FromDeviceIdentifier);

        // TransferOptions serializes without the null fields (upstream skip_serializing_if)
        Assert.Equal("""{"restore_paused":"pause","retain_session":"true"}""", JsonSerializer.Serialize(transfer.Options));

        // data defaults to None when absent
        var withoutData = ParseCommand($$"""{"endpoint":"transfer","options":{},"from_device_identifier":"x",{{Logging}}}""");
        Assert.Null(Assert.IsType<Command.Transfer>(withoutData).Value.Data);
    }

    [Fact]
    public void Simple_commands_decode()
    {
        Assert.IsType<Command.Pause>(ParseCommand($$"""{"endpoint":"pause","options":{},{{Logging}}}"""));
        Assert.IsType<Command.Resume>(ParseCommand($$"""{"endpoint":"resume",{{Logging}}}"""));
        Assert.IsType<Command.SkipPrev>(ParseCommand($$"""{"endpoint":"skip_prev",{{Logging}}}"""));

        var seek = Assert.IsType<Command.SeekTo>(ParseCommand($$"""{"endpoint":"seek_to","value":1000,"position":900,{{Logging}}}""")).Value;
        Assert.Equal(1000u, seek.Value);
        Assert.Equal(900u, seek.Position);

        Assert.True(Assert.IsType<Command.SetShufflingContext>(ParseCommand($$"""{"endpoint":"set_shuffling_context","value":true,{{Logging}}}""")).Value.Value);
        Assert.False(Assert.IsType<Command.SetRepeatingTrack>(ParseCommand($$"""{"endpoint":"set_repeating_track","value":false,{{Logging}}}""")).Value.Value);
        Assert.IsType<Command.SetRepeatingContext>(ParseCommand($$"""{"endpoint":"set_repeating_context","value":true,{{Logging}}}"""));

        var skipNext = Assert.IsType<Command.SkipNext>(ParseCommand($$"""{"endpoint":"skip_next","track":{"uri":"spotify:track:1","uid":"u"},{{Logging}}}""")).Value;
        Assert.Equal("spotify:track:1", skipNext.Track!.Uri);
        Assert.Null(Assert.IsType<Command.SkipNext>(ParseCommand($$"""{"endpoint":"skip_next",{{Logging}}}""")).Value.Track);

        var add = Assert.IsType<Command.AddToQueue>(ParseCommand($$"""{"endpoint":"add_to_queue","track":{"uri":"spotify:track:2"},{{Logging}}}""")).Value;
        Assert.Equal("spotify:track:2", add.Track.Uri);

        var options = Assert.IsType<Command.SetOptions>(ParseCommand($$"""
            {"endpoint":"set_options","repeating_track":true,
             "options":{"only_for_local_device":true,"override_restrictions":false,"system_initiated":true},{{Logging}}}
            """)).Value;
        Assert.True(options.RepeatingTrack);
        Assert.Null(options.ShufflingContext);
        Assert.True(options.Options!.OnlyForLocalDevice);

        var update = Assert.IsType<Command.UpdateContext>(ParseCommand("""{"endpoint":"update_context","context":{"uri":"spotify:album:1"},"session_id":"s"}""")).Value;
        Assert.Equal("spotify:album:1", update.Context.Uri);
        Assert.Equal("s", update.SessionId);
    }

    [Fact]
    public void Set_queue_skips_unparsable_tracks()
    {
        var queue = Assert.IsType<Command.SetQueue>(ParseCommand($$"""
            {"endpoint":"set_queue",
             "next_tracks":[{"uri":"spotify:track:a"},{"uri":42},{"uri":"spotify:track:b","provider":"queue"}],
             "prev_tracks":[],
             "queue_revision":"123",{{Logging}}}
            """)).Value;

        Assert.Equal(new[] { "spotify:track:a", "spotify:track:b" }, queue.NextTracks.Select(t => t.Uri));
        Assert.Equal("queue", queue.NextTracks[1].Provider);
        Assert.Empty(queue.PrevTracks);
        Assert.Equal("123", queue.QueueRevision);
    }

    [Fact]
    public void Unknown_and_malformed_commands_fall_back_to_unknown()
    {
        var unknown = Assert.IsType<Command.Unknown>(ParseCommand("""{"endpoint":"set_volume","volume":5}"""));
        Assert.Equal(5, unknown.Value.GetProperty("volume").GetInt32());
        Assert.Equal("endpoint: unknown set_volume", unknown.ToDisplayString());

        // known endpoint, but a required field is missing: serde falls through to the untagged variant
        var missing = ParseCommand("""{"endpoint":"pause"}""");
        Assert.Equal("endpoint: unknown pause", Assert.IsType<Command.Unknown>(missing).ToDisplayString());

        // known endpoint, wrong field type
        Assert.IsType<Command.Unknown>(ParseCommand($$"""{"endpoint":"seek_to","value":"x","position":1,{{Logging}}}"""));

        // a non-optional string given as null
        Assert.IsType<Command.Unknown>(ParseCommand($$"""{"endpoint":"transfer","options":{},"from_device_identifier":null,{{Logging}}}"""));

        // json_proto that does not parse
        Assert.IsType<Command.Unknown>(ParseCommand($$"""{"endpoint":"add_to_queue","track":{"uri":42},{{Logging}}}"""));

        // no endpoint at all
        Assert.Equal("endpoint: unknown ???", ParseCommand("""{"foo":1}""").ToDisplayString());
        Assert.Equal("endpoint: unknown ???", ParseCommand("null").ToDisplayString());
    }

    [Fact]
    public void Request_payload_errors_are_classified()
    {
        var notBase64 = """{"type":"request","message_ident":"hm://x/y","key":"k","payload":{"compressed":"!!"}}""";
        var request = Assert.IsType<MessageOrRequest.Request>(MessageOrRequest.Parse(notBase64)).Value;
        var e = Assert.Throws<ProtocolException>(() => request.HandlePayload());
        Assert.Equal(ProtocolException.Reason.Base64, e.Cause);
        Assert.Equal(ErrorKind.FailedPrecondition, e.Kind);

        var notGzip = """{"type":"request","message_ident":"hm://x/y","key":"k","headers":{"Transfer-Encoding":"gzip"},"payload":{"compressed":"""
                      + $"\"{Convert.ToBase64String("plain"u8.ToArray())}\"}}}}";
        request = Assert.IsType<MessageOrRequest.Request>(MessageOrRequest.Parse(notGzip)).Value;
        Assert.Equal(ProtocolException.Reason.GZip, Assert.Throws<ProtocolException>(() => request.HandlePayload()).Cause);

        var badRequest = RequestEnvelope("""{"message_id":-1,"sent_by_device_id":"x","command":{}}""");
        request = Assert.IsType<MessageOrRequest.Request>(MessageOrRequest.Parse(badRequest)).Value;
        Assert.Equal(ProtocolException.Reason.Deserialization, Assert.Throws<ProtocolException>(() => request.HandlePayload()).Cause);
    }

    [Fact]
    public void Envelope_type_tag_is_required()
    {
        Assert.Throws<JsonException>(() => MessageOrRequest.Parse("""{"type":"pong"}"""));
        Assert.Throws<JsonException>(() => MessageOrRequest.Parse("""{"uri":"hm://a/b"}"""));
        Assert.Throws<JsonException>(() => MessageOrRequest.Parse("""{"type":"message"}"""));
        Assert.Throws<JsonException>(() => MessageOrRequest.Parse("""{"type":"request","message_ident":"hm://a"}"""));
    }

    private static Message ParseMessage(string json)
    {
        var message = Assert.IsType<MessageOrRequest.Message>(MessageOrRequest.Parse(json)).Value;
        return new Message { Headers = message.Headers, Payload = message.HandlePayload(), Uri = message.Uri };
    }

    [Fact]
    public void Message_with_gzip_protobuf_payload_parses_from_raw()
    {
        var update = new ClusterUpdate { AckId = "ack-1", DevicesThatChanged = { "a", "b" } };
        using var buffer = new MemoryStream();
        using (var gz = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            gz.Write(update.ToByteArray());
        }

        var json = $$"""
            {"type":"message","method":"PUT","uri":"hm://connect-state/v1/cluster",
             "headers":{"Transfer-Encoding":"gzip","content-type":"application/octet-stream"},
             "payloads":["{{Convert.ToBase64String(buffer.ToArray())}}"]}
            """;
        var message = ParseMessage(json);

        Assert.Equal("hm://connect-state/v1/cluster", message.Uri);
        Assert.Equal("gzip", message.Headers["Transfer-Encoding"]);
        Assert.Equal(update, Message.FromRaw<ClusterUpdate>(message));

        // wrong payload kind -> Unavailable
        var e = Assert.ThrowsAny<LibrespotException>(() => Message.TryFromJson<ClusterUpdate>(message));
        Assert.Equal(ErrorKind.Unavailable, e.Kind);
    }

    [Fact]
    public void Message_payload_variants()
    {
        // no payloads -> Empty
        var empty = ParseMessage("""{"type":"message","uri":"hm://pusher/v1/connections/abc","headers":{"Spotify-Connection-Id":"abc"}}""");
        Assert.IsType<PayloadValue.Empty>(empty.Payload);
        Assert.Equal("abc", empty.Headers["Spotify-Connection-Id"]);
        Assert.Equal(ErrorKind.Unavailable, Assert.ThrowsAny<LibrespotException>(() => Message.FromRaw<ClusterUpdate>(empty)).Kind);

        // byte-array payload, no transfer encoding
        var bytes = ParseMessage("""{"type":"message","uri":"hm://a/b","payloads":[[1,2,255]]}""");
        Assert.Equal(new byte[] { 1, 2, 255 }, Assert.IsType<PayloadValue.Raw>(bytes.Payload).Value);

        // an array that is not bytes is JSON
        var jsonArray = ParseMessage("""{"type":"message","uri":"hm://a/b","payloads":[[1,256]]}""");
        Assert.IsType<PayloadValue.Json>(jsonArray.Payload);

        // more than one payload
        var two = Assert.IsType<MessageOrRequest.Message>(MessageOrRequest.Parse("""{"type":"message","uri":"hm://a/b","payloads":["AA==","AA=="]}""")).Value;
        var e = Assert.Throws<ProtocolException>(() => two.HandlePayload());
        Assert.Equal(ProtocolException.Reason.MoreThenOneValue, e.Cause);
        Assert.Equal("payload had more then one value. had 2 values", e.Detail);
    }

    [Fact]
    public void Json_payload_parses_with_fallback()
    {
        var ok = ParseMessage("""{"type":"message","uri":"social-connect/v2/session_update","payloads":[{"uri":"spotify:track:1","provider":"context","whatever":true}]}""");
        Assert.IsType<PayloadValue.Json>(ok.Payload);
        var inner = Assert.IsType<FallbackWrapper<ProvidedTrack>.Inner>(Message.TryFromJson<ProvidedTrack>(ok));
        Assert.Equal("spotify:track:1", inner.Value.Uri);

        var bad = ParseMessage("""{"type":"message","uri":"social-connect/v2/session_update","payloads":[{"uri":42,"reason":"WIFI_BROADCAST_CHANGED"}]}""");
        var fallback = Assert.IsType<FallbackWrapper<ProvidedTrack>.Fallback>(Message.TryFromJson<ProvidedTrack>(bad));
        Assert.Equal("WIFI_BROADCAST_CHANGED", fallback.Value.GetProperty("reason").GetString());

        // FromRaw on a JSON payload -> Unavailable
        Assert.Equal(ErrorKind.Unavailable, Assert.ThrowsAny<LibrespotException>(() => Message.FromRaw<ProvidedTrack>(ok)).Kind);
    }

    [Fact]
    public void FromRaw_parse_failure_is_failed_precondition()
    {
        var message = new Message { Uri = "hm://a/b", Payload = new PayloadValue.Raw([0xFF, 0xFF, 0xFF]) };
        Assert.Equal(ErrorKind.FailedPrecondition, Assert.ThrowsAny<LibrespotException>(() => Message.FromRaw<ClusterUpdate>(message)).Kind);

        var invalidJson = new Message { Uri = "hm://a/b", Payload = new PayloadValue.Json("{not json") };
        Assert.Equal(ErrorKind.FailedPrecondition, Assert.ThrowsAny<LibrespotException>(() => Message.TryFromJson<ClusterUpdate>(invalidJson)).Kind);
    }
}
