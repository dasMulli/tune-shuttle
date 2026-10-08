// Port of playback/src/decoder/symphonia_decoder.rs, part 3: SymphoniaDecoder for MPEG audio, on top of NLayer
// (symphonia's MpaReader + MpaDecoder). Deviations, all forced by NLayer's API:
//  * NLayer decodes samples, not packets. Each NextPacket() returns up to SamplesPerSecond / 10 interleaved
//    samples (100 ms); the position is the sample position of the first sample.
//  * NLayer has no gapless support: the encoder delay/padding of the LAME tag is not trimmed (symphonia trims it
//    with enable_gapless), so positions and the frame count include the delay.
//  * Tags come from the ID3v2 tag in front of the stream (symphonia's probe metadata); NLayer skips that tag itself.
using Librespot.Core;
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;
using NLayer;

namespace Librespot.Playback.Decoder;

/// <summary>Decodes MPEG audio (MP3) to interleaved stereo f64 at 44.1 kHz (upstream <c>SymphoniaDecoder</c> for MP3).</summary>
public sealed class Mp3Decoder : IAudioDecoder
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<Mp3Decoder>();

    private readonly Stream _input;
    private readonly MpegFile _mpeg;
    private readonly ProbeResult _probeResult;
    private readonly uint _sampleRate;
    private readonly int _channels;
    private readonly float[] _buffer = new float[VorbisDecoder.SamplesPerPacket];
    private bool _disposed;

    /// <summary>Opens an MPEG audio stream. The decoder owns <paramref name="input"/>.</summary>
    /// <exception cref="DecoderException">The stream is not MPEG audio, or not 44.1 kHz stereo.</exception>
    public Mp3Decoder(Stream input)
        : this(input, ProbeId3(input))
    {
    }

    internal Mp3Decoder(Stream input, ProbeResult probeResult)
    {
        _input = input;
        _probeResult = probeResult;
        _mpeg = Open(input);

        try
        {
            var rate = (uint)_mpeg.SampleRate;

            // Upstream note (open issue): The official client supports local files with sample rates other than 44,100 kHz.
            // To play these accurately, we need to either resample the input audio, or introduce a way
            // to change the player's current sample rate (likely by closing and re-opening the sink
            // with new parameters).
            if (rate != AudioConstants.SampleRate)
            {
                throw new DecoderException(DecoderException.Reason.AudioDecoder, $"Unsupported sample rate: {rate}");
            }

            var channels = _mpeg.Channels;
            if (channels != AudioConstants.NumChannels)
            {
                throw new DecoderException(
                    DecoderException.Reason.AudioDecoder,
                    $"Unsupported number of channels: {channels}");
            }

            _sampleRate = rate;
            _channels = channels;
        }
        catch
        {
            _mpeg.Dispose();
            input.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public uint Seek(uint positionMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var targetTs = (ulong)positionMs * _sampleRate / 1000;

            // "Saturate" the position_ms to the duration of the track if it exceeds it.
            // Deviation: upstream saturates up front with n_frames from the Xing header or symphonia's
            // estimate_num_mpeg_frames. NLayer only knows the frame count after parsing every frame of the
            // stream, which for a streamed MP3 would mean downloading the whole file on the first seek. So the
            // seek is tried first: NLayer reads frames only up to the target, and throws
            // ArgumentOutOfRangeException when it runs off the end. Only then is the stream already scanned
            // to its end and the frame count cheap, so the target is saturated and the seek retried.
            try
            {
                _mpeg.Position = (long)targetTs * sizeof(float) * _channels;
            }
            catch (ArgumentOutOfRangeException) when (FrameCount(_mpeg) is { } nFrames && targetTs > nFrames)
            {
                _mpeg.Position = (long)FrameCount(_mpeg)!.Value * sizeof(float) * _channels;
            }
            return AudioDecoders.TsToMs(CurrentTs(), _sampleRate);
        }
        catch (Exception e) when (e is not LibrespotException)
        {
            throw new DecoderException(DecoderException.Reason.AudioDecoder, e.Message, e);
        }
    }

    /// <inheritdoc />
    public (AudioPacketPosition Position, AudioPacket Packet)? NextPacket()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var skipped = false;

        while (true)
        {
            var ts = CurrentTs();
            var positionMs = AudioDecoders.TsToMs(ts, _sampleRate);
            var packetPosition = new AudioPacketPosition(positionMs, skipped);

            int read;
            try
            {
                read = _mpeg.ReadSamples(_buffer, 0, _buffer.Length);
            }
            catch (EndOfStreamException)
            {
                // Upstream: io::ErrorKind::UnexpectedEof ends the stream.
                return null;
            }
            catch (InvalidDataException) when (CurrentTs() != ts)
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
        AudioDecoders.NormalisationDataFromTags(DecoderUtil.GetLatestMetadata(null, _probeResult));

    /// <inheritdoc />
    public LocalFileMetadata? GetLocalFileMetadata()
    {
        uint? durationMs = FrameCount(_mpeg) is { } nFrames ? AudioDecoders.TsToMs(nFrames, _sampleRate) : null;
        return AudioDecoders.LocalFileMetadataFromTags(DecoderUtil.GetLatestMetadata(null, _probeResult), durationMs);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _mpeg.Dispose();
            _input.Dispose();
        }
    }

    /// <summary>Opens the stream's default track without format checks, for the local file probe.</summary>
    internal static ProbedTrack ProbeTrack(Stream input, ProbeResult probeResult)
    {
        using var mpeg = Open(input);
        return new ProbedTrack(
            DecoderUtil.GetLatestMetadata(null, probeResult),
            FrameCount(mpeg),
            mpeg.SampleRate > 0 ? (uint)mpeg.SampleRate : null);
    }

    private ulong CurrentTs() => (ulong)Math.Max(0, _mpeg.Position) / (ulong)(sizeof(float) * _channels);

    /// <summary>The total number of audio frames (samples per channel), if NLayer can determine it.</summary>
    private static ulong? FrameCount(MpegFile mpeg)
    {
        var length = mpeg.Length;
        var channels = mpeg.Channels;
        return length >= 0 && channels > 0 ? (ulong)length / (ulong)(sizeof(float) * channels) : null;
    }

    private static MpegFile Open(Stream input)
    {
        try
        {
            return new MpegFile(input);
        }
        catch (Exception e) when (e is not LibrespotException)
        {
            input.Dispose();
            throw new DecoderException(DecoderException.Reason.AudioDecoder, e.Message, e);
        }
    }

    private static ProbeResult ProbeId3(Stream input)
    {
        var probe = DecoderUtil.Probe(input, new DecoderHint(MimeType: "audio/mpeg"));
        return probe with { Format = ProbedFormat.Mpa };
    }
}
