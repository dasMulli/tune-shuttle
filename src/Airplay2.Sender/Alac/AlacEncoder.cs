// Port of reference/alac/codec/ALACEncoder.cpp (Apache-2.0, Copyright (c) 2011 Apple Inc.), 16-bit stereo path only
// (EncodeStereo, EncodeStereoEscape, Encode, InitializeEncoder, GetMagicCookie). Search loops are kept so the output is
// bit-identical to the reference encoder (docs/airplay2/test-vectors/alac-44100-s16-2ch-352.json).
using System.Buffers.Binary;

namespace Airplay2.Sender.Alac;

/// <summary>Element types of the ALAC bitstream.</summary>
public enum AlacElement : byte
{
    Sce = 0,
    Cpe = 1,
    Cce = 2,
    Lfe = 3,
    Dse = 4,
    Pce = 5,
    Fil = 6,
    End = 7,
}

/// <summary>
/// The 24-byte <c>ALACSpecificConfig</c> ("magic cookie"). AirPlay never transmits it: both sides assume
/// 352 frames, 16 bit, 2 channels, 44 100 Hz (SPEC-audio.md 5.2).
/// </summary>
public readonly record struct AlacMagicCookie(uint FrameLength, byte CompatibleVersion, byte BitDepth, byte Pb, byte Mb, byte Kb, byte NumChannels, ushort MaxRun, uint MaxFrameBytes, uint AvgBitRate, uint SampleRate)
{
    /// <summary>Size of the serialised cookie.</summary>
    public const int Size = 24;

    /// <summary>The AirPlay realtime stream cookie: 352 / 16 / 2 / 44100 with the default tuning constants.</summary>
    public static AlacMagicCookie AirPlay { get; } = new(352, 0, 16, (byte)AdaptiveGolomb.Pb0, (byte)AdaptiveGolomb.Mb0, (byte)AdaptiveGolomb.Kb0, 2, (ushort)AdaptiveGolomb.MaxRunDefault, 0, 0, 44100);

    /// <summary>
    /// The receiver's decoder cookie (SPEC-receiver.md 7.1): frame length 352, bit depth 16, 2 channels, 44 100 Hz, the
    /// fmtp defaults of SPEC-audio.md 5.2. Used for realtime packets and buffered ALAC blocks (<c>0x0000FACE</c>) alike;
    /// the same values as <see cref="AirPlay"/>, named for the receiver role.
    /// </summary>
    public static AlacMagicCookie Default44100Stereo16 => AirPlay;

    /// <summary>Serialises big-endian.</summary>
    public byte[] ToBytes()
    {
        var b = new byte[Size];
        BinaryPrimitives.WriteUInt32BigEndian(b, FrameLength);
        b[4] = CompatibleVersion;
        b[5] = BitDepth;
        b[6] = Pb;
        b[7] = Mb;
        b[8] = Kb;
        b[9] = NumChannels;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(10), MaxRun);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), MaxFrameBytes);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), AvgBitRate);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), SampleRate);
        return b;
    }

    /// <summary>Parses a cookie, skipping optional <c>frma</c> and <c>alac</c> atom headers like the reference decoder.</summary>
    public static AlacMagicCookie Parse(ReadOnlySpan<byte> cookie)
    {
        if (cookie.Length >= 12 && cookie[4] == (byte)'f' && cookie[5] == (byte)'r' && cookie[6] == (byte)'m' && cookie[7] == (byte)'a')
        {
            cookie = cookie[12..];
        }

        if (cookie.Length >= 12 && cookie[4] == (byte)'a' && cookie[5] == (byte)'l' && cookie[6] == (byte)'a' && cookie[7] == (byte)'c')
        {
            cookie = cookie[12..];
        }

        if (cookie.Length < Size)
        {
            throw new ArgumentException("ALAC magic cookie is too short.", nameof(cookie));
        }

        return new AlacMagicCookie(
            BinaryPrimitives.ReadUInt32BigEndian(cookie),
            cookie[4], cookie[5], cookie[6], cookie[7], cookie[8], cookie[9],
            BinaryPrimitives.ReadUInt16BigEndian(cookie[10..]),
            BinaryPrimitives.ReadUInt32BigEndian(cookie[12..]),
            BinaryPrimitives.ReadUInt32BigEndian(cookie[16..]),
            BinaryPrimitives.ReadUInt32BigEndian(cookie[20..]));
    }
}

/// <summary>
/// Apple Lossless encoder for 16-bit interleaved stereo. One instance per stream: the predictor coefficients and the
/// last mixRes carry over between frames by design (better compression, and required for the bit-exact vectors).
/// Not thread-safe.
/// </summary>
public sealed class AlacEncoder
{
    private const int MaxSearches = 16;
    private const int MaxCoefs = 16;
    private const int DefaultMixBits = 2;
    private const int DefaultMixRes = 0;
    private const int MaxRes = 4;
    private const int DefaultNumUV = 8;
    private const int MinUV = 4;
    private const int MaxUV = 8;
    private const int BitDepth = 16;
    private const int Channels = 2;

    private readonly int[] _mixBufferU;
    private readonly int[] _mixBufferV;
    private readonly int[] _predictorU;
    private readonly int[] _predictorV;
    private readonly byte[] _workBuffer;
    private readonly byte[] _outputBuffer;
    private readonly short[][] _coefsU = new short[MaxSearches][];
    private readonly short[][] _coefsV = new short[MaxSearches][];
    private int _lastMixRes;
    private uint _maxFrameBytes;

    /// <summary>Creates an encoder with the given frame size (352 for AirPlay) at 44 100 Hz / 16 bit / stereo.</summary>
    public AlacEncoder(int frameSize = 352, int sampleRate = 44100)
    {
        if (frameSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameSize));
        }

        FrameSize = frameSize;
        SampleRate = sampleRate;
        // the maximum output frame size can be no bigger than (samplesPerBlock * numChannels * ((10 + sampleSize)/8) + 1)
        MaxOutputBytes = frameSize * Channels * ((10 + 32) / 8) + 1;
        _mixBufferU = new int[frameSize];
        _mixBufferV = new int[frameSize];
        _predictorU = new int[frameSize];
        _predictorV = new int[frameSize];
        _workBuffer = new byte[MaxOutputBytes + 8];
        _outputBuffer = new byte[MaxOutputBytes + 8];
        for (var search = 0; search < MaxSearches; search++)
        {
            _coefsU[search] = new short[MaxCoefs];
            _coefsV[search] = new short[MaxCoefs];
            Predictor.InitCoefs(_coefsU[search], Predictor.DenShiftDefault, MaxCoefs);
            Predictor.InitCoefs(_coefsV[search], Predictor.DenShiftDefault, MaxCoefs);
        }

        _lastMixRes = DefaultMixRes;
    }

    /// <summary>Frames per packet.</summary>
    public int FrameSize { get; }

    /// <summary>Sample rate written into the magic cookie.</summary>
    public int SampleRate { get; }

    /// <summary>Upper bound of <see cref="Encode(ReadOnlySpan{short}, Span{byte})"/>'s output length.</summary>
    public int MaxOutputBytes { get; }

    /// <summary>The magic cookie for this configuration (<c>GetMagicCookie</c>).</summary>
    public AlacMagicCookie MagicCookie => AlacMagicCookie.AirPlay with { FrameLength = (uint)FrameSize, MaxFrameBytes = _maxFrameBytes, SampleRate = (uint)SampleRate };

    /// <summary>
    /// Encodes one frame of interleaved L/R samples (at most <see cref="FrameSize"/> frames; fewer produce a partial
    /// frame) into <paramref name="output"/> and returns the number of bytes written.
    /// </summary>
    public int Encode(ReadOnlySpan<short> interleavedPcm, Span<byte> output)
    {
        if (interleavedPcm.Length % Channels != 0)
        {
            throw new ArgumentException("Stereo input must have an even number of samples.", nameof(interleavedPcm));
        }

        var numFrames = interleavedPcm.Length / Channels;
        if (numFrames == 0 || numFrames > FrameSize)
        {
            throw new ArgumentException($"Input must hold 1..{FrameSize} frames.", nameof(interleavedPcm));
        }

        var bitstream = new BitBuffer(_outputBuffer, MaxOutputBytes);
        bitstream.Write((uint)AlacElement.Cpe, 3);
        bitstream.Write(0, 4);
        EncodeStereo(ref bitstream, interleavedPcm, Channels, numFrames);
        bitstream.Write((uint)AlacElement.End, 3);
        bitstream.ByteAlign(true);
        var outputSize = (int)(bitstream.Position / 8);
        if (output.Length < outputSize)
        {
            throw new ArgumentException($"Output buffer too small: need {outputSize} bytes.", nameof(output));
        }

        _outputBuffer.AsSpan(0, outputSize).CopyTo(output);
        _maxFrameBytes = Math.Max(_maxFrameBytes, (uint)outputSize);
        return outputSize;
    }

    /// <summary>Allocating variant of <see cref="Encode(ReadOnlySpan{short}, Span{byte})"/>.</summary>
    public byte[] Encode(ReadOnlySpan<short> interleavedPcm)
    {
        var buffer = new byte[MaxOutputBytes];
        var n = Encode(interleavedPcm, buffer);
        return buffer.AsSpan(0, n).ToArray();
    }

    private void EncodeStereo(ref BitBuffer bitstream, ReadOnlySpan<short> input, int stride, int numSamples)
    {
        var startBits = bitstream;
        const uint bytesShifted = 0;
        const uint chanBits = BitDepth - bytesShifted * 8 + 1;
        var partialFrame = numSamples == FrameSize ? 0u : 1u;

        const int mixBits = DefaultMixBits;
        var numU = DefaultNumUV;
        var numV = DefaultNumUV;
        const uint mode = 0;
        const uint pbFactor = 4;
        var dilate = 8;

        var minBits1 = 1u << 31;
        var minBits2 = 1u << 31;
        var bestRes = _lastMixRes;
        var coefsU = _coefsU;
        var coefsV = _coefsV;

        for (var mixRes = 0; mixRes <= MaxRes; mixRes++)
        {
            StereoMatrix.Mix16(input, stride, _mixBufferU, _mixBufferV, numSamples / dilate, mixBits, mixRes);
            var workBits = new BitBuffer(_workBuffer, MaxOutputBytes);

            Predictor.Encode(_mixBufferU, _predictorU, numSamples / dilate, coefsU[numU - 1], numU, chanBits, Predictor.DenShiftDefault);
            Predictor.Encode(_mixBufferV, _predictorV, numSamples / dilate, coefsV[numV - 1], numV, chanBits, Predictor.DenShiftDefault);

            var ag = AgParams.Create(AdaptiveGolomb.Mb0, pbFactor * AdaptiveGolomb.Pb0 / 4, AdaptiveGolomb.Kb0, (uint)(numSamples / dilate), (uint)(numSamples / dilate), AdaptiveGolomb.MaxRunDefault);
            AdaptiveGolomb.Encode(ref ag, _predictorU, ref workBits, numSamples / dilate, (int)chanBits, out var bits1);
            ag = AgParams.Create(AdaptiveGolomb.Mb0, pbFactor * AdaptiveGolomb.Pb0 / 4, AdaptiveGolomb.Kb0, (uint)(numSamples / dilate), (uint)(numSamples / dilate), AdaptiveGolomb.MaxRunDefault);
            AdaptiveGolomb.Encode(ref ag, _predictorV, ref workBits, numSamples / dilate, (int)chanBits, out var bits2);

            if (bits1 + bits2 < minBits1)
            {
                minBits1 = bits1 + bits2;
                bestRes = mixRes;
            }
        }

        _lastMixRes = bestRes;
        var chosenMixRes = _lastMixRes;
        StereoMatrix.Mix16(input, stride, _mixBufferU, _mixBufferV, numSamples, mixBits, chosenMixRes);

        numU = numV = MinUV;
        minBits1 = minBits2 = 1u << 31;
        for (var numUV = MinUV; numUV <= MaxUV; numUV += 4)
        {
            var workBits = new BitBuffer(_workBuffer, MaxOutputBytes);
            dilate = 32;
            for (var converge = 0; converge < 8; converge++)
            {
                Predictor.Encode(_mixBufferU, _predictorU, numSamples / dilate, coefsU[numUV - 1], numUV, chanBits, Predictor.DenShiftDefault);
                Predictor.Encode(_mixBufferV, _predictorV, numSamples / dilate, coefsV[numUV - 1], numUV, chanBits, Predictor.DenShiftDefault);
            }

            dilate = 8;
            var ag = AgParams.Create(AdaptiveGolomb.Mb0, pbFactor * AdaptiveGolomb.Pb0 / 4, AdaptiveGolomb.Kb0, (uint)(numSamples / dilate), (uint)(numSamples / dilate), AdaptiveGolomb.MaxRunDefault);
            AdaptiveGolomb.Encode(ref ag, _predictorU, ref workBits, numSamples / dilate, (int)chanBits, out var bits1);
            if (bits1 * (uint)dilate + 16u * (uint)numUV < minBits1)
            {
                minBits1 = bits1 * (uint)dilate + 16u * (uint)numUV;
                numU = numUV;
            }

            ag = AgParams.Create(AdaptiveGolomb.Mb0, pbFactor * AdaptiveGolomb.Pb0 / 4, AdaptiveGolomb.Kb0, (uint)(numSamples / dilate), (uint)(numSamples / dilate), AdaptiveGolomb.MaxRunDefault);
            AdaptiveGolomb.Encode(ref ag, _predictorV, ref workBits, numSamples / dilate, (int)chanBits, out var bits2);
            if (bits2 * (uint)dilate + 16u * (uint)numUV < minBits2)
            {
                minBits2 = bits2 * (uint)dilate + 16u * (uint)numUV;
                numV = numUV;
            }
        }

        var minBits = minBits1 + minBits2 + 8 * 8 + (partialFrame != 0 ? 32u : 0u);
        var escapeBits = (uint)(numSamples * BitDepth * 2) + (partialFrame != 0 ? 32u : 0u) + 2 * 8;
        var doEscape = minBits >= escapeBits;

        if (!doEscape)
        {
            bitstream.Write(0, 12);
            bitstream.Write((partialFrame << 3) | (bytesShifted << 1), 4);
            if (partialFrame != 0)
            {
                bitstream.Write((uint)numSamples, 32);
            }

            bitstream.Write(mixBits, 8);
            bitstream.Write((uint)chosenMixRes, 8);

            bitstream.Write((mode << 4) | Predictor.DenShiftDefault, 8);
            bitstream.Write((pbFactor << 5) | (uint)numU, 8);
            for (var index = 0; index < numU; index++)
            {
                bitstream.Write((ushort)coefsU[numU - 1][index], 16);
            }

            bitstream.Write((mode << 4) | Predictor.DenShiftDefault, 8);
            bitstream.Write((pbFactor << 5) | (uint)numV, 8);
            for (var index = 0; index < numV; index++)
            {
                bitstream.Write((ushort)coefsV[numV - 1][index], 16);
            }

            Predictor.Encode(_mixBufferU, _predictorU, numSamples, coefsU[numU - 1], numU, chanBits, Predictor.DenShiftDefault);
            var ag = AgParams.Create(AdaptiveGolomb.Mb0, pbFactor * AdaptiveGolomb.Pb0 / 4, AdaptiveGolomb.Kb0, (uint)numSamples, (uint)numSamples, AdaptiveGolomb.MaxRunDefault);
            AdaptiveGolomb.Encode(ref ag, _predictorU, ref bitstream, numSamples, (int)chanBits, out _);

            Predictor.Encode(_mixBufferV, _predictorV, numSamples, coefsV[numV - 1], numV, chanBits, Predictor.DenShiftDefault);
            ag = AgParams.Create(AdaptiveGolomb.Mb0, pbFactor * AdaptiveGolomb.Pb0 / 4, AdaptiveGolomb.Kb0, (uint)numSamples, (uint)numSamples, AdaptiveGolomb.MaxRunDefault);
            AdaptiveGolomb.Encode(ref ag, _predictorV, ref bitstream, numSamples, (int)chanBits, out _);

            minBits = bitstream.Position - startBits.Position;
            if (minBits >= escapeBits)
            {
                bitstream = startBits;
                doEscape = true;
            }
        }

        if (doEscape)
        {
            EncodeStereoEscape(ref bitstream, input, stride, numSamples);
        }
    }

    private void EncodeStereoEscape(ref BitBuffer bitstream, ReadOnlySpan<short> input, int stride, int numSamples)
    {
        var partialFrame = numSamples == FrameSize ? 0u : 1u;
        bitstream.Write(0, 12);
        bitstream.Write((partialFrame << 3) | 1, 4);
        if (partialFrame != 0)
        {
            bitstream.Write((uint)numSamples, 32);
        }

        for (var index = 0; index < numSamples * stride; index += stride)
        {
            bitstream.Write((ushort)input[index], 16);
            bitstream.Write((ushort)input[index + 1], 16);
        }
    }
}
