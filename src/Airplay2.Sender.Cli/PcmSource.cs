using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using Airplay2.Sender.Playback;

namespace Airplay2.Sender.Cli;

/// <summary>Interleaved 16-bit stereo PCM at 44.1 kHz for <c>play</c>: a WAV file or a generated tone.</summary>
public interface IPcmSource : IDisposable
{
    /// <summary>A display title (file name or "test tone").</summary>
    string Title { get; }

    /// <summary>Total frames, or -1 when unknown (a WAV written by a stream with an open-ended data chunk).</summary>
    long TotalFrames { get; }

    /// <summary>Reads up to <c>buffer.Length / 2</c> frames; returns the frames read, 0 at the end.</summary>
    int Read(Span<short> buffer);
}

/// <summary>Opens <c>play</c>'s source argument.</summary>
public static class PcmSource
{
    /// <summary>The <c>tone:&lt;seconds&gt;</c> prefix.</summary>
    public const string TonePrefix = "tone:";

    /// <summary>Longest generated tone (a guard against typos such as <c>tone:300</c>).</summary>
    public static readonly TimeSpan MaxTone = TimeSpan.FromMinutes(10);

    /// <summary>
    /// <c>tone:&lt;s&gt;</c>: a 440 Hz sine at -24 dBFS with 10 ms fades (<see cref="PlaybackProbes.Tone"/>, the quiet test tone of
    /// docs/DEVELOPMENT.md, "Live-device rules"); anything else is a WAV path (<see cref="WavReader"/>).
    /// </summary>
    /// <exception cref="FormatException">The tone length or the WAV format is invalid.</exception>
    /// <exception cref="FileNotFoundException">The WAV file does not exist.</exception>
    public static IPcmSource Open(string spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);
        if (spec.StartsWith(TonePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var text = spec[TonePrefix.Length..];
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds) || seconds <= 0 || seconds > MaxTone.TotalSeconds)
            {
                throw new FormatException($"'{spec}': the tone length must be a number of seconds between 0 and {MaxTone.TotalSeconds:0} (e.g. tone:3).");
            }

            var pcm = PlaybackProbes.Tone(TimeSpan.FromSeconds(seconds));
            return new ArrayPcmSource(string.Create(CultureInfo.InvariantCulture, $"test tone {seconds:0.###} s"), pcm);
        }

        return WavReader.Open(spec);
    }
}

/// <summary>PCM from memory.</summary>
public sealed class ArrayPcmSource(string title, short[] interleaved) : IPcmSource
{
    private int _position;

    /// <inheritdoc />
    public string Title { get; } = title;

    /// <inheritdoc />
    public long TotalFrames => interleaved.Length / 2;

    /// <inheritdoc />
    public int Read(Span<short> buffer)
    {
        var samples = Math.Min(buffer.Length & ~1, interleaved.Length - _position);
        interleaved.AsSpan(_position, samples).CopyTo(buffer);
        _position += samples;
        return samples / 2;
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

/// <summary>
/// A RIFF/WAVE reader for exactly what the sender streams (librespot's output: 16-bit, 44.1 kHz, stereo, little-endian PCM):
/// <c>WAVE_FORMAT_PCM</c> or <c>WAVE_FORMAT_EXTENSIBLE</c> with the PCM sub-format; unknown chunks (<c>LIST</c>,
/// <c>fact</c>, ...) are skipped with their pad byte; a data size of 0 or 0xFFFFFFFF (streamed WAVs) reads to the end of
/// the file. Anything else is refused with a message naming the format found; resampling is not this tool's job.
/// </summary>
public sealed class WavReader : IPcmSource
{
    private const ushort FormatPcm = 1;
    private const ushort FormatExtensible = 0xFFFE;
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");

    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[4 * 4096];
    private long _remainingBytes;
    private int _carry;

    private WavReader(Stream stream, string title, long dataBytes)
    {
        _stream = stream;
        Title = title;
        _remainingBytes = dataBytes;
        TotalFrames = dataBytes < 0 ? -1 : dataBytes / 4;
    }

    /// <inheritdoc />
    public string Title { get; }

    /// <inheritdoc />
    public long TotalFrames { get; }

    /// <summary>Opens a WAV file.</summary>
    public static WavReader Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        try
        {
            return Open(stream, Path.GetFileNameWithoutExtension(path));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Reads the header from <paramref name="stream"/> (owned from now on) and positions it at the samples.</summary>
    /// <exception cref="FormatException">Not a RIFF/WAVE file, or not 16-bit 44.1 kHz stereo PCM.</exception>
    public static WavReader Open(Stream stream, string title)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> header = stackalloc byte[12];
        if (!TryReadExactly(stream, header) || !header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8))
        {
            throw new FormatException("Not a RIFF/WAVE file.");
        }

        var haveFormat = false;
        Span<byte> chunk = stackalloc byte[8];
        while (TryReadExactly(stream, chunk))
        {
            var id = chunk[..4];
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (id.SequenceEqual("fmt "u8))
            {
                if (size is < 16 or > 1024)
                {
                    throw new FormatException($"Invalid fmt chunk size {size}.");
                }

                var fmt = new byte[size];
                if (!TryReadExactly(stream, fmt))
                {
                    throw new FormatException("Truncated fmt chunk.");
                }

                CheckFormat(fmt);
                haveFormat = true;
                SkipPad(stream, size);
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (!haveFormat)
                {
                    throw new FormatException("The data chunk comes before the fmt chunk.");
                }

                long data = size is 0 or uint.MaxValue ? -1 : size;
                return new WavReader(stream, title, data);
            }
            else
            {
                Skip(stream, size + (size & 1));
            }
        }

        throw new FormatException(haveFormat ? "No data chunk." : "No fmt chunk.");
    }

    /// <summary>Writes interleaved 16-bit stereo 44.1 kHz PCM as a canonical 44-byte-header WAV (tests, tools).</summary>
    public static void Write(Stream destination, ReadOnlySpan<short> interleaved)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var dataBytes = interleaved.Length * 2;
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(36 + dataBytes));
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], FormatPcm);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], 44100);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], 44100 * 4);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 4);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)dataBytes);
        destination.Write(header);
        var bytes = new byte[dataBytes];
        for (var i = 0; i < interleaved.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), interleaved[i]);
        }

        destination.Write(bytes);
    }

    /// <inheritdoc />
    public int Read(Span<short> buffer)
    {
        var frames = 0;
        var wanted = buffer.Length / 2;
        while (frames < wanted && _remainingBytes != 0)
        {
            var maxBytes = Math.Min((wanted - frames) * 4, _buffer.Length) - _carry;
            if (_remainingBytes > 0)
            {
                maxBytes = (int)Math.Min(maxBytes, _remainingBytes);
            }

            var n = _stream.Read(_buffer, _carry, maxBytes);
            if (n == 0)
            {
                _remainingBytes = 0; // truncated or open-ended file: stop at the last whole frame
                break;
            }

            if (_remainingBytes > 0)
            {
                _remainingBytes -= n;
            }

            var available = _carry + n;
            var whole = available / 4;
            var bytes = _buffer.AsSpan(0, whole * 4);
            var target = buffer.Slice(frames * 2, whole * 2);
            if (BitConverter.IsLittleEndian)
            {
                MemoryMarshal.Cast<byte, short>(bytes).CopyTo(target);
            }
            else
            {
                for (var i = 0; i < target.Length; i++)
                {
                    target[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes[(i * 2)..]);
                }
            }

            _carry = available - (whole * 4);
            if (_carry > 0)
            {
                _buffer.AsSpan(whole * 4, _carry).CopyTo(_buffer);
            }

            frames += whole;
        }

        return frames;
    }

    /// <inheritdoc />
    public void Dispose() => _stream.Dispose();

    private static void CheckFormat(ReadOnlySpan<byte> fmt)
    {
        var tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
        var rate = BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
        if (tag == FormatExtensible)
        {
            if (fmt.Length < 40 || new Guid(fmt.Slice(24, 16)) != PcmSubFormat)
            {
                throw new FormatException("WAVE_FORMAT_EXTENSIBLE with a sub-format other than PCM is not supported.");
            }
        }
        else if (tag != FormatPcm)
        {
            throw new FormatException($"WAV format tag {tag} is not PCM; only 16-bit 44.1 kHz stereo PCM is supported.");
        }

        if (channels != 2 || rate != 44100 || bits != 16)
        {
            throw new FormatException($"The WAV is {bits}-bit {rate} Hz with {channels} channel(s); only 16-bit 44100 Hz stereo is supported (convert it first, e.g. with ffmpeg -ar 44100 -ac 2 -sample_fmt s16).");
        }
    }

    private static bool TryReadExactly(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer[read..]);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    private static void SkipPad(Stream stream, uint size)
    {
        if ((size & 1) != 0)
        {
            Skip(stream, 1);
        }
    }

    private static void Skip(Stream stream, long bytes)
    {
        if (stream.CanSeek)
        {
            stream.Seek(bytes, SeekOrigin.Current);
            return;
        }

        Span<byte> scratch = stackalloc byte[512];
        while (bytes > 0)
        {
            var n = stream.Read(scratch[..(int)Math.Min(scratch.Length, bytes)]);
            if (n == 0)
            {
                return;
            }

            bytes -= n;
        }
    }
}
