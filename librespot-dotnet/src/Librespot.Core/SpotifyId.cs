// Port of core/src/spotify_id.rs.
using System.Buffers.Binary;
using System.Globalization;

namespace Librespot.Core;

/// <summary>The upstream <c>SpotifyIdError</c> variants.</summary>
public enum SpotifyIdError
{
    /// <summary>ID cannot be parsed.</summary>
    InvalidId,
    /// <summary>Not a valid Spotify ID.</summary>
    InvalidFormat,
}

/// <summary>Thrown when a Spotify ID cannot be parsed; always <see cref="ErrorKind.InvalidArgument"/>.</summary>
public sealed class SpotifyIdException : LibrespotException
{
    public SpotifyIdError Error { get; }

    public SpotifyIdException(SpotifyIdError error)
        : base(ErrorKind.InvalidArgument, Describe(error))
    {
        Error = error;
    }

    private static string Describe(SpotifyIdError error) => error switch
    {
        SpotifyIdError.InvalidId => "ID cannot be parsed",
        SpotifyIdError.InvalidFormat => "not a valid Spotify ID",
        _ => "not a valid Spotify ID",
    };
}

/// <summary>A 128-bit Spotify item id (track, album, artist, ...), formatted as base62 (22 chars) or base16 (32 chars).</summary>
/// <param name="Id">The raw 128-bit value.</param>
public readonly record struct SpotifyId(UInt128 Id)
{
    /// <summary>Byte length of the raw form.</summary>
    public const int Size = 16;

    /// <summary>Character length of the base62 form.</summary>
    public const int SizeBase62 = 22;

    private const string Base62Digits = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>
    /// Parses a base16 (hex) encoded Spotify ID. <paramref name="src"/> is expected to be 32 characters long
    /// and encoded using valid characters.
    /// </summary>
    public static SpotifyId FromBase16(string src)
    {
        if (src.Length != 32)
        {
            throw new SpotifyIdException(SpotifyIdError.InvalidId);
        }

        if (!UInt128.TryParse(src, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id))
        {
            throw new SpotifyIdException(SpotifyIdError.InvalidId);
        }

        return new SpotifyId(id);
    }

    /// <summary>
    /// Parses a base62 encoded Spotify ID. <paramref name="src"/> is expected to be 22 characters long and
    /// encoded using valid characters; values that do not fit in 128 bits are rejected.
    /// </summary>
    public static SpotifyId FromBase62(string src)
    {
        if (src.Length != SizeBase62)
        {
            throw new SpotifyIdException(SpotifyIdError.InvalidId);
        }

        UInt128 dst = 0;
        foreach (var c in src)
        {
            UInt128 p = c switch
            {
                >= '0' and <= '9' => (uint)(c - '0'),
                >= 'a' and <= 'z' => (uint)(c - 'a' + 10),
                >= 'A' and <= 'Z' => (uint)(c - 'A' + 36),
                _ => throw new SpotifyIdException(SpotifyIdError.InvalidId),
            };

            try
            {
                dst = checked(dst * 62);
                dst = checked(dst + p);
            }
            catch (OverflowException)
            {
                throw new SpotifyIdException(SpotifyIdError.InvalidId);
            }
        }

        return new SpotifyId(dst);
    }

    /// <summary>Creates an id from exactly <see cref="Size"/> (16) bytes in big-endian order.</summary>
    public static SpotifyId FromRaw(ReadOnlySpan<byte> src)
    {
        if (src.Length != Size)
        {
            throw new SpotifyIdException(SpotifyIdError.InvalidId);
        }

        return new SpotifyId(BinaryPrimitives.ReadUInt128BigEndian(src));
    }

    /// <summary>Upstream <c>TryFrom&lt;&amp;SpotifyUri&gt;</c>: the id of a typed URI; Local/Unknown URIs have none.</summary>
    public static SpotifyId From(SpotifyUri uri) => uri switch
    {
        SpotifyUri.Album a => a.Id,
        SpotifyUri.Artist a => a.Id,
        SpotifyUri.Episode e => e.Id,
        SpotifyUri.Playlist p => p.Id,
        SpotifyUri.Show s => s.Id,
        SpotifyUri.Track t => t.Id,
        _ => throw new SpotifyIdException(SpotifyIdError.InvalidFormat),
    };

    /// <summary>Returns the id as a base16 (hex) encoded, 32-character long string.</summary>
    public string ToBase16() => Id.ToString("x32", CultureInfo.InvariantCulture);

    /// <summary>Returns the id as a canonically base62 encoded, 22-character long string.</summary>
    public string ToBase62()
    {
        Span<char> dst = stackalloc char[SizeBase62];
        var n = Id;
        for (var i = SizeBase62 - 1; i >= 0; i--)
        {
            (n, var rem) = UInt128.DivRem(n, 62);
            dst[i] = Base62Digits[(int)rem];
        }

        return new string(dst);
    }

    /// <summary>Returns a copy of the id as <see cref="Size"/> (16) bytes in big-endian order.</summary>
    public byte[] ToRaw()
    {
        var raw = new byte[Size];
        BinaryPrimitives.WriteUInt128BigEndian(raw, Id);
        return raw;
    }

    /// <summary>Upstream <c>Display</c>: base62.</summary>
    public override string ToString() => ToBase62();
}
