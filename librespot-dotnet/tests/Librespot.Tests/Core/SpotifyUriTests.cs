// Port of the #[cfg(test)] module in core/src/spotify_uri.rs.
using Librespot.Core;
using Xunit;

namespace Librespot.Tests.Core;

public class SpotifyUriTests
{
    private static readonly SpotifyId Id1 = new(UInt128.Parse("238762092608182713602505436543891614649"));
    private static readonly SpotifyId Id2 = new(UInt128.Parse("204841891221366092811751085145916697048"));

    private static readonly (SpotifyUri Parsed, string Uri, string Base62)[] ConvValid =
    [
        (new SpotifyUri.Track(Id1), "spotify:track:5sWHDYs0csV6RS48xBl0tH", "5sWHDYs0csV6RS48xBl0tH"),
        (new SpotifyUri.Track(Id2), "spotify:track:4GNcXTGWmnZ3ySrqvol3o4", "4GNcXTGWmnZ3ySrqvol3o4"),
        (new SpotifyUri.Episode(Id2), "spotify:episode:4GNcXTGWmnZ3ySrqvol3o4", "4GNcXTGWmnZ3ySrqvol3o4"),
        (new SpotifyUri.Show(Id2), "spotify:show:4GNcXTGWmnZ3ySrqvol3o4", "4GNcXTGWmnZ3ySrqvol3o4"),
    ];

    private static readonly string[] ConvInvalidUris =
    [
        "spotify:track:5sWHDYs0Bl0tH", // Invalid ID in the URI.
        "spotify:arbitrarywhatever5sWHDYs0csV6RS48xBl0tH", // Missing colon between ID and type.
        "spotify:track:aRS48xBl0tH", // Uri too short
        "cleary invalid uri",
    ];

    [Fact]
    public void ToId()
    {
        foreach (var (parsed, _, base62) in ConvValid)
        {
            Assert.Equal(base62, parsed.ToId());
        }
    }

    [Theory]
    [InlineData("album", "album")]
    [InlineData("artist", "artist")]
    [InlineData("episode", "episode")]
    [InlineData("playlist", "playlist")]
    [InlineData("show", "show")]
    [InlineData("track", "track")]
    public void ItemType(string kind, string expected)
    {
        SpotifyUri uri = kind switch
        {
            "album" => new SpotifyUri.Album(default),
            "artist" => new SpotifyUri.Artist(default),
            "episode" => new SpotifyUri.Episode(default),
            "playlist" => new SpotifyUri.Playlist(null, default),
            "show" => new SpotifyUri.Show(default),
            _ => new SpotifyUri.Track(default),
        };
        Assert.Equal(expected, uri.ItemType);
    }

    [Fact]
    public void FromUri()
    {
        foreach (var (parsed, uri, _) in ConvValid)
        {
            Assert.Equal(parsed, SpotifyUri.FromUri(uri));
        }

        foreach (var uri in ConvInvalidUris)
        {
            Assert.ThrowsAny<LibrespotException>(() =>
            {
                try
                {
                    SpotifyUri.FromUri(uri);
                }
                catch (LibrespotException e)
                {
                    Assert.Equal(ErrorKind.InvalidArgument, e.Kind);
                    throw;
                }
            });
        }
    }

    [Fact]
    public void FromInvalidTypeUri()
    {
        var actual = SpotifyUri.FromUri("spotify:arbitrarywhatever:5sWHDYs0csV6RS48xBl0tH");
        Assert.Equal(new SpotifyUri.Unknown("arbitrarywhatever", "5sWHDYs0csV6RS48xBl0tH"), actual);
        Assert.Equal("spotify:unknown:5sWHDYs0csV6RS48xBl0tH", actual.ToUri());
    }

    [Fact]
    public void FromLocalUri()
    {
        var actual = SpotifyUri.FromUri("spotify:local:Pink Floyd:The Wall:Comfortably Numb:382");
        Assert.Equal(new SpotifyUri.Local("Pink Floyd", "The Wall", "Comfortably Numb", TimeSpan.FromSeconds(382)), actual);
        Assert.True(actual.IsPlayable);
        Assert.Equal("spotify:local:Pink Floyd:The Wall:Comfortably Numb:382", actual.ToUri());
    }

    [Fact]
    public void FromLocalUriMissingFields()
    {
        Assert.Throws<SpotifyUriException>(() => SpotifyUri.FromUri("spotify:local:Pink Floyd:The Wall:Comfortably Numb"));
        Assert.Throws<SpotifyUriException>(() => SpotifyUri.FromUri("spotify:local:Pink Floyd:The Wall:Comfortably Numb:notanumber"));
    }

    [Fact]
    public void FromNamedUri()
    {
        var actual = SpotifyUri.FromUri("spotify:user:spotify:playlist:37i9dQZF1DWSw8liJZcPOI");
        Assert.Equal(new SpotifyUri.Playlist("spotify", SpotifyId.FromBase62("37i9dQZF1DWSw8liJZcPOI")), actual);
    }

    [Fact]
    public void ToUri()
    {
        foreach (var (parsed, uri, _) in ConvValid)
        {
            Assert.Equal(uri, parsed.ToUri());
            Assert.Equal(uri, parsed.ToString());
        }
    }

    [Fact]
    public void ToNamedUri()
    {
        var uri = new SpotifyUri.Playlist("spotify", SpotifyId.FromBase62("37i9dQZF1DWSw8liJZcPOI"));
        Assert.Equal("spotify:user:spotify:playlist:37i9dQZF1DWSw8liJZcPOI", uri.ToUri());
    }

    [Fact]
    public void SpotifyId_from_uri()
    {
        Assert.Equal(Id1, SpotifyId.From(new SpotifyUri.Track(Id1)));
        Assert.Throws<SpotifyIdException>(() => SpotifyId.From(new SpotifyUri.Unknown("x", "y")));
    }
}
