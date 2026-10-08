// Minimal port of symphonia-metadata's id3v2 reader (symphonia_metadata::id3v2, v0.5.5), which symphonia's probe
// runs on local MP3 files. Only text frames (T***, TXXX) are turned into tags, mapped to the standard keys
// librespot reads (title, artist, album artist, album, track/disc number, language, ReplayGain via TXXX). Values
// stay strings, exactly as symphonia stores them. Pictures, comments, URLs and other frames are skipped. As in
// symphonia, a malformed tag and a compressed or encrypted text frame are errors that fail the whole read
// (symphonia's probe propagates them). Deviation: symphonia also rejects compressed/encrypted non-text frames it has a
// parser for, and validates the unused frame flag bits; this reader skips those frames unchecked.
using System.Buffers.Binary;
using System.Text;

namespace Librespot.Playback.Decoder;

/// <summary>Reads an ID3v2 (2.2, 2.3, 2.4) tag into <see cref="Tag"/>s.</summary>
internal static class Id3v2
{
    private static readonly Dictionary<string, StandardTagKey> TextFrameStdKeys = new(StringComparer.Ordinal)
    {
        ["TALB"] = StandardTagKey.Album,
        ["TIT2"] = StandardTagKey.TrackTitle,
        ["TLAN"] = StandardTagKey.Language,
        ["TPE1"] = StandardTagKey.Artist,
        ["TPE2"] = StandardTagKey.AlbumArtist,
        ["TPOS"] = StandardTagKey.DiscNumber,
        ["TRCK"] = StandardTagKey.TrackNumber,
    };

    private static readonly Dictionary<string, StandardTagKey> TxxxFrameStdKeys = new(StringComparer.Ordinal)
    {
        ["REPLAYGAIN_ALBUM_GAIN"] = StandardTagKey.ReplayGainAlbumGain,
        ["REPLAYGAIN_ALBUM_PEAK"] = StandardTagKey.ReplayGainAlbumPeak,
        ["REPLAYGAIN_TRACK_GAIN"] = StandardTagKey.ReplayGainTrackGain,
        ["REPLAYGAIN_TRACK_PEAK"] = StandardTagKey.ReplayGainTrackPeak,
    };

    /// <summary>ID3v2.2 three-character frame ids of the frames above, mapped to their ID3v2.3 ids.</summary>
    private static readonly Dictionary<string, string> LegacyFrameIds = new(StringComparer.Ordinal)
    {
        ["TAL"] = "TALB",
        ["TLA"] = "TLAN",
        ["TP1"] = "TPE1",
        ["TP2"] = "TPE2",
        ["TPA"] = "TPOS",
        ["TRK"] = "TRCK",
        ["TT2"] = "TIT2",
        ["TXX"] = "TXXX",
    };

    /// <summary>
    /// Reads the tag at the current position of <paramref name="stream"/> and leaves the stream after it.
    /// </summary>
    /// <exception cref="InvalidDataException">The tag is malformed (symphonia <c>DecodeError</c>).</exception>
    /// <exception cref="NotSupportedException">The tag uses an unsupported feature (symphonia <c>Unsupported</c>).</exception>
    /// <exception cref="EndOfStreamException">The stream ends inside the tag.</exception>
    public static List<Tag> Read(Stream stream)
    {
        Span<byte> header = stackalloc byte[10];
        stream.ReadExactly(header);

        if (!header[..3].SequenceEqual("ID3"u8))
        {
            throw new InvalidDataException("id3v2: not an ID3v2 tag");
        }

        var majorVersion = header[3];
        var minorVersion = header[4];
        var flags = header[5];
        var size = ReadSyncsafe(header[6..10]);

        // Major and minor version numbers should never equal 0xff as per the specification.
        if (majorVersion == 0xff || minorVersion == 0xff)
        {
            throw new InvalidDataException("id3v2: invalid version number(s)");
        }

        // Only support versions 2.2.x to 2.4.x of the specification.
        if (majorVersion is < 2 or > 4)
        {
            throw new NotSupportedException("id3v2: unsupported ID3v2 version");
        }

        // Version 2.2 specifies a compression flag bit, but no compression standard.
        if (majorVersion == 2 && (flags & 0x40) != 0)
        {
            throw new NotSupportedException("id3v2: ID3v2.2 compression is not supported");
        }

        var unsynchronisation = (flags & 0x80) != 0;
        var hasExtendedHeader = majorVersion >= 3 && (flags & 0x40) != 0;
        var hasFooter = majorVersion == 4 && (flags & 0x10) != 0;

        var body = new byte[size];
        stream.ReadExactly(body);
        if (hasFooter)
        {
            stream.ReadExactly(stackalloc byte[10]);
        }

        // If the unsynchronisation flag is set in the header, all tag data must be passed through the
        // unsynchronisation decoder before being read for versions < 4 of ID3v2.
        if (unsynchronisation && majorVersion < 4)
        {
            body = DecodeUnsynchronisation(body);
        }

        return ReadBody(body, majorVersion, hasExtendedHeader);
    }

    private static List<Tag> ReadBody(byte[] body, byte majorVersion, bool hasExtendedHeader)
    {
        var tags = new List<Tag>();
        var pos = 0;

        if (hasExtendedHeader)
        {
            if (body.Length < 4)
            {
                throw new InvalidDataException("id3v2: truncated extended header");
            }

            pos = majorVersion == 3
                ? 4 + (int)BinaryPrimitives.ReadUInt32BigEndian(body) // size excludes the size field itself
                : (int)ReadSyncsafe(body.AsSpan(0, 4)); // size includes the size field
            if (pos < 0 || pos > body.Length)
            {
                throw new InvalidDataException("id3v2: invalid extended header size");
            }
        }

        var minFrameSize = majorVersion == 2 ? 6 : 10;

        while (body.Length - pos >= minFrameSize)
        {
            // The padding has been reached, don't parse any further.
            if (body[pos] == 0)
            {
                break;
            }

            string id;
            int frameSize;
            ushort frameFlags = 0;
            if (majorVersion == 2)
            {
                var legacyId = Encoding.ASCII.GetString(body, pos, 3);
                frameSize = (body[pos + 3] << 16) | (body[pos + 4] << 8) | body[pos + 5];
                id = LegacyFrameIds.TryGetValue(legacyId, out var mapped) ? mapped : legacyId;
                pos += 6;
            }
            else
            {
                id = Encoding.ASCII.GetString(body, pos, 4);
                frameSize = majorVersion == 3
                    ? (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(pos + 4))
                    : (int)ReadSyncsafe(body.AsSpan(pos + 4, 4));
                frameFlags = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(pos + 8));
                pos += 10;
            }

            if (frameSize < 0 || frameSize > body.Length - pos)
            {
                throw new InvalidDataException($"id3v2: frame {id} exceeds the tag size");
            }

            var frame = body.AsSpan(pos, frameSize);
            pos += frameSize;

            // Frames without a parser are skipped before their flags are looked at.
            var isTxxx = id == "TXXX";
            if (!isTxxx && !(id.Length == 4 && id[0] == 'T'))
            {
                continue;
            }

            UnwrapFrame(majorVersion, frameFlags, ref frame, out var unsyncFrame);
            if (unsyncFrame)
            {
                frame = DecodeUnsynchronisation(frame.ToArray());
            }

            if (isTxxx)
            {
                ReadTxxxFrame(frame, tags);
            }
            else
            {
                ReadTextFrame(frame, id, tags);
            }
        }

        return tags;
    }

    /// <summary>Applies the frame format flags.</summary>
    /// <exception cref="NotSupportedException">The frame is compressed or encrypted.</exception>
    private static void UnwrapFrame(byte majorVersion, ushort flags, ref Span<byte> frame, out bool unsync)
    {
        unsync = false;
        switch (majorVersion)
        {
            case 3:
            {
                // Compression (0x0080) and encryption (0x0040) are not supported.
                ThrowIfCompressedOrEncrypted((flags & 0x0080) != 0, (flags & 0x0040) != 0);

                // Grouping identity (0x0020) adds one byte.
                if ((flags & 0x0020) != 0)
                {
                    frame = frame.Length >= 1 ? frame[1..] : [];
                }

                return;
            }

            case 4:
            {
                // Compression (0x0008) and encryption (0x0004) are not supported.
                ThrowIfCompressedOrEncrypted((flags & 0x0008) != 0, (flags & 0x0004) != 0);

                // Grouping identity (0x0040) adds one byte.
                if ((flags & 0x0040) != 0)
                {
                    frame = frame.Length >= 1 ? frame[1..] : [];
                }

                // Data length indicator (0x0001) adds four bytes.
                if ((flags & 0x0001) != 0)
                {
                    frame = frame.Length >= 4 ? frame[4..] : [];
                }

                unsync = (flags & 0x0002) != 0;
                return;
            }

            default:
                return;
        }
    }

    private static void ThrowIfCompressedOrEncrypted(bool compressed, bool encrypted)
    {
        if (compressed)
        {
            throw new NotSupportedException("id3v2: compressed frames are not supported");
        }

        if (encrypted)
        {
            throw new NotSupportedException("id3v2: encrypted frames are not supported");
        }
    }

    /// <summary>Reads all text frames except <c>TXXX</c>: one tag per listed value.</summary>
    private static void ReadTextFrame(ReadOnlySpan<byte> frame, string id, List<Tag> tags)
    {
        if (frame.Length == 0)
        {
            throw new InvalidDataException("id3v2: invalid text encoding");
        }

        var encoding = frame[0];
        if (encoding > 3)
        {
            throw new InvalidDataException("id3v2: invalid text encoding");
        }

        StandardTagKey? stdKey = TextFrameStdKeys.TryGetValue(id, out var key) ? key : null;
        var rest = frame[1..];
        while (rest.Length > 0)
        {
            var text = ScanText(ref rest, encoding);
            tags.Add(new Tag(stdKey, id, new TagValue.String(text)));
        }
    }

    /// <summary>Reads a <c>TXXX</c> (user defined) text frame: a description followed by one or more values.</summary>
    private static void ReadTxxxFrame(ReadOnlySpan<byte> frame, List<Tag> tags)
    {
        if (frame.Length == 0)
        {
            throw new InvalidDataException("id3v2: invalid TXXX text encoding");
        }

        var encoding = frame[0];
        if (encoding > 3)
        {
            throw new InvalidDataException("id3v2: invalid TXXX text encoding");
        }

        var rest = frame[1..];
        var desc = ScanText(ref rest, encoding);

        // Some TXXX frames may be mapped to standard keys.
        StandardTagKey? stdKey = TxxxFrameStdKeys.TryGetValue(desc, out var key) ? key : null;
        var tagKey = $"TXXX:{desc}";

        while (rest.Length > 0)
        {
            var text = ScanText(ref rest, encoding);
            tags.Add(new Tag(stdKey, tagKey, new TagValue.String(text)));
        }
    }

    /// <summary>
    /// symphonia <c>scan_text</c> + <c>decode_text</c>: reads up to and including the (aligned) null terminator and
    /// decodes the text without trailing terminators.
    /// </summary>
    private static string ScanText(ref ReadOnlySpan<byte> data, byte encoding)
    {
        var wide = encoding is 1 or 2;
        var align = wide ? 2 : 1;
        var end = data.Length;
        for (var i = 0; i + align <= data.Length; i += align)
        {
            if (data[i] == 0 && (!wide || data[i + 1] == 0))
            {
                end = i + align;
                break;
            }
        }

        var buf = data[..end];
        data = data[end..];

        switch (encoding)
        {
            case 0:
            {
                // ISO-8859-1: only characters between 0x20 and 0xFF are valid; terminators are dropped.
                var sb = new StringBuilder(buf.Length);
                foreach (var b in buf)
                {
                    if (b > 0x1f)
                    {
                        sb.Append((char)b);
                    }
                }

                return sb.ToString();
            }

            case 3:
            {
                var len = buf.Length;
                while (len > 0 && buf[len - 1] == 0)
                {
                    len--;
                }

                return Encoding.UTF8.GetString(buf[..len]);
            }

            default:
            {
                var len = buf.Length;
                while (len > 1 && buf[len - 2] == 0 && buf[len - 1] == 0)
                {
                    len -= 2;
                }

                // Both UTF-16 encodings go through encoding_rs's UTF_16BE.decode, which sniffs a byte-order mark
                // (UTF-8, UTF-16BE or UTF-16LE). Otherwise, big endian is assumed.
                var text = buf[..len];
                if (text.Length >= 3 && text[0] == 0xef && text[1] == 0xbb && text[2] == 0xbf)
                {
                    return Encoding.UTF8.GetString(text[3..]);
                }

                if (text.Length >= 2 && text[0] == 0xfe && text[1] == 0xff)
                {
                    return Encoding.BigEndianUnicode.GetString(text[2..]);
                }

                if (text.Length >= 2 && text[0] == 0xff && text[1] == 0xfe)
                {
                    return Encoding.Unicode.GetString(text[2..]);
                }

                return Encoding.BigEndianUnicode.GetString(text);
            }
        }
    }

    /// <summary>Removes the 0x00 inserted after every 0xFF by unsynchronisation.</summary>
    private static byte[] DecodeUnsynchronisation(byte[] data)
    {
        var output = new byte[data.Length];
        var len = 0;
        for (var i = 0; i < data.Length; i++)
        {
            output[len++] = data[i];
            if (data[i] == 0xff && i + 1 < data.Length && data[i + 1] == 0x00)
            {
                i++;
            }
        }

        return output.AsSpan(0, len).ToArray();
    }

    private static uint ReadSyncsafe(ReadOnlySpan<byte> bytes) =>
        ((uint)(bytes[0] & 0x7f) << 21) | ((uint)(bytes[1] & 0x7f) << 14) | ((uint)(bytes[2] & 0x7f) << 7) |
        (uint)(bytes[3] & 0x7f);
}
