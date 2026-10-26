using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Airplay2.Sender.Session;

/// <summary>
/// DMAP (<c>application/x-dmap-tagged</c>) decoder for the text metadata item a sender sends (SPEC-receiver.md 4.8,
/// SPEC-control.md 5.2): the inverse of <see cref="DmapEncoder"/>. Walks <c>tag || length || value</c> items inside the
/// outer <c>mlit</c> (shairport-sync skips the 8-byte outer header the same way, <c>[sps] rtsp.c:3282-3314</c>); unknown
/// tags are kept by code, malformed bodies are rejected.
/// </summary>
/// <remarks>
/// Item layout and types from SPEC-control.md 5.2 (<c>[pyatv] pyatv/protocols/dmap/tags.py:39-88</c>): the tag is four
/// ASCII bytes, the length four bytes big-endian, integers big-endian with the length giving the width (1, 2, 4 or 8),
/// strings UTF-8 without terminator. The outer tag must be <c>mlit</c>; its declared length bounds the walk (bytes after
/// it are ignored, as shairport-sync does not check the length), but a declared length past the end of the body, an
/// item whose length runs past the container, or a partial item header is a truncated body and rejected. A known tag
/// with an integer of another width is kept in <see cref="DmapItem.OtherTags"/> instead of being misread.
/// </remarks>
public static class DmapDecoder
{
    private const int HeaderSize = 8;
    private const ulong MaxDurationMilliseconds = (ulong)(long.MaxValue / TimeSpan.TicksPerMillisecond);

    /// <summary>Decodes an <c>mlit</c> body (SPEC-receiver.md 4.8).</summary>
    /// <exception cref="FormatException">The body is not a well-formed <c>mlit</c> container.</exception>
    public static DmapItem Decode(ReadOnlySpan<byte> body) =>
        DecodeCore(body, out var error) ?? throw new FormatException(error);

    /// <summary>Decodes an <c>mlit</c> body; false instead of throwing for a malformed one (SPEC-receiver.md 4.8).</summary>
    public static bool TryDecode(ReadOnlySpan<byte> body, [NotNullWhen(true)] out DmapItem? item)
    {
        item = DecodeCore(body, out _);
        return item is not null;
    }

    /// <summary>
    /// Reads a DMAP integer value by its width (SPEC-control.md 5.2: 1, 2, 4 or 8 bytes, big-endian), for example one of
    /// <see cref="DmapItem.OtherTags"/>; null for any other width.
    /// </summary>
    public static ulong? ReadUnsigned(ReadOnlySpan<byte> value) => value.Length switch
    {
        1 => value[0],
        2 => BinaryPrimitives.ReadUInt16BigEndian(value),
        4 => BinaryPrimitives.ReadUInt32BigEndian(value),
        8 => BinaryPrimitives.ReadUInt64BigEndian(value),
        _ => null,
    };

    private static DmapItem? DecodeCore(ReadOnlySpan<byte> body, out string error)
    {
        if (body.Length < HeaderSize)
        {
            error = $"A DMAP body needs an 8-byte mlit header; it has {body.Length} bytes.";
            return null;
        }

        if (!body[..4].SequenceEqual("mlit"u8))
        {
            error = $"The outer DMAP tag is '{Tag(body[..4])}', not 'mlit'.";
            return null;
        }

        var declared = BinaryPrimitives.ReadUInt32BigEndian(body[4..8]);
        if (declared > (uint)(body.Length - HeaderSize))
        {
            error = $"The mlit container declares {declared} bytes but only {body.Length - HeaderSize} follow (truncated body).";
            return null;
        }

        string? title = null, artist = null, album = null, genre = null;
        TimeSpan? duration = null;
        int? trackNumber = null, discNumber = null;
        var other = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        var rest = body.Slice(HeaderSize, (int)declared);
        while (!rest.IsEmpty)
        {
            if (rest.Length < HeaderSize)
            {
                error = $"{rest.Length} bytes after the last DMAP item are not a full item header (truncated body).";
                return null;
            }

            var tag = Tag(rest[..4]);
            var length = BinaryPrimitives.ReadUInt32BigEndian(rest[4..8]);
            if (length > (uint)(rest.Length - HeaderSize))
            {
                error = $"DMAP item '{tag}' declares {length} bytes but only {rest.Length - HeaderSize} remain (truncated body).";
                return null;
            }

            var value = rest.Slice(HeaderSize, (int)length);
            rest = rest[(HeaderSize + (int)length)..];
            switch (tag)
            {
                case "minm":
                    title = Encoding.UTF8.GetString(value);
                    continue;
                case "asar":
                    artist = Encoding.UTF8.GetString(value);
                    continue;
                case "asal":
                    album = Encoding.UTF8.GetString(value);
                    continue;
                case "asgn":
                    genre = Encoding.UTF8.GetString(value);
                    continue;
                case "astm" when ReadUnsigned(value) is { } ms && ms <= MaxDurationMilliseconds:
                    duration = TimeSpan.FromMilliseconds((long)ms);
                    continue;
                case "astn" when ReadUnsigned(value) is { } n && n <= int.MaxValue:
                    trackNumber = (int)n;
                    continue;
                case "asdn" when ReadUnsigned(value) is { } n && n <= int.MaxValue:
                    discNumber = (int)n;
                    continue;
                default:
                    other[tag] = value.ToArray();
                    continue;
            }
        }

        error = string.Empty;
        return new DmapItem(title, artist, album, duration, trackNumber, discNumber, genre, other.AsReadOnly());
    }

    private static string Tag(ReadOnlySpan<byte> tag) => Encoding.ASCII.GetString(tag);
}
