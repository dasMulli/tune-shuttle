using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Librespot.Core;
using Spotify.Connectstate;
using Xunit;

namespace Librespot.Tests.Core;

// Upstream core/src/deserialize_with.rs has no tests.
public class DeserializeWithTests
{
    private sealed record Holder
    {
        [JsonPropertyName("b64")]
        [JsonConverter(typeof(Base64ProtoConverter<ProvidedTrack>))]
        public ProvidedTrack? B64 { get; init; }

        [JsonPropertyName("json")]
        [JsonConverter(typeof(JsonProtoConverter<ProvidedTrack>))]
        public ProvidedTrack Json { get; init; } = new();

        [JsonPropertyName("opt")]
        [JsonConverter(typeof(OptionJsonProtoConverter<ProvidedTrack>))]
        public ProvidedTrack? Opt { get; init; }

        [JsonPropertyName("vec")]
        [JsonConverter(typeof(VecJsonProtoConverter<ProvidedTrack>))]
        public IReadOnlyList<ProvidedTrack> Vec { get; init; } = [];

        [JsonPropertyName("flag")]
        [JsonConverter(typeof(BoolFromStringConverter))]
        public bool Flag { get; init; }
    }

    [Fact]
    public void Converters_decode_upstream_shapes()
    {
        var track = new ProvidedTrack { Uri = "spotify:track:x", Uid = "u" };
        var json = $$"""
            {"b64":"{{Convert.ToBase64String(track.ToByteArray())}}",
             "json":{"uri":"spotify:track:x","uid":"u","ignored":[1,2]},
             "opt":{"uri":"spotify:track:y"},
             "vec":[{"uri":"a"},"not an object",{"uri":"b"}],
             "flag":"true"}
            """;

        var holder = JsonSerializer.Deserialize<Holder>(json)!;
        Assert.Equal(track, holder.B64);
        Assert.Equal(track, holder.Json);
        Assert.Equal("spotify:track:y", holder.Opt!.Uri);
        Assert.Equal(new[] { "a", "b" }, holder.Vec.Select(t => t.Uri));
        Assert.True(holder.Flag);
    }

    [Fact]
    public void Missing_optional_values_stay_null()
    {
        var holder = JsonSerializer.Deserialize<Holder>("""{"json":{}}""")!;
        Assert.Null(holder.B64);
        Assert.Null(holder.Opt);
        Assert.Empty(holder.Vec);
    }

    [Theory]
    [InlineData("""{"b64":"%%%"}""")]
    [InlineData("""{"b64":null}""")]
    [InlineData("""{"b64":"/w=="}""")]
    [InlineData("""{"json":{"uri":5}}""")]
    [InlineData("""{"json":null}""")]
    [InlineData("""{"opt":null}""")]
    [InlineData("""{"vec":{"uri":"a"}}""")]
    [InlineData("""{"flag":"yes"}""")]
    [InlineData("""{"flag":true}""")]
    public void Invalid_values_are_rejected(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Holder>(json));
    }

    [Fact]
    public void ParseValueToMsg_ignores_unknown_fields()
    {
        using var doc = JsonDocument.Parse("""{"uri":"spotify:track:z","unknown":{"deep":true}}""");
        Assert.Equal("spotify:track:z", DeserializeWith.ParseValueToMsg<ProvidedTrack>(doc.RootElement).Uri);
    }
}
