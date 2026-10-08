// Port of playback/src/decoder/mod.rs. The decoders (symphonia_decoder.rs -> NVorbis/NLayer based
// VorbisDecoder/Mp3Decoder, passthrough_decoder.rs) belong to package `playback-pipeline`.
using Librespot.Core;

namespace Librespot.Playback.Decoder;

/// <summary>Upstream <c>DecoderError</c>; converted to a librespot error with kind Aborted.</summary>
public sealed class DecoderException : LibrespotException
{
    public enum Reason
    {
        PassthroughDecoder,
        /// <summary>Upstream: SymphoniaDecoder. Here: the NVorbis/NLayer-based decoders.</summary>
        AudioDecoder,
    }

    public Reason Cause { get; }

    public DecoderException(Reason cause, string detail, Exception? inner = null)
        : base(ErrorKind.Aborted, cause == Reason.PassthroughDecoder ? $"Passthrough Decoder Error: {detail}" : $"Audio Decoder Error: {detail}", inner)
    {
        Cause = cause;
    }
}

/// <summary>Upstream <c>AudioPacketError</c>.</summary>
public sealed class AudioPacketException(string detail) : LibrespotException(ErrorKind.FailedPrecondition, detail);

/// <summary>Decoded samples (interleaved stereo f64) or raw passthrough bytes.</summary>
public abstract class AudioPacket
{
    private AudioPacket()
    {
    }

    public sealed class Samples(double[] samples) : AudioPacket
    {
        public double[] Data { get; } = samples;
    }

    public sealed class Raw(byte[] bytes) : AudioPacket
    {
        public byte[] Data { get; } = bytes;
    }

    /// <summary>The samples; throws when this is a raw packet (upstream <c>AudioPacketError::Raw</c>).</summary>
    public double[] SamplesOrThrow() => this is Samples s ? s.Data : throw new AudioPacketException("Decoder Raw Error: Can't return Raw on Samples");

    /// <summary>The raw bytes; throws when this is a samples packet (upstream <c>AudioPacketError::Samples</c>).</summary>
    public byte[] RawOrThrow() => this is Raw r ? r.Data : throw new AudioPacketException("Decoder Samples Error: Can't return Samples on Raw");

    public bool IsEmpty => this switch
    {
        Samples s => s.Data.Length == 0,
        Raw r => r.Data.Length == 0,
        _ => true,
    };
}

/// <summary>Position of a decoded packet and whether packets before it were skipped (after a seek).</summary>
public readonly record struct AudioPacketPosition(uint PositionMs, bool Skipped);

/// <summary>Upstream <c>trait AudioDecoder</c>.</summary>
public interface IAudioDecoder : IDisposable
{
    /// <summary>Seeks to the position and returns the actual position reached, in ms.</summary>
    uint Seek(uint positionMs);

    /// <summary>The next packet, or null at end of stream.</summary>
    (AudioPacketPosition Position, AudioPacket Packet)? NextPacket();
}
