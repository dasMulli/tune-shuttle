// Port of playback/src/decoder/mod.rs. The decoders live next to it: symphonia_decoder.rs -> AudioDecoders.cs
// (probe + facade) with the NVorbis/NLayer based VorbisDecoder.cs/Mp3Decoder.cs, passthrough_decoder.rs ->
// PassthroughDecoder.cs.
using System.Diagnostics;
using Librespot.Core;

namespace Librespot.Playback.Decoder;

/// <summary>Upstream <c>DecoderError</c>; converted to a librespot error with kind Aborted.</summary>
public sealed class DecoderException : LibrespotException
{
    /// <summary>The <c>DecoderError</c> variants.</summary>
    public enum Reason
    {
        /// <summary>Upstream <c>PassthroughDecoder(String)</c>.</summary>
        PassthroughDecoder,
        /// <summary>Upstream: SymphoniaDecoder. Here: the NVorbis/NLayer-based decoders.</summary>
        AudioDecoder,
    }

    /// <summary>The variant.</summary>
    public Reason Cause { get; }

    /// <summary>Creates the error; the message renders like upstream's <c>#[error("...")]</c> text.</summary>
    /// <remarks>
    /// <see cref="Reason.AudioDecoder"/> keeps upstream's "Symphonia Decoder Error" text although symphonia is replaced
    /// by NVorbis/NLayer, so log lines match upstream's.
    /// </remarks>
    public DecoderException(Reason cause, string detail, Exception? inner = null)
        : base(ErrorKind.Aborted, cause == Reason.PassthroughDecoder ? $"Passthrough Decoder Error: {detail}" : $"Symphonia Decoder Error: {detail}", inner)
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

    /// <summary>Decoded interleaved samples (upstream <c>AudioPacket::Samples(Vec&lt;f64&gt;)</c>).</summary>
    public sealed class Samples(double[] samples) : AudioPacket
    {
        /// <summary>The interleaved samples.</summary>
        public double[] Data { get; } = samples;
    }

    /// <summary>Raw (passthrough) bytes (upstream <c>AudioPacket::Raw(Vec&lt;u8&gt;)</c>).</summary>
    public sealed class Raw(byte[] bytes) : AudioPacket
    {
        /// <summary>The raw bytes.</summary>
        public byte[] Data { get; } = bytes;
    }

    /// <summary>The samples; throws when this is a raw packet (upstream <c>AudioPacketError::Raw</c>).</summary>
    public double[] SamplesOrThrow() => this is Samples s ? s.Data : throw new AudioPacketException("Decoder Raw Error: Can't return Raw on Samples");

    /// <summary>The raw bytes; throws when this is a samples packet (upstream <c>AudioPacketError::Samples</c>).</summary>
    public byte[] RawOrThrow() => this is Raw r ? r.Data : throw new AudioPacketException("Decoder Samples Error: Can't return Samples on Raw");

    /// <summary>Upstream <c>is_empty</c>.</summary>
    public bool IsEmpty => this switch
    {
        Samples s => s.Data.Length == 0,
        Raw r => r.Data.Length == 0,
        _ => throw new UnreachableException(),
    };
}

/// <summary>
/// Position of a decoded packet, and whether malformed packets were skipped before it (upstream
/// <c>AudioPacketPosition</c>).
/// </summary>
public readonly record struct AudioPacketPosition(uint PositionMs, bool Skipped)
{
    /// <summary>Upstream <c>Deref&lt;Target = u32&gt;</c>: the position in ms.</summary>
    public static implicit operator uint(AudioPacketPosition position) => position.PositionMs;
}

/// <summary>Upstream <c>trait AudioDecoder</c>.</summary>
/// <remarks>
/// Upstream's <c>SymphoniaDecoder</c> additionally has inherent <c>normalisation_data()</c> and
/// <c>local_file_metadata()</c> methods that the player calls on the concrete type; here they are default
/// interface members so the player can keep holding an <see cref="IAudioDecoder"/>.
/// </remarks>
public interface IAudioDecoder : IDisposable
{
    /// <summary>Seeks to the position and returns the actual position reached, in ms.</summary>
    uint Seek(uint positionMs);

    /// <summary>The next packet, or null at end of stream.</summary>
    (AudioPacketPosition Position, AudioPacket Packet)? NextPacket();

    /// <summary>
    /// ReplayGain data from the file's tags, or null when the file has no tags (upstream
    /// <c>SymphoniaDecoder::normalisation_data</c>).
    /// </summary>
    NormalisationData? GetNormalisationData() => null;

    /// <summary>Tag metadata of a local file (upstream <c>SymphoniaDecoder::local_file_metadata</c>).</summary>
    LocalFileMetadata? GetLocalFileMetadata() => null;
}
