// Port of playback/src/symphonia_util.rs, plus the minimal subset of symphonia's metadata model
// (symphonia_core::meta::{StandardTagKey, Tag, Value}) and format probe that the decoders and the local file
// lookup need. NVorbis and NLayer replace symphonia's format readers and codecs; this file supplies what they
// lack: a content-sniffing probe (Ogg / ID3v2 + MPEG audio) and a common tag representation.
using System.Globalization;

namespace Librespot.Playback.Decoder;

/// <summary>The subset of symphonia's <c>StandardTagKey</c> that librespot reads.</summary>
internal enum StandardTagKey
{
    Album,
    AlbumArtist,
    Artist,
    DiscNumber,
    Language,
    ReplayGainAlbumGain,
    ReplayGainAlbumPeak,
    ReplayGainTrackGain,
    ReplayGainTrackPeak,
    TrackNumber,
    TrackTitle,
}

/// <summary>symphonia's <c>meta::Value</c> (the variants librespot inspects).</summary>
internal abstract record TagValue
{
    private TagValue()
    {
    }

    /// <summary><c>Value::String</c>.</summary>
    public sealed record String(string Value) : TagValue
    {
        public override string ToString() => Value;
    }

    /// <summary><c>Value::UnsignedInt</c>.</summary>
    public sealed record UnsignedInt(ulong Value) : TagValue
    {
        public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary><c>Value::SignedInt</c>.</summary>
    public sealed record SignedInt(long Value) : TagValue
    {
        public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary><c>Value::Float</c>.</summary>
    public sealed record Float(double Value) : TagValue
    {
        public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>symphonia's <c>meta::Tag</c>: an optional standard key, the format-specific key, and the value.</summary>
internal sealed record Tag(StandardTagKey? StdKey, string Key, TagValue Value);

/// <summary>The container formats the probe recognises (symphonia's registered format readers that are ported).</summary>
internal enum ProbedFormat
{
    /// <summary>Ogg container (decoded with NVorbis; only Vorbis streams are supported).</summary>
    Ogg,

    /// <summary>MPEG audio (decoded with NLayer).</summary>
    Mpa,
}

/// <summary>
/// The result of <see cref="DecoderUtil.Probe"/> (symphonia's <c>ProbeResult</c>): the detected format and the
/// metadata found while probing in front of the container (ID3v2), which symphonia keeps separately from the
/// container's own metadata.
/// </summary>
internal sealed record ProbeResult(ProbedFormat Format, IReadOnlyList<Tag>? Metadata);

/// <summary>Helpers shared by the decoders and the local file lookup (upstream <c>symphonia_util</c>).</summary>
internal static class DecoderUtil
{
    /// <summary>
    /// Upstream <c>get_latest_metadata</c>: the container's metadata, or, if the container has none, the tags found
    /// by probing (only relevant for local files). Null when neither has a metadata revision (upstream:
    /// <c>metadata.current()</c> is <c>None</c>).
    /// </summary>
    public static IReadOnlyList<Tag>? GetLatestMetadata(IReadOnlyList<Tag>? containerMetadata, ProbeResult probeResult)
    {
        var metadata = containerMetadata;

        // If we can't get metadata from the container, fall back to other tags found by probing.
        // Note that this is only relevant for local files.
        if (metadata is null && probeResult.Metadata is { } innerProbeMetadata)
        {
            metadata = innerProbeMetadata;
        }

        return metadata;
    }

    /// <summary>
    /// Detects the format of <paramref name="stream"/> by content (symphonia's probe), reading any ID3v2 tag in front
    /// of it. The hint is only used when the content is not recognised. The stream is returned to its original
    /// position.
    /// </summary>
    /// <exception cref="DecoderException">
    /// No supported format was found, or the ID3v2 tag is malformed or unsupported (symphonia's probe propagates the
    /// metadata reader's error).
    /// </exception>
    public static ProbeResult Probe(Stream stream, DecoderHint hint)
    {
        var start = stream.Position;
        try
        {
            IReadOnlyList<Tag>? metadata = null;
            Span<byte> marker = stackalloc byte[8];
            var read = ReadAtMost(stream, marker);

            if (read >= 3 && marker[..3].SequenceEqual("ID3"u8))
            {
                stream.Position = start;
                try
                {
                    metadata = Id3v2.Read(stream);
                }
                catch (InvalidDataException e)
                {
                    // Upstream: the metadata reader's error propagates out of the probe (symphonia DecodeError).
                    throw new DecoderException(DecoderException.Reason.AudioDecoder, $"malformed stream: {e.Message}", e);
                }
                catch (NotSupportedException e)
                {
                    // Upstream: symphonia Unsupported error, propagated out of the probe.
                    throw new DecoderException(DecoderException.Reason.AudioDecoder, $"unsupported feature: {e.Message}", e);
                }

                read = ReadAtMost(stream, marker);
            }

            ProbedFormat? format = null;
            if (read >= 4 && marker[..4].SequenceEqual("OggS"u8))
            {
                // An Ogg stream behind an ID3v2 tag is not supported: NVorbis needs the container at the start.
                format = metadata is null ? ProbedFormat.Ogg : null;
            }
            else if (read >= 2 && IsMpegAudioSync(marker[0], marker[1]))
            {
                format = ProbedFormat.Mpa;
            }
            else if (!(read >= 4 && marker[..4].SequenceEqual("fLaC"u8)) &&
                     !(read >= 8 && marker[4..8].SequenceEqual("ftyp"u8)) &&
                     (metadata is not null || HintsMpegAudio(hint)))
            {
                // Content not recognised (e.g. junk before the first frame); NLayer resynchronises on its own.
                format = ProbedFormat.Mpa;
            }

            if (format is null)
            {
                throw new DecoderException(
                    DecoderException.Reason.AudioDecoder,
                    "unsupported feature: core (probe): no suitable format reader found");
            }

            return new ProbeResult(format.Value, metadata);
        }
        catch (IOException e)
        {
            throw new DecoderException(DecoderException.Reason.AudioDecoder, e.Message, e);
        }
        finally
        {
            stream.Position = start;
        }
    }

    /// <summary>Upstream <c>u32::from_str</c> (optional '+', ASCII digits only); null plus the Rust error text on failure.</summary>
    public static uint? ParseU32(string value, out string? error)
    {
        error = null;
        var digits = value.AsSpan();
        if (digits.Length > 0 && digits[0] == '+')
        {
            digits = digits[1..];
            if (digits.Length == 0)
            {
                error = "invalid digit found in string";
                return null;
            }
        }

        if (digits.Length == 0)
        {
            error = "cannot parse integer from empty string";
            return null;
        }

        ulong result = 0;
        foreach (var c in digits)
        {
            if (c is < '0' or > '9')
            {
                error = "invalid digit found in string";
                return null;
            }

            result = result * 10 + (ulong)(c - '0');
            if (result > uint.MaxValue)
            {
                error = "number too large to fit in target type";
                return null;
            }
        }

        return (uint)result;
    }

    private static bool IsMpegAudioSync(byte b0, byte b1) => b0 == 0xff && (b1 & 0xe0) == 0xe0 && (b1 & 0x06) != 0;

    private static bool HintsMpegAudio(DecoderHint hint) =>
        string.Equals(hint.MimeType, "audio/mpeg", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(hint.MimeType, "audio/mp3", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(hint.Extension, "mp3", StringComparison.OrdinalIgnoreCase);

    private static int ReadAtMost(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer[total..]);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }
}
