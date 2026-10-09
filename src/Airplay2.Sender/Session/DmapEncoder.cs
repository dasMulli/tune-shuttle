using System.Buffers.Binary;
using System.Text;

namespace Airplay2.Sender.Session;

/// <summary>DMAP (<c>application/x-dmap-tagged</c>) encoder for the text metadata item (SPEC-control.md 5.2).</summary>
/// <remarks>
/// Each item is <c>tag (4 ASCII bytes) || length (4 bytes, big-endian) || value</c>; integers are big-endian with the
/// length giving the width, strings UTF-8 without terminator, a container's value is the concatenation of its items
/// [pyatv:pyatv/protocols/dmap/tags.py:39-88]. shairport-sync skips the 8-byte outer header and walks the items
/// [sps:rtsp.c:3282-3314], so the outer <c>mlit</c> must be present.
/// </remarks>
public static class DmapEncoder
{
    private const int HeaderSize = 8;

    /// <summary>
    /// Encodes <c>mlit { minm, asar, asal, astm?, astn?, asdn?, asgn? }</c>: title, artist and album as strings (absent or
    /// empty values are left out, as pyatv does [pyatv:pyatv/support/rtsp.py:208-216]), the duration as <c>astm</c>
    /// (uint32 milliseconds), track and disc number as <c>astn</c>/<c>asdn</c> (uint16) and the genre as <c>asgn</c>.
    /// </summary>
    public static byte[] EncodeListingItem(TrackMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var items = new List<(string Tag, byte[] Value)>(7);
        AddString(items, "minm", metadata.Title);
        AddString(items, "asar", metadata.Artist);
        AddString(items, "asal", metadata.Album);
        if (metadata.Duration is { } duration)
        {
            var ms = duration.TotalMilliseconds;
            if (ms < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(metadata), duration, "The duration must not be negative.");
            }

            var value = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(value, ms >= uint.MaxValue ? uint.MaxValue : (uint)Math.Round(ms));
            items.Add(("astm", value));
        }

        AddUInt16(items, "astn", metadata.TrackNumber, nameof(metadata.TrackNumber));
        AddUInt16(items, "asdn", metadata.DiscNumber, nameof(metadata.DiscNumber));
        AddString(items, "asgn", metadata.Genre);

        var contentLength = 0;
        foreach (var (_, value) in items)
        {
            contentLength += HeaderSize + value.Length;
        }

        var output = new byte[HeaderSize + contentLength];
        var span = output.AsSpan();
        WriteHeader(span, "mlit", contentLength);
        var offset = HeaderSize;
        foreach (var (tag, value) in items)
        {
            WriteHeader(span[offset..], tag, value.Length);
            value.CopyTo(span[(offset + HeaderSize)..]);
            offset += HeaderSize + value.Length;
        }

        return output;
    }

    private static void AddString(List<(string, byte[])> items, string tag, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            items.Add((tag, Encoding.UTF8.GetBytes(value)));
        }
    }

    private static void AddUInt16(List<(string, byte[])> items, string tag, int? value, string name)
    {
        if (value is not { } v)
        {
            return;
        }

        if (v is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(name, v, $"{tag} is an unsigned 16-bit value.");
        }

        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)v);
        items.Add((tag, bytes));
    }

    private static void WriteHeader(Span<byte> destination, string tag, int length)
    {
        Encoding.ASCII.GetBytes(tag, destination[..4]);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..8], (uint)length);
    }
}
