// Port of playback/src/decoder/symphonia_decoder.rs, part 1: the decoder facade (SymphoniaDecoder::new picks a
// format reader and codec by probing; here it picks VorbisDecoder (NVorbis) or Mp3Decoder (NLayer)),
// LocalFileMetadata, and the tag handling of normalisation_data()/local_file_metadata(). The per-codec decoding
// (next_packet, seek, ts_to_ms) lives in VorbisDecoder.cs and Mp3Decoder.cs.
using Librespot.Core.Logging;
using Librespot.Metadata.Audio;
using Microsoft.Extensions.Logging;

namespace Librespot.Playback.Decoder;

/// <summary>
/// Format hint for the probe (symphonia's <c>Hint</c>): a MIME type (<c>hint.mime_type</c>) and/or a file
/// extension (<c>hint.with_extension</c>).
/// </summary>
public sealed record DecoderHint(string? MimeType = null, string? Extension = null);

/// <summary>Tag metadata of a local file (upstream <c>LocalFileMetadata</c> of <c>symphonia_decoder.rs</c>).</summary>
public sealed record LocalFileMetadata
{
    /// <summary>Track title.</summary>
    public string? Name { get; init; }

    /// <summary>Language tag.</summary>
    public string? Language { get; init; }

    /// <summary>Album title.</summary>
    public string? Album { get; init; }

    /// <summary>Artist tag (verbatim, not split).</summary>
    public string? Artists { get; init; }

    /// <summary>Album artist tag (verbatim, not split).</summary>
    public string? AlbumArtists { get; init; }

    /// <summary>Track number.</summary>
    public uint? Number { get; init; }

    /// <summary>Disc number.</summary>
    public uint? DiscNumber { get; init; }

    /// <summary>
    /// Duration of the decoded stream in ms when the decoder knows it. Not upstream (the player takes the duration
    /// from the <c>spotify:local:</c> URI); added by the port plan for convenience.
    /// </summary>
    public uint? DurationMs { get; init; }
}

/// <summary>
/// Creates audio decoders (upstream <c>SymphoniaDecoder::new</c> and the decoder selection in
/// <c>PlayerTrackLoader::load_track</c>).
/// </summary>
public static class AudioDecoders
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger(typeof(AudioDecoders).FullName!);

    /// <summary>
    /// Creates a decoder for a stream whose MIME type is known (or null). With <paramref name="passthrough"/> the
    /// stream must be Ogg Vorbis (<c>audio/ogg</c>) and a <see cref="PassthroughDecoder"/> is returned.
    /// </summary>
    /// <exception cref="DecoderException">The format is not supported or the stream cannot be decoded.</exception>
    public static IAudioDecoder Create(Stream stream, string? mimeType, bool passthrough)
    {
        if (passthrough)
        {
            if (!string.Equals(mimeType, "audio/ogg", StringComparison.OrdinalIgnoreCase))
            {
                throw new DecoderException(
                    DecoderException.Reason.PassthroughDecoder,
                    $"Passthrough decoder is not implemented for format {mimeType ?? "unknown"}");
            }

            return new PassthroughDecoder(stream, AudioFileFormat.OggVorbis320);
        }

        return Create(stream, new DecoderHint(MimeType: mimeType));
    }

    /// <summary>
    /// Creates a decoder for a Spotify audio file of the given format, as <c>PlayerTrackLoader::load_track</c> does:
    /// the passthrough decoder when <paramref name="passthrough"/> is set, otherwise the decoding decoder with the
    /// format's MIME type as hint.
    /// </summary>
    /// <exception cref="DecoderException">The format is not supported or the stream cannot be decoded.</exception>
    public static IAudioDecoder Create(Stream stream, AudioFileFormat format, bool passthrough)
    {
        if (passthrough)
        {
            return new PassthroughDecoder(stream, format);
        }

        return Create(stream, new DecoderHint(MimeType: AudioFiles.MimeType(format)));
    }

    /// <summary>
    /// Upstream <c>SymphoniaDecoder::new(input, hint)</c>: probes the stream, opens the matching decoder and checks
    /// that it produces 44.1 kHz stereo. The decoder owns <paramref name="input"/> and disposes it.
    /// </summary>
    /// <exception cref="DecoderException">The format is not supported or the stream cannot be decoded.</exception>
    public static IAudioDecoder Create(Stream input, DecoderHint hint)
    {
        ProbeResult probeResult;
        try
        {
            probeResult = DecoderUtil.Probe(input, hint);
        }
        catch
        {
            // Upstream drops (closes) the media source when probing fails.
            input.Dispose();
            throw;
        }

        Log.LogTrace("Probed {Format} stream", probeResult.Format);
        return probeResult.Format switch
        {
            ProbedFormat.Ogg => new VorbisDecoder(input, probeResult),
            _ => new Mp3Decoder(input, probeResult),
        };
    }

    /// <summary>
    /// Opens the stream's default track without the sample rate / channel checks and returns its metadata, frame
    /// count and sample rate (what <c>get_uri_from_file</c> reads from symphonia's probe result).
    /// </summary>
    /// <exception cref="DecoderException">The stream cannot be probed.</exception>
    internal static ProbedTrack ProbeTrack(Stream input, DecoderHint hint)
    {
        var probeResult = DecoderUtil.Probe(input, hint);
        return probeResult.Format switch
        {
            ProbedFormat.Ogg => VorbisDecoder.ProbeTrack(input, probeResult),
            _ => Mp3Decoder.ProbeTrack(input, probeResult),
        };
    }

    /// <summary>Upstream <c>SymphoniaDecoder::normalisation_data</c> over the latest metadata's tags.</summary>
    internal static NormalisationData? NormalisationDataFromTags(IReadOnlyList<Tag>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return null;
        }

        var data = NormalisationData.Default;
        foreach (var tag in tags)
        {
            // Like upstream, only float values are used. The tag readers store ReplayGain values as strings
            // (as symphonia does), so in practice this yields the defaults.
            if (tag.Value is TagValue.Float(var value))
            {
                data = tag.StdKey switch
                {
                    StandardTagKey.ReplayGainAlbumGain => data with { AlbumGainDb = value },
                    StandardTagKey.ReplayGainAlbumPeak => data with { AlbumPeak = value },
                    StandardTagKey.ReplayGainTrackGain => data with { TrackGainDb = value },
                    StandardTagKey.ReplayGainTrackPeak => data with { TrackPeak = value },
                    _ => data,
                };
            }
        }

        return data;
    }

    /// <summary>Upstream <c>SymphoniaDecoder::local_file_metadata</c> over the latest metadata's tags.</summary>
    internal static LocalFileMetadata? LocalFileMetadataFromTags(IReadOnlyList<Tag>? tags, uint? durationMs)
    {
        if (tags is null)
        {
            return null;
        }

        var metadata = new LocalFileMetadata { DurationMs = durationMs };

        foreach (var tag in tags)
        {
            switch (tag.Value)
            {
                case TagValue.String(var value):
                    metadata = tag.StdKey switch
                    {
                        StandardTagKey.TrackTitle => metadata with { Name = value },
                        StandardTagKey.Language => metadata with { Language = value },
                        StandardTagKey.Artist => metadata with { Artists = value },
                        StandardTagKey.AlbumArtist => metadata with { AlbumArtists = value },
                        StandardTagKey.Album => metadata with { Album = value },
                        StandardTagKey.TrackNumber => metadata with { Number = ParseNumberTag(value, "track") },
                        StandardTagKey.DiscNumber => metadata with { DiscNumber = ParseNumberTag(value, "disc") },
                        _ => metadata,
                    };
                    break;
                case TagValue.UnsignedInt(var value):
                    metadata = tag.StdKey switch
                    {
                        StandardTagKey.TrackNumber => metadata with { Number = unchecked((uint)value) },
                        StandardTagKey.DiscNumber => metadata with { DiscNumber = unchecked((uint)value) },
                        _ => metadata,
                    };
                    break;
                case TagValue.SignedInt(var value):
                    metadata = tag.StdKey switch
                    {
                        StandardTagKey.TrackNumber => metadata with { Number = unchecked((uint)value) },
                        StandardTagKey.DiscNumber => metadata with { DiscNumber = unchecked((uint)value) },
                        _ => metadata,
                    };
                    break;
            }
        }

        return metadata;
    }

    /// <summary>Upstream <c>symphonia_decoder.rs</c> <c>ts_to_ms</c> with a <c>1/sample_rate</c> time base.</summary>
    internal static uint TsToMs(ulong ts, uint sampleRate) =>
        unchecked((uint)(ts * 1000 / sampleRate));

    private static uint? ParseNumberTag(string value, string kind)
    {
        var parsed = DecoderUtil.ParseU32(value, out var error);
        if (parsed is null)
        {
            Log.LogWarning("Failed to parse local file's {Kind} number tag '{Value}': {Error}", kind, value, error);
        }

        return parsed;
    }
}

/// <summary>The default track of a probed stream: latest metadata, frame count and sample rate (when known).</summary>
internal sealed record ProbedTrack(IReadOnlyList<Tag>? Metadata, ulong? NFrames, uint? SampleRate);
