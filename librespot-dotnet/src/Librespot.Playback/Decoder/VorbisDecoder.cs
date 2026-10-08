// Port of playback/src/decoder/symphonia_decoder.rs, part 2: SymphoniaDecoder for Ogg Vorbis, on top of NVorbis
// (symphonia's OggReader + VorbisDecoder). Deviations, all forced by NVorbis' API:
//  * NVorbis decodes samples, not packets. Each NextPacket() returns up to SamplesPerSecond / 10 interleaved
//    samples (100 ms) instead of one Vorbis packet; the position is the sample position of the first sample.
//  * NVorbis seeks sample-accurately, so Seek() returns the requested (saturated) position, where symphonia returns
//    the timestamp of the packet it landed on.
using Librespot.Core;
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;
using NVorbis;

namespace Librespot.Playback.Decoder;

/// <summary>Decodes Ogg Vorbis to interleaved stereo f64 at 44.1 kHz (upstream <c>SymphoniaDecoder</c> for Vorbis).</summary>
public sealed class VorbisDecoder : IAudioDecoder
{
    /// <summary>Interleaved samples returned per <see cref="NextPacket"/>.</summary>
    internal const int SamplesPerPacket = (int)(AudioConstants.SamplesPerSecond / 10);

    private static readonly ILogger Log = LibrespotLog.CreateLogger<VorbisDecoder>();

    private readonly VorbisReader _reader;
    private readonly ProbeResult _probeResult;
    private readonly uint _sampleRate;
    private readonly float[] _buffer = new float[SamplesPerPacket];
    private bool _disposed;

    /// <summary>Opens an Ogg Vorbis stream. The decoder owns <paramref name="input"/>.</summary>
    /// <exception cref="DecoderException">The stream is not Ogg Vorbis, or not 44.1 kHz stereo.</exception>
    public VorbisDecoder(Stream input)
        : this(input, new ProbeResult(ProbedFormat.Ogg, null))
    {
    }

    internal VorbisDecoder(Stream input, ProbeResult probeResult)
    {
        _probeResult = probeResult;
        _reader = Open(input);

        try
        {
            var rate = (uint)_reader.SampleRate;

            // Upstream note (open issue): The official client supports local files with sample rates other than 44,100 kHz.
            // To play these accurately, we need to either resample the input audio, or introduce a way
            // to change the player's current sample rate (likely by closing and re-opening the sink
            // with new parameters).
            if (rate != AudioConstants.SampleRate)
            {
                throw new DecoderException(DecoderException.Reason.AudioDecoder, $"Unsupported sample rate: {rate}");
            }

            var channels = _reader.Channels;
            if (channels != AudioConstants.NumChannels)
            {
                throw new DecoderException(
                    DecoderException.Reason.AudioDecoder,
                    $"Unsupported number of channels: {channels}");
            }

            _sampleRate = rate;

            // symphonia's decoder does not clip f64 output; NVorbis clips to [-1, 1] by default.
            _reader.ClipSamples = false;
        }
        catch
        {
            _reader.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public uint Seek(uint positionMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            // "Saturate" the position_ms to the duration of the track if it exceeds it.
            var targetTs = (ulong)positionMs * _sampleRate / 1000;
            var nFrames = _reader.TotalSamples;
            if (nFrames >= 0 && targetTs > (ulong)nFrames)
            {
                targetTs = (ulong)nFrames;
            }

            SeekTo((long)targetTs);

            // Seeking resets NVorbis' decoder state, so there is nothing else to reset (upstream: decoder.reset()).
            return AudioDecoders.TsToMs((ulong)_reader.SamplePosition, _sampleRate);
        }
        catch (Exception e) when (e is not LibrespotException)
        {
            throw new DecoderException(DecoderException.Reason.AudioDecoder, e.Message, e);
        }
    }

    /// <summary>
    /// Seeks NVorbis to <paramref name="targetTs"/>. NVorbis cannot seek to the very end of a stream, nor into a final
    /// page whose granule position is end-trimmed (it reports a granule mismatch); in those cases seek to an earlier
    /// position and decode forward to the target, which is what symphonia's accurate seek does internally.
    /// </summary>
    private void SeekTo(long targetTs)
    {
        try
        {
            _reader.SeekTo(targetTs, SeekOrigin.Begin);
            return;
        }
        catch (InvalidDataException e) when (targetTs > 0)
        {
            Log.LogTrace("Direct seek to {TargetTs} failed ({Error}), seeking earlier and decoding forward", targetTs, e.Message);
        }

        var step = (long)_sampleRate / 2;
        for (var fallback = Math.Max(0, targetTs - step); ; fallback = Math.Max(0, fallback - step))
        {
            try
            {
                _reader.SeekTo(fallback, SeekOrigin.Begin);
                break;
            }
            catch (InvalidDataException) when (fallback > 0)
            {
            }
        }

        while (_reader.SamplePosition < targetTs)
        {
            var remaining = (targetTs - _reader.SamplePosition) * _reader.Channels;
            var read = _reader.ReadSamples(_buffer, 0, (int)Math.Min(_buffer.Length, remaining));
            if (read == 0)
            {
                break;
            }
        }
    }

    /// <inheritdoc />
    public (AudioPacketPosition Position, AudioPacket Packet)? NextPacket()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var skipped = false;

        while (true)
        {
            var ts = _reader.SamplePosition;
            var positionMs = AudioDecoders.TsToMs((ulong)ts, _sampleRate);
            var packetPosition = new AudioPacketPosition(positionMs, skipped);

            int read;
            try
            {
                read = _reader.ReadSamples(_buffer, 0, _buffer.Length);
            }
            catch (EndOfStreamException)
            {
                // Upstream: io::ErrorKind::UnexpectedEof ends the stream.
                return null;
            }
            catch (InvalidDataException) when (_reader.SamplePosition != ts)
            {
                // The packet failed to decode due to corrupted or invalid data, get a new packet and try again.
                // (Deviation: only retried when the reader made progress, so a stuck reader cannot loop forever.)
                Log.LogWarning("Skipping malformed audio packet at {PositionMs} ms", positionMs);
                skipped = true;
                continue;
            }
            catch (Exception e) when (e is not LibrespotException)
            {
                throw new DecoderException(DecoderException.Reason.AudioDecoder, e.Message, e);
            }

            if (read == 0)
            {
                return null;
            }

            var samples = new double[read];
            for (var i = 0; i < read; i++)
            {
                samples[i] = _buffer[i];
            }

            return (packetPosition, new AudioPacket.Samples(samples));
        }
    }

    /// <inheritdoc />
    public NormalisationData? GetNormalisationData() =>
        AudioDecoders.NormalisationDataFromTags(DecoderUtil.GetLatestMetadata(ContainerMetadata(_reader), _probeResult));

    /// <inheritdoc />
    public LocalFileMetadata? GetLocalFileMetadata()
    {
        var nFrames = _reader.TotalSamples;
        uint? durationMs = nFrames >= 0 ? AudioDecoders.TsToMs((ulong)nFrames, _sampleRate) : null;
        return AudioDecoders.LocalFileMetadataFromTags(
            DecoderUtil.GetLatestMetadata(ContainerMetadata(_reader), _probeResult),
            durationMs);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _reader.Dispose();
        }
    }

    /// <summary>Opens the stream's default track without format checks, for the local file probe.</summary>
    internal static ProbedTrack ProbeTrack(Stream input, ProbeResult probeResult)
    {
        using var reader = Open(input);
        var nFrames = reader.TotalSamples;
        return new ProbedTrack(
            DecoderUtil.GetLatestMetadata(ContainerMetadata(reader), probeResult),
            nFrames >= 0 ? (ulong)nFrames : null,
            reader.SampleRate > 0 ? (uint)reader.SampleRate : null);
    }

    private static VorbisReader Open(Stream input)
    {
        try
        {
            return new VorbisReader(input, closeOnDispose: true);
        }
        catch (Exception e) when (e is not LibrespotException)
        {
            input.Dispose();
            throw new DecoderException(DecoderException.Reason.AudioDecoder, e.Message, e);
        }
    }

    /// <summary>
    /// The Vorbis comments as tags (symphonia's Ogg reader metadata). Keys are matched case-insensitively with
    /// symphonia's Vorbis comment key table; values stay strings.
    /// </summary>
    private static List<Tag> ContainerMetadata(VorbisReader reader)
    {
        var tags = new List<Tag>();
        foreach (var (key, values) in reader.Tags.All)
        {
            var stdKey = VorbisCommentStdKey(key);
            foreach (var value in values)
            {
                tags.Add(new Tag(stdKey, key, new TagValue.String(value)));
            }
        }

        return tags;
    }

    private static StandardTagKey? VorbisCommentStdKey(string key) => key.ToLowerInvariant() switch
    {
        "album artist" or "albumartist" => StandardTagKey.AlbumArtist,
        "album" => StandardTagKey.Album,
        "artist" => StandardTagKey.Artist,
        "discnumber" => StandardTagKey.DiscNumber,
        "language" => StandardTagKey.Language,
        "replaygain_album_gain" => StandardTagKey.ReplayGainAlbumGain,
        "replaygain_album_peak" => StandardTagKey.ReplayGainAlbumPeak,
        "replaygain_track_gain" => StandardTagKey.ReplayGainTrackGain,
        "replaygain_track_peak" => StandardTagKey.ReplayGainTrackPeak,
        "title" => StandardTagKey.TrackTitle,
        "tracknumber" => StandardTagKey.TrackNumber,
        _ => null,
    };
}
