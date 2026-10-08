// Port of the #[cfg(test)] module in core/src/spotify_id.rs.
using Librespot.Core;
using Xunit;

namespace Librespot.Tests.Core;

public class SpotifyIdTests
{
    private sealed record ConversionCase(UInt128 Id, string Base16, string Base62, byte[] Raw);

    private static readonly ConversionCase[] ConvValid =
    [
        new(UInt128.Parse("238762092608182713602505436543891614649"), "b39fe8081e1f4c54be38e8d6f9f12bb9", "5sWHDYs0csV6RS48xBl0tH",
            [179, 159, 232, 8, 30, 31, 76, 84, 190, 56, 232, 214, 249, 241, 43, 185]),
        new(UInt128.Parse("204841891221366092811751085145916697048"), "9a1b1cfbc6f244569ae0356c77bbe9d8", "4GNcXTGWmnZ3ySrqvol3o4",
            [154, 27, 28, 251, 198, 242, 68, 86, 154, 224, 53, 108, 119, 187, 233, 216]),
        new(0, "00000000000000000000000000000000", "0000000000000000000000", new byte[16]),
    ];

    private static readonly ConversionCase[] ConvInvalid =
    [
        new(0, "ZZZZZ8081e1f4c54be38e8d6f9f12bb9", "!!!!!Ys0csV6RS48xBl0tH",
            [154, 27, 28, 251, 198, 242, 68, 86, 154, 224, 5, 3, 108, 119, 187, 233, 216, 255]), // invalid length
        new(0, "--------------------", "....................", [154, 27, 28, 251]),
        // too long, should return error but not panic overflow
        new(0, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", [154, 27, 28, 251]),
        // too short to encode a 128 bits int
        new(0, "--------------------", "aa", [154, 27, 28, 251]),
        // too high of a value, this would need a 132 bits int
        new(0, "--------------------", "ZZZZZZZZZZZZZZZZZZZZZZ", [154, 27, 28, 251]),
    ];

    [Fact]
    public void FromBase62()
    {
        foreach (var c in ConvValid)
        {
            Assert.Equal(c.Id, SpotifyId.FromBase62(c.Base62).Id);
        }

        foreach (var c in ConvInvalid)
        {
            Assert.Throws<SpotifyIdException>(() => SpotifyId.FromBase62(c.Base62));
        }
    }

    [Fact]
    public void ToBase62()
    {
        foreach (var c in ConvValid)
        {
            Assert.Equal(c.Base62, new SpotifyId(c.Id).ToBase62());
        }
    }

    [Fact]
    public void FromBase16()
    {
        foreach (var c in ConvValid)
        {
            Assert.Equal(c.Id, SpotifyId.FromBase16(c.Base16).Id);
        }

        foreach (var c in ConvInvalid)
        {
            Assert.Throws<SpotifyIdException>(() => SpotifyId.FromBase16(c.Base16));
        }
    }

    [Fact]
    public void ToBase16()
    {
        foreach (var c in ConvValid)
        {
            Assert.Equal(c.Base16, new SpotifyId(c.Id).ToBase16());
        }
    }

    [Fact]
    public void FromRaw()
    {
        foreach (var c in ConvValid)
        {
            Assert.Equal(c.Id, SpotifyId.FromRaw(c.Raw).Id);
            Assert.Equal(c.Raw, new SpotifyId(c.Id).ToRaw());
        }

        foreach (var c in ConvInvalid)
        {
            Assert.Throws<SpotifyIdException>(() => SpotifyId.FromRaw(c.Raw));
        }
    }

    [Fact]
    public void Exception_has_invalid_argument_kind_and_rust_display_format()
    {
        var e = Assert.Throws<SpotifyIdException>(() => SpotifyId.FromBase62("aa"));
        Assert.Equal(ErrorKind.InvalidArgument, e.Kind);
        Assert.Equal("Client specified an invalid argument { ID cannot be parsed }", e.Message);
        Assert.Same(e, LibrespotException.From(e));
    }

    [Fact]
    public void FileId_pads_short_input_and_formats_hex()
    {
        var id = FileId.FromRaw([0xde, 0xad, 0xbe, 0xef]);
        Assert.Equal("deadbeef" + new string('0', 32), id.ToBase16());
        Assert.Equal(id, FileId.FromRaw([0xde, 0xad, 0xbe, 0xef]));
        Assert.NotEqual(id, default);
        Assert.Equal(new string('0', 40), default(FileId).ToString());
        // longer than 20 bytes -> all zeros (upstream behaviour)
        Assert.Equal(default, FileId.FromRaw(new byte[21]));
    }
}
