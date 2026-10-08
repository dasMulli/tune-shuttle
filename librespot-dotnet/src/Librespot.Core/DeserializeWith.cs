// Port of core/src/deserialize_with.rs.
//
// Upstream defines serde `deserialize_with` functions; System.Text.Json expresses the same thing as
// JsonConverter<T> classes that are attached to properties with [JsonConverter(typeof(...))]
// (see Dealer/Protocol/Request.cs). Upstream's `boxed` helper only exists to put a large enum payload on the
// heap; C# records are reference types already, so it has no counterpart.
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;

namespace Librespot.Core;

/// <summary>
/// Shared helpers of the <c>deserialize_with</c> converters: protobuf-JSON parsing that ignores unknown fields
/// (upstream <c>IGNORE_UNKNOWN</c> + <c>parse_value_to_msg</c>).
/// </summary>
public static class DeserializeWith
{
    /// <summary>Upstream <c>IGNORE_UNKNOWN</c>: protobuf-JSON parse options with <c>ignore_unknown_fields: true</c>.</summary>
    public static JsonParser IgnoreUnknownParser { get; } =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    /// <summary>
    /// Upstream <c>parse_value_to_msg</c>: parses a JSON value as the protobuf-JSON representation of
    /// <typeparamref name="T"/>, ignoring unknown fields. Throws <see cref="InvalidProtocolBufferException"/>
    /// (or <see cref="InvalidJsonException"/>) when the value does not describe a <typeparamref name="T"/>.
    /// </summary>
    public static T ParseValueToMsg<T>(JsonElement value) where T : IMessage<T>, new() =>
        IgnoreUnknownParser.Parse<T>(value.GetRawText());

    /// <summary>Like <see cref="ParseValueToMsg{T}"/>, but reports failure as <c>false</c> instead of throwing.</summary>
    internal static bool TryParseValueToMsg<T>(JsonElement value, out T message) where T : IMessage<T>, new()
    {
        try
        {
            message = ParseValueToMsg<T>(value);
            return true;
        }
        catch (Exception e) when (e is InvalidProtocolBufferException or InvalidJsonException or FormatException)
        {
            message = default!;
            return false;
        }
    }

    /// <summary>Reads the current JSON value of <paramref name="reader"/> (upstream <c>Value::deserialize</c>).</summary>
    internal static JsonElement ReadValue(ref Utf8JsonReader reader)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.Clone();
    }

    /// <summary>Parses <paramref name="value"/> as <typeparamref name="T"/>, mapping parse errors to <see cref="JsonException"/> (upstream <c>Error::custom</c>).</summary>
    internal static T ParseOrJsonError<T>(JsonElement value) where T : IMessage<T>, new()
    {
        try
        {
            return ParseValueToMsg<T>(value);
        }
        catch (Exception e) when (e is InvalidProtocolBufferException or InvalidJsonException or FormatException)
        {
            throw new JsonException(e.Message, e);
        }
    }
}

/// <summary>
/// Upstream <c>base64_proto</c>: the value is a base64 string holding a binary protobuf. Upstream returns
/// <c>Option&lt;T&gt;</c> but only ever <c>Some</c>; a missing property stays <c>null</c> (upstream
/// <c>#[serde(default)]</c>), a JSON <c>null</c> is an error like upstream's <c>String::deserialize</c>.
/// </summary>
public sealed class Base64ProtoConverter<T> : JsonConverter<T> where T : IMessage<T>, new()
{
    /// <inheritdoc />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"invalid type: {reader.TokenType}, expected a string");
        }

        var v = reader.GetString()!;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(v);
        }
        catch (FormatException e)
        {
            throw new JsonException(e.Message, e);
        }

        try
        {
            var message = new T();
            message.MergeFrom(bytes);
            return message;
        }
        catch (InvalidProtocolBufferException e)
        {
            throw new JsonException(e.Message, e);
        }
    }

    /// <summary>Not supported: upstream only deserializes.</summary>
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        throw new NotSupportedException("base64_proto is deserialize-only (as upstream)");
}

/// <summary>Upstream <c>json_proto</c>: the value is the protobuf-JSON representation of <typeparamref name="T"/>.</summary>
public sealed class JsonProtoConverter<T> : JsonConverter<T> where T : IMessage<T>, new()
{
    /// <inheritdoc />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DeserializeWith.ParseOrJsonError<T>(DeserializeWith.ReadValue(ref reader));

    /// <summary>Not supported: upstream only deserializes.</summary>
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        throw new NotSupportedException("json_proto is deserialize-only (as upstream)");
}

/// <summary>
/// Upstream <c>option_json_proto</c>: like <see cref="JsonProtoConverter{T}"/> for an optional property. A missing
/// property stays <c>null</c> (upstream <c>#[serde(default)]</c>); a present value, including JSON <c>null</c>,
/// must parse, as upstream feeds every present value to the protobuf-JSON parser.
/// </summary>
public sealed class OptionJsonProtoConverter<T> : JsonConverter<T> where T : IMessage<T>, new()
{
    /// <inheritdoc />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DeserializeWith.ParseOrJsonError<T>(DeserializeWith.ReadValue(ref reader));

    /// <summary>Not supported: upstream only deserializes.</summary>
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        throw new NotSupportedException("option_json_proto is deserialize-only (as upstream)");
}

/// <summary>
/// Upstream <c>vec_json_proto</c>: the value must be an array; every element that parses as protobuf-JSON of
/// <typeparamref name="T"/> is kept, elements that do not parse are silently skipped (upstream <c>flat_map</c>).
/// </summary>
public sealed class VecJsonProtoConverter<T> : JsonConverter<IReadOnlyList<T>> where T : IMessage<T>, new()
{
    /// <inheritdoc />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override IReadOnlyList<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var v = DeserializeWith.ReadValue(ref reader);
        if (v.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("the value wasn't an array");
        }

        var res = new List<T>(v.GetArrayLength());
        foreach (var element in v.EnumerateArray())
        {
            if (DeserializeWith.TryParseValueToMsg<T>(element, out var message))
            {
                res.Add(message);
            }
        }

        return res;
    }

    /// <summary>Not supported: upstream only deserializes.</summary>
    public override void Write(Utf8JsonWriter writer, IReadOnlyList<T> value, JsonSerializerOptions options) =>
        throw new NotSupportedException("vec_json_proto is deserialize-only (as upstream)");
}

/// <summary>Upstream <c>bool_from_string</c>: accepts exactly the strings <c>"true"</c> and <c>"false"</c>.</summary>
public sealed class BoolFromStringConverter : JsonConverter<bool>
{
    /// <inheritdoc />
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"invalid type: {reader.TokenType}, expected a string");
        }

        return reader.GetString() switch
        {
            "true" => true,
            "false" => false,
            var other => throw new JsonException($"invalid value: string \"{other}\", expected true or false"),
        };
    }

    /// <summary>Not supported: upstream only deserializes.</summary>
    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
        throw new NotSupportedException("bool_from_string is deserialize-only (as upstream)");
}
