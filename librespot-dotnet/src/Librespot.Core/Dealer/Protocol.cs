// Port of core/src/dealer/protocol.rs.
//
// Public: the message types handed to subscribers (Message, PayloadValue, MessagePayloadValue, FallbackWrapper).
// Internal (private upstream): the websocket envelopes WebsocketMessage / WebsocketRequest / MessageOrRequest /
// Payload, `handle_payload`, the gzip transfer encoding and ProtocolError.
//
// Namespace note: everything from protocol.rs and protocol/request.rs lives in Librespot.Core.Dealer, NOT in
// Librespot.Core.Dealer.Protocol. A sub-namespace named `Protocol` would shadow the identifier `Protocol`
// (Librespot.Protocol.*) inside every Librespot.Core.Dealer file.
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Librespot.Core.Dealer;

/// <summary>Upstream <c>ProtocolError</c> (private upstream).</summary>
internal sealed class ProtocolException : LibrespotException
{
    public enum Reason
    {
        Base64,
        GZip,
        Deserialization,
        MoreThenOneValue,
        UnexpectedData,
        Empty,
    }

    public Reason Cause { get; }

    private ProtocolException(Reason cause, string detail, Exception? inner = null)
        // upstream: UnexpectedData -> Error::unavailable, everything else -> Error::failed_precondition
        : base(cause == Reason.UnexpectedData ? ErrorKind.Unavailable : ErrorKind.FailedPrecondition, detail, inner)
    {
        Cause = cause;
    }

    public static ProtocolException Base64(Exception e) => new(Reason.Base64, $"base64 decoding failed: {e.Message}", e);

    public static ProtocolException GZip(Exception e) => new(Reason.GZip, $"gzip decoding failed: {e.Message}", e);

    public static ProtocolException Deserialization(Exception e) =>
        new(Reason.Deserialization, $"deserialization failed: {e.Message}", e);

    public static ProtocolException MoreThenOneValue(int count) =>
        new(Reason.MoreThenOneValue, $"payload had more then one value. had {count} values");

    public static ProtocolException UnexpectedData(PayloadValue value) =>
        new(Reason.UnexpectedData, $"received unexpected data {value}");

    public static ProtocolException Empty() => new(Reason.Empty, "payload was empty");
}

/// <summary>Upstream <c>MessagePayloadValue</c>: one element of the websocket message's <c>payloads</c> array.</summary>
[JsonConverter(typeof(MessagePayloadValueJsonConverter))]
public abstract record MessagePayloadValue
{
    private MessagePayloadValue()
    {
    }

    /// <summary>A base64 string (possibly gzip-compressed, see the <c>Transfer-Encoding</c> header).</summary>
    public sealed record String(string Value) : MessagePayloadValue;

    /// <summary>A JSON array of bytes.</summary>
    public sealed record Bytes(byte[] Value) : MessagePayloadValue;

    /// <summary>Any other JSON value.</summary>
    public sealed record Json(JsonElement Value) : MessagePayloadValue;
}

/// <summary>
/// Reads <see cref="MessagePayloadValue"/> like upstream's <c>#[serde(untagged)]</c>: the first variant that
/// accepts the value wins (string, then an array of integers 0-255, then any JSON).
/// </summary>
internal sealed class MessagePayloadValueJsonConverter : JsonConverter<MessagePayloadValue>
{
    public override bool HandleNull => true;

    public override MessagePayloadValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = DeserializeWith.ReadValue(ref reader);
        if (value.ValueKind == JsonValueKind.String)
        {
            return new MessagePayloadValue.String(value.GetString()!);
        }

        if (value.ValueKind == JsonValueKind.Array && TryReadBytes(value, out var bytes))
        {
            return new MessagePayloadValue.Bytes(bytes);
        }

        return new MessagePayloadValue.Json(value);
    }

    private static bool TryReadBytes(JsonElement array, out byte[] bytes)
    {
        bytes = new byte[array.GetArrayLength()];
        var i = 0;
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetByte(out bytes[i++]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Not supported: upstream only deserializes.</summary>
    public override void Write(Utf8JsonWriter writer, MessagePayloadValue value, JsonSerializerOptions options) =>
        throw new NotSupportedException("MessagePayloadValue is deserialize-only (as upstream)");
}

/// <summary>Upstream <c>PayloadValue</c>: the decoded payload of a <see cref="Message"/>.</summary>
public abstract record PayloadValue
{
    private PayloadValue()
    {
    }

    /// <summary>The message carried no payload.</summary>
    public sealed record Empty() : PayloadValue;

    /// <summary>Decoded (and decompressed) bytes, normally a protobuf.</summary>
    public sealed record Raw(byte[] Value) : PayloadValue
    {
        /// <inheritdoc />
        public override string ToString() => $"Raw({Value.Length} bytes)";
    }

    /// <summary>The JSON text of a JSON payload.</summary>
    public sealed record Json(string Value) : PayloadValue;
}

/// <summary>
/// Upstream <c>FallbackWrapper&lt;T&gt;</c>: a protobuf parsed from JSON, or, when protobuf JSON parsing fails
/// (e.g. an enum value missing from our .proto files), the raw JSON. Consumed by spirc for
/// <c>social-connect/v2/session_update</c>.
/// </summary>
public abstract record FallbackWrapper<T> where T : IMessage<T>
{
    private FallbackWrapper()
    {
    }

    /// <summary>The JSON parsed as <typeparamref name="T"/>.</summary>
    public sealed record Inner(T Value) : FallbackWrapper<T>;

    /// <summary>The JSON could not be parsed as <typeparamref name="T"/>; the raw value.</summary>
    public sealed record Fallback(JsonElement Value) : FallbackWrapper<T>;
}

/// <summary>A dealer message (upstream <c>dealer::protocol::Message</c>).</summary>
public sealed record Message
{
    /// <summary>The message headers (e.g. <c>Transfer-Encoding</c>, <c>Spotify-Connection-Id</c>).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>The decoded payload.</summary>
    public PayloadValue Payload { get; init; } = new PayloadValue.Empty();

    /// <summary>The uri the message was published on.</summary>
    public string Uri { get; init; } = "";

    /// <summary>
    /// Upstream <c>Message::try_from_json::&lt;M&gt;(message)</c>: parses a <see cref="PayloadValue.Json"/> payload as
    /// protobuf-JSON (<c>JsonParser</c> ignoring unknown fields); on a parse failure returns
    /// <see cref="FallbackWrapper{T}.Fallback"/>. Throws (kind FailedPrecondition) for invalid JSON text and
    /// (kind Unavailable) for other payload kinds.
    /// </summary>
    public static FallbackWrapper<M> TryFromJson<M>(Message message) where M : IMessage<M>, new()
    {
        if (message.Payload is not PayloadValue.Json(var json))
        {
            throw ProtocolException.UnexpectedData(message.Payload);
        }

        JsonElement value;
        try
        {
            using var document = JsonDocument.Parse(json);
            value = document.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw LibrespotException.From(e);
        }

        // upstream: #[serde(untagged)] - Inner (json_proto) first, then Fallback(Value)
        return DeserializeWith.TryParseValueToMsg<M>(value, out var inner)
            ? new FallbackWrapper<M>.Inner(inner)
            : new FallbackWrapper<M>.Fallback(value);
    }

    /// <summary>
    /// Upstream <c>Message::from_raw::&lt;M&gt;(message)</c>: parses a <see cref="PayloadValue.Raw"/> payload with
    /// <c>M.Parser.ParseFrom</c> (parse failure: kind FailedPrecondition). Throws (kind Unavailable) for other payload kinds.
    /// </summary>
    public static M FromRaw<M>(Message message) where M : IMessage<M>, new()
    {
        if (message.Payload is not PayloadValue.Raw(var bytes))
        {
            throw ProtocolException.UnexpectedData(message.Payload);
        }

        try
        {
            var m = new M();
            m.MergeFrom(bytes);
            return m;
        }
        catch (InvalidProtocolBufferException e)
        {
            throw LibrespotException.FailedPrecondition(e);
        }
    }
}

/// <summary>JSON options for the dealer wire format (serde semantics: <c>null</c> only where upstream has <c>Option</c>).</summary>
internal static class DealerJson
{
    public static JsonSerializerOptions Options { get; } = new() { RespectNullableAnnotations = true };
}

/// <summary>Upstream <c>Payload</c> of a websocket request.</summary>
internal sealed record Payload
{
    [JsonPropertyName("compressed")]
    [JsonRequired]
    public string Compressed { get; init; } = "";
}

/// <summary>Upstream <c>WebsocketRequest</c>: the <c>"type": "request"</c> envelope.</summary>
internal sealed record WebsocketRequest
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<WebsocketRequest>();

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; init; } = new();

    [JsonPropertyName("message_ident")]
    [JsonRequired]
    public string MessageIdent { get; init; } = "";

    [JsonPropertyName("key")]
    [JsonRequired]
    public string Key { get; init; } = "";

    [JsonPropertyName("payload")]
    [JsonRequired]
    public Payload Payload { get; init; } = new();

    /// <summary>Upstream <c>handle_payload</c>: base64 → transfer encoding → UTF-8 → <see cref="Request"/>.</summary>
    public Request HandlePayload()
    {
        byte[] payloadBytes;
        try
        {
            payloadBytes = Convert.FromBase64String(Payload.Compressed);
        }
        catch (FormatException e)
        {
            throw ProtocolException.Base64(e);
        }

        var payload = DealerProtocol.HandleTransferEncoding(Headers, payloadBytes);

        string text;
        try
        {
            text = DealerProtocol.StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException e)
        {
            throw LibrespotException.From(e);
        }

        if (Log.IsEnabled(LogLevel.Trace))
        {
            try
            {
                using var json = JsonDocument.Parse(text);
                Log.LogTrace("websocket request: {Json}", JsonSerializer.Serialize(json.RootElement, DealerProtocol.Indented));
            }
            catch (JsonException)
            {
                Log.LogTrace("websocket request: {Payload}", text);
            }
        }

        try
        {
            return JsonSerializer.Deserialize<Request>(text, DealerJson.Options)
                   ?? throw new JsonException("invalid type: null, expected struct Request");
        }
        catch (JsonException e)
        {
            throw ProtocolException.Deserialization(e);
        }
    }
}

/// <summary>Upstream <c>WebsocketMessage</c>: the <c>"type": "message"</c> envelope.</summary>
internal sealed record WebsocketMessage
{
    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; init; } = new();

    [JsonPropertyName("method")]
    public string? Method { get; init; }

    [JsonPropertyName("payloads")]
    public List<MessagePayloadValue> Payloads { get; init; } = [];

    [JsonPropertyName("uri")]
    [JsonRequired]
    public string Uri { get; init; } = "";

    /// <summary>Upstream <c>handle_payload</c>: decodes the single payload element (if any).</summary>
    public PayloadValue HandlePayload()
    {
        if (Payloads.Count == 0)
        {
            return new PayloadValue.Empty();
        }
        else if (Payloads.Count > 1)
        {
            throw ProtocolException.MoreThenOneValue(Payloads.Count);
        }

        var payload = Payloads[^1] ?? throw ProtocolException.Empty();
        byte[] bytes;
        switch (payload)
        {
            case MessagePayloadValue.String(var s):
                try
                {
                    bytes = Convert.FromBase64String(s);
                }
                catch (FormatException e)
                {
                    throw ProtocolException.Base64(e);
                }

                break;
            case MessagePayloadValue.Bytes(var b):
                bytes = b;
                break;
            case MessagePayloadValue.Json(var json):
                return new PayloadValue.Json(json.GetRawText());
            default:
                throw new InvalidOperationException("closed hierarchy");
        }

        return new PayloadValue.Raw(DealerProtocol.HandleTransferEncoding(Headers, bytes));
    }
}

/// <summary>Upstream <c>MessageOrRequest</c>: the websocket text frame, tagged by <c>type</c> (<c>message</c> | <c>request</c>).</summary>
internal abstract record MessageOrRequest
{
    private MessageOrRequest()
    {
    }

    public sealed record Message(WebsocketMessage Value) : MessageOrRequest;

    public sealed record Request(WebsocketRequest Value) : MessageOrRequest;

    /// <summary>Upstream <c>serde_json::from_str::&lt;MessageOrRequest&gt;</c>; throws <see cref="JsonException"/> on failure.</summary>
    public static MessageOrRequest Parse(string text)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"invalid type: {root.ValueKind}, expected internally tagged enum MessageOrRequest");
        }

        if (!root.TryGetProperty("type", out var tag))
        {
            throw new JsonException("missing field `type`");
        }

        if (tag.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"invalid type: {tag.ValueKind}, expected variant identifier");
        }

        return tag.GetString() switch
        {
            "message" => new Message(root.Deserialize<WebsocketMessage>(DealerJson.Options)!),
            "request" => new Request(root.Deserialize<WebsocketRequest>(DealerJson.Options)!),
            var other => throw new JsonException($"unknown variant `{other}`, expected `message` or `request`"),
        };
    }
}

/// <summary>Free functions of upstream <c>protocol.rs</c>.</summary>
internal static class DealerProtocol
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger(typeof(DealerProtocol).FullName!);

    /// <summary>UTF-8 decoder that throws on invalid input (upstream <c>String::from_utf8</c>).</summary>
    public static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Pretty-printing options for trace output (upstream <c>{:#?}</c>).</summary>
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Upstream <c>handle_transfer_encoding</c>: gunzips the data when <c>Transfer-Encoding: gzip</c>.</summary>
    public static byte[] HandleTransferEncoding(IReadOnlyDictionary<string, string> headers, byte[] data)
    {
        headers.TryGetValue("Transfer-Encoding", out var encoding);
        if (encoding is not null)
        {
            Log.LogTrace("message was sent with {Encoding} encoding ", encoding);
        }
        else
        {
            Log.LogTrace("message was sent with no encoding ");
        }

        if (encoding != "gzip")
        {
            return data;
        }

        try
        {
            using var gz = new GZipStream(new MemoryStream(data, writable: false), CompressionMode.Decompress);
            using var bytes = new MemoryStream();
            gz.CopyTo(bytes);
            return bytes.ToArray();
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            throw ProtocolException.GZip(e);
        }
    }
}
