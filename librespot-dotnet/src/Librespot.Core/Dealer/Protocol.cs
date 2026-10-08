// Port of core/src/dealer/protocol.rs (contract: the public message types). The private wire envelopes
// (WebsocketMessage / WebsocketRequest / MessageOrRequest / Payload, `handle_payload`, gzip transfer encoding,
// ProtocolError) are added here by package `core-dealer`.
//
// Namespace note: everything from protocol.rs and protocol/request.rs lives in Librespot.Core.Dealer, NOT in
// Librespot.Core.Dealer.Protocol. A sub-namespace named `Protocol` would shadow the identifier `Protocol`
// (Librespot.Protocol.*) inside every Librespot.Core.Dealer file.
// TODO(port: core-dealer)
using System.Text.Json;
using Google.Protobuf;

namespace Librespot.Core.Dealer;

/// <summary>Upstream <c>MessagePayloadValue</c>: one element of the websocket message's <c>payloads</c> array.</summary>
public abstract record MessagePayloadValue
{
    private MessagePayloadValue()
    {
    }

    /// <summary>A base64 string (possibly gzip-compressed, see the <c>Transfer-Encoding</c> header).</summary>
    public sealed record String(string Value) : MessagePayloadValue;

    public sealed record Bytes(byte[] Value) : MessagePayloadValue;

    public sealed record Json(JsonElement Value) : MessagePayloadValue;
}

/// <summary>Upstream <c>PayloadValue</c>: the decoded payload of a <see cref="Message"/>.</summary>
public abstract record PayloadValue
{
    private PayloadValue()
    {
    }

    public sealed record Empty() : PayloadValue;

    /// <summary>Decoded (and decompressed) bytes, normally a protobuf.</summary>
    public sealed record Raw(byte[] Value) : PayloadValue;

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

    public sealed record Inner(T Value) : FallbackWrapper<T>;

    public sealed record Fallback(JsonElement Value) : FallbackWrapper<T>;
}

/// <summary>A dealer message (upstream <c>dealer::protocol::Message</c>).</summary>
public sealed record Message
{
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    public PayloadValue Payload { get; init; } = new PayloadValue.Empty();

    public string Uri { get; init; } = "";

    /// <summary>
    /// Upstream <c>Message::try_from_json::&lt;M&gt;(message)</c>: parses a <see cref="PayloadValue.Json"/> payload as
    /// protobuf-JSON (<c>JsonParser</c> ignoring unknown fields); on a parse failure returns
    /// <see cref="FallbackWrapper{T}.Fallback"/>. Throws (kind Unavailable) for other payload kinds.
    /// </summary>
    public static FallbackWrapper<M> TryFromJson<M>(Message message) where M : IMessage<M>, new() =>
        throw new NotImplementedException("TODO(port: core-dealer)");

    /// <summary>
    /// Upstream <c>Message::from_raw::&lt;M&gt;(message)</c>: parses a <see cref="PayloadValue.Raw"/> payload with
    /// <c>M.Parser.ParseFrom</c> (parse failure: kind FailedPrecondition). Throws (kind Unavailable) for other payload kinds.
    /// </summary>
    public static M FromRaw<M>(Message message) where M : IMessage<M>, new() =>
        throw new NotImplementedException("TODO(port: core-dealer)");
}
