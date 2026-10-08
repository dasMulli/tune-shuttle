// Port of metadata/src/lyrics.rs.
using System.Text.Json;
using System.Text.Json.Serialization;
using Librespot.Core;

namespace Librespot.Metadata;

/// <summary>Lyrics of a track as returned by the color-lyrics endpoint (upstream <c>Lyrics</c>, serde camelCase).</summary>
public sealed record Lyrics
{
    /// <summary>Display colors.</summary>
    [JsonPropertyName("colors")]
    public required Colors Colors { get; init; }

    /// <summary>Whether a vocal-removed version exists.</summary>
    [JsonPropertyName("hasVocalRemoval")]
    public required bool HasVocalRemoval { get; init; }

    /// <summary>The lyrics proper.</summary>
    [JsonPropertyName("lyrics")]
    public required LyricsInner LyricsInner { get; init; }

    /// <summary>Fetches the lyrics of a track (upstream <c>Lyrics::get</c>).</summary>
    /// <exception cref="LibrespotException">The request failed or the JSON could not be parsed.</exception>
    public static async Task<Lyrics> GetAsync(Session session, SpotifyId id, CancellationToken cancellationToken = default)
    {
        var spclient = session.SpClient;
        var lyrics = await spclient.GetLyricsAsync(id, cancellationToken).ConfigureAwait(false);
        return From(lyrics);
    }

    /// <summary>Fetches the lyrics of a track for a given cover image (upstream <c>Lyrics::get_for_image</c>).</summary>
    /// <exception cref="LibrespotException">The request failed or the JSON could not be parsed.</exception>
    public static async Task<Lyrics> GetForImageAsync(Session session, SpotifyId id, FileId imageId, CancellationToken cancellationToken = default)
    {
        var spclient = session.SpClient;
        var lyrics = await spclient.GetLyricsForImageAsync(id, imageId, cancellationToken).ConfigureAwait(false);
        return From(lyrics);
    }

    /// <summary>Upstream <c>TryFrom&lt;&amp;Bytes&gt;</c>: deserializes the JSON body.</summary>
    /// <exception cref="LibrespotException">The JSON is malformed or misses a field (<see cref="ErrorKind.FailedPrecondition"/>).</exception>
    public static Lyrics From(ReadOnlySpan<byte> lyrics)
    {
        try
        {
            return JsonSerializer.Deserialize(lyrics, LyricsJsonContext.Default.Lyrics)
                ?? throw new JsonException("invalid type: null, expected struct Lyrics");
        }
        catch (JsonException e)
        {
            throw LibrespotException.From(e);
        }
    }
}

/// <summary>Lyrics display colors (upstream <c>Colors</c>).</summary>
public sealed record Colors
{
    /// <summary>Background color (ARGB as a signed integer).</summary>
    [JsonPropertyName("background")]
    public required int Background { get; init; }

    /// <summary>Highlighted text color.</summary>
    [JsonPropertyName("highlightText")]
    public required int HighlightText { get; init; }

    /// <summary>Text color.</summary>
    [JsonPropertyName("text")]
    public required int Text { get; init; }
}

/// <summary>The lyrics lines and their provider (upstream <c>LyricsInner</c>).</summary>
/// <remarks>Upstream leaves the <c>alternatives</c> array unmapped (its meaning is unknown).</remarks>
public sealed record LyricsInner
{
    /// <summary>Whether a dense typeface should be used.</summary>
    [JsonPropertyName("isDenseTypeface")]
    public required bool IsDenseTypeface { get; init; }

    /// <summary>Whether the language is written right to left.</summary>
    [JsonPropertyName("isRtlLanguage")]
    public required bool IsRtlLanguage { get; init; }

    /// <summary>Language code.</summary>
    [JsonPropertyName("language")]
    public required string Language { get; init; }

    /// <summary>The lines.</summary>
    [JsonPropertyName("lines")]
    public required IReadOnlyList<Line> Lines { get; init; }

    /// <summary>Provider id.</summary>
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    /// <summary>Provider display name.</summary>
    [JsonPropertyName("providerDisplayName")]
    public required string ProviderDisplayName { get; init; }

    /// <summary>The provider's lyrics id.</summary>
    [JsonPropertyName("providerLyricsId")]
    public required string ProviderLyricsId { get; init; }

    /// <summary>URI of the synced lyrics.</summary>
    [JsonPropertyName("syncLyricsUri")]
    public required string SyncLyricsUri { get; init; }

    /// <summary>Whether the lines carry timing.</summary>
    [JsonPropertyName("syncType")]
    public required SyncType SyncType { get; init; }
}

/// <summary>Lyrics synchronisation type (upstream <c>SyncType</c>, serde SCREAMING_SNAKE_CASE).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SyncType>))]
public enum SyncType
{
    /// <summary>No timing information.</summary>
    [JsonStringEnumMemberName("UNSYNCED")]
    Unsynced,

    /// <summary>Per-line timing.</summary>
    [JsonStringEnumMemberName("LINE_SYNCED")]
    LineSynced,
}

/// <summary>One lyrics line (upstream <c>Line</c>).</summary>
/// <remarks>Upstream leaves the <c>syllables</c> array unmapped.</remarks>
public sealed record Line
{
    /// <summary>Start time in milliseconds, as a string.</summary>
    [JsonPropertyName("startTimeMs")]
    public required string StartTimeMs { get; init; }

    /// <summary>End time in milliseconds, as a string.</summary>
    [JsonPropertyName("endTimeMs")]
    public required string EndTimeMs { get; init; }

    /// <summary>The words of the line.</summary>
    [JsonPropertyName("words")]
    public required string Words { get; init; }
}

/// <summary>Source-generated JSON metadata for <see cref="Lyrics"/>.</summary>
/// <remarks>
/// <c>RespectNullableAnnotations</c> plus <c>required</c> members reproduce serde's strictness: a missing field or a
/// <c>null</c> where upstream has no <c>Option</c> is an error. Unknown fields are ignored, as with serde's default.
/// </remarks>
[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.Strict, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(Lyrics))]
internal sealed partial class LyricsJsonContext : JsonSerializerContext;
