// Tests for playback/src/local_file.rs (upstream has no unit tests for it).
using Librespot.Core;
using Librespot.Playback;
using Xunit;

namespace Librespot.Tests.Playback;

public sealed class LocalFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "librespot-localfile-" + Guid.NewGuid().ToString("N"));

    public LocalFileTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Create_indexes_supported_files_recursively_by_tag_uri()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_root, "a", "b")).FullName;
        var mp3 = Path.Combine(nested, "tone.MP3");
        File.Copy(DecoderTests.AssetPath("tone-2s.mp3"), mp3);

        // Not a supported extension (upstream only indexes mp3/mp4/m4p/flac): ignored.
        File.Copy(DecoderTests.AssetPath("tone-2s.ogg"), Path.Combine(_root, "tone.ogg"));

        // Supported extension but not probeable: skipped with a warning.
        File.WriteAllBytes(Path.Combine(_root, "broken.flac"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(_root, "noext"), [1, 2, 3]);

        var lookup = LocalFileLookup.Create([_root, Path.Combine(_root, "does-not-exist")]);

        Assert.Equal(1, lookup.Count);
        var uri = SpotifyUri.FromUri("spotify:local:Librespot+Tests:Assets:Test+Tone+%26+Co:2");
        Assert.Equal(mp3, lookup.Get(uri));
        Assert.Null(lookup.Get(SpotifyUri.FromUri("spotify:local:Librespot+Tests:Assets:Test+Tone+%26+Co:3")));
        Assert.Null(lookup.Get(SpotifyUri.FromUri("spotify:track:4GNcXTGWmnZ3ySrqvol3o4")));
    }

    [Fact]
    public void Empty_lookup_finds_nothing()
    {
        var lookup = new LocalFileLookup();
        Assert.Null(lookup.Get(new SpotifyUri.Local("a", "b", "c", TimeSpan.FromSeconds(1))));
        Assert.Equal(0, LocalFileLookup.Create([]).Count);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("Test Tone & Co", "Test+Tone+%26+Co")]
    [InlineData("a*b-c.d_e~f", "a*b-c.d_e%7Ef")]
    [InlineData("Björk/Ü:x", "Bj%C3%B6rk%2F%C3%9C%3Ax")]
    public void FormatUriPart_is_form_urlencoded(string? input, string expected)
    {
        Assert.Equal(expected, LocalFileLookup.FormatUriPart(input));
    }

    [Theory]
    [InlineData("x/song.mp3", "mp3")]
    [InlineData("x/song.tar.FLAC", "FLAC")]
    [InlineData("x/.mp3", null)]
    [InlineData("x/noext", null)]
    [InlineData("x/trailing.", "")]
    public void RustExtension_matches_path_extension(string path, string? expected)
    {
        Assert.Equal(expected, LocalFileLookup.RustExtension(path));
    }
}
