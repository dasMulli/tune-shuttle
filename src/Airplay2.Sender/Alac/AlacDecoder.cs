// Port of reference/alac/codec/ALACDecoder.cpp (Apache-2.0, Copyright (c) 2011 Apple Inc.), 16-bit path only.
// Used by the tests (round trip), the fake receiver and diagnostics; the sender itself never decodes.
namespace Airplay2.Sender.Alac;

/// <summary>Apple Lossless decoder for 16-bit mono/stereo frames produced with a known magic cookie.</summary>
public sealed class AlacDecoder
{
    private readonly int[] _mixBufferU;
    private readonly int[] _mixBufferV;
    private readonly int[] _predictor;
    private readonly short[] _coefsU = new short[32];
    private readonly short[] _coefsV = new short[32];

    /// <summary>Creates a decoder for the given configuration.</summary>
    public AlacDecoder(AlacMagicCookie config)
    {
        if (config.BitDepth != 16)
        {
            throw new NotSupportedException("Only 16-bit ALAC is ported.");
        }

        if (config.CompatibleVersion > 0)
        {
            throw new NotSupportedException("Unsupported ALAC compatible version.");
        }

        Config = config;
        _mixBufferU = new int[config.FrameLength];
        _mixBufferV = new int[config.FrameLength];
        _predictor = new int[config.FrameLength];
    }

    /// <summary>Creates a decoder from a serialised magic cookie.</summary>
    public AlacDecoder(ReadOnlySpan<byte> magicCookie)
        : this(AlacMagicCookie.Parse(magicCookie))
    {
    }

    /// <summary>The decoding parameters.</summary>
    public AlacMagicCookie Config { get; }

    /// <summary>
    /// Decodes one frame into interleaved 16-bit samples. <paramref name="numChannels"/> selects the output layout
    /// (2 for the AirPlay stream). Returns the number of frames decoded, or -1 on a malformed frame.
    /// </summary>
    public int Decode(ReadOnlySpan<byte> frame, Span<short> output, int numChannels)
    {
        var bits = new BitBuffer(frame.ToArray(), frame.Length);
        return Decode(ref bits, output, (int)Config.FrameLength, numChannels);
    }

    /// <summary>Decodes one frame from a bit buffer (<c>ALACDecoder::Decode</c>).</summary>
    public int Decode(ref BitBuffer bits, Span<short> output, int numSamples, int numChannels)
    {
        if (numChannels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numChannels));
        }

        var channelIndex = 0;
        var outNumSamples = numSamples;

        while (true)
        {
            if (!bits.HasMore)
            {
                return -1;
            }

            uint pb = Config.Pb;
            var tag = (AlacElement)bits.ReadSmall(3);
            switch (tag)
            {
                case AlacElement.Sce:
                case AlacElement.Lfe:
                {
                    bits.ReadSmall(4); // element instance tag
                    var unusedHeader = bits.Read(12);
                    if (unusedHeader != 0)
                    {
                        return -1;
                    }

                    var headerByte = bits.Read(4);
                    var partialFrame = headerByte >> 3;
                    var bytesShifted = (headerByte >> 1) & 0x3;
                    if (bytesShifted == 3)
                    {
                        return -1;
                    }

                    var escapeFlag = headerByte & 0x1;
                    var chanBits = (uint)Config.BitDepth - bytesShifted * 8;
                    if (partialFrame != 0)
                    {
                        numSamples = (int)((bits.Read(16) << 16) | bits.Read(16));
                    }

                    if (numSamples > _mixBufferU.Length)
                    {
                        return -1;
                    }

                    if (escapeFlag == 0)
                    {
                        bits.Read(8); // mixBits
                        bits.Read(8); // mixRes
                        headerByte = bits.Read(8);
                        var modeU = headerByte >> 4;
                        var denShiftU = headerByte & 0xF;
                        headerByte = bits.Read(8);
                        var pbFactorU = headerByte >> 5;
                        var numU = (int)(headerByte & 0x1F);
                        for (var i = 0; i < numU; i++)
                        {
                            _coefsU[i] = (short)bits.Read(16);
                        }

                        if (bytesShifted != 0)
                        {
                            return -1; // 16-bit streams never shift bytes
                        }

                        var ag = AgParams.Create(Config.Mb, pb * pbFactorU / 4, Config.Kb, (uint)numSamples, (uint)numSamples, Config.MaxRun);
                        if (!AdaptiveGolomb.Decode(ref ag, ref bits, _predictor, numSamples, (int)chanBits, out _))
                        {
                            return -1;
                        }

                        if (modeU == 0)
                        {
                            Predictor.Decode(_predictor, _mixBufferU, numSamples, _coefsU, numU, chanBits, denShiftU);
                        }
                        else
                        {
                            Predictor.Decode(_predictor, _predictor, numSamples, null, 31, chanBits, 0);
                            Predictor.Decode(_predictor, _mixBufferU, numSamples, _coefsU, numU, chanBits, denShiftU);
                        }
                    }
                    else
                    {
                        var shift = (int)(32 - chanBits);
                        for (var i = 0; i < numSamples; i++)
                        {
                            var val = (int)bits.Read((int)chanBits);
                            _mixBufferU[i] = (val << shift) >> shift;
                        }
                    }

                    for (int i = 0, j = channelIndex; i < numSamples; i++, j += numChannels)
                    {
                        output[j] = (short)_mixBufferU[i];
                    }

                    channelIndex += 1;
                    outNumSamples = numSamples;
                    break;
                }

                case AlacElement.Cpe:
                {
                    if (channelIndex + 2 > numChannels)
                    {
                        return FillRemaining(output, channelIndex, numChannels, outNumSamples);
                    }

                    bits.ReadSmall(4); // element instance tag
                    var unusedHeader = bits.Read(12);
                    if (unusedHeader != 0)
                    {
                        return -1;
                    }

                    var headerByte = bits.Read(4);
                    var partialFrame = headerByte >> 3;
                    var bytesShifted = (headerByte >> 1) & 0x3;
                    if (bytesShifted == 3)
                    {
                        return -1;
                    }

                    var escapeFlag = headerByte & 0x1;
                    var chanBits = (uint)Config.BitDepth - bytesShifted * 8 + 1;
                    if (partialFrame != 0)
                    {
                        numSamples = (int)((bits.Read(16) << 16) | bits.Read(16));
                    }

                    if (numSamples > _mixBufferU.Length)
                    {
                        return -1;
                    }

                    int mixBits;
                    int mixRes;
                    if (escapeFlag == 0)
                    {
                        mixBits = (int)bits.Read(8);
                        mixRes = (sbyte)bits.Read(8);

                        headerByte = bits.Read(8);
                        var modeU = headerByte >> 4;
                        var denShiftU = headerByte & 0xF;
                        headerByte = bits.Read(8);
                        var pbFactorU = headerByte >> 5;
                        var numU = (int)(headerByte & 0x1F);
                        for (var i = 0; i < numU; i++)
                        {
                            _coefsU[i] = (short)bits.Read(16);
                        }

                        headerByte = bits.Read(8);
                        var modeV = headerByte >> 4;
                        var denShiftV = headerByte & 0xF;
                        headerByte = bits.Read(8);
                        var pbFactorV = headerByte >> 5;
                        var numV = (int)(headerByte & 0x1F);
                        for (var i = 0; i < numV; i++)
                        {
                            _coefsV[i] = (short)bits.Read(16);
                        }

                        if (bytesShifted != 0)
                        {
                            return -1; // 16-bit streams never shift bytes
                        }

                        var ag = AgParams.Create(Config.Mb, pb * pbFactorU / 4, Config.Kb, (uint)numSamples, (uint)numSamples, Config.MaxRun);
                        if (!AdaptiveGolomb.Decode(ref ag, ref bits, _predictor, numSamples, (int)chanBits, out _))
                        {
                            return -1;
                        }

                        if (modeU == 0)
                        {
                            Predictor.Decode(_predictor, _mixBufferU, numSamples, _coefsU, numU, chanBits, denShiftU);
                        }
                        else
                        {
                            Predictor.Decode(_predictor, _predictor, numSamples, null, 31, chanBits, 0);
                            Predictor.Decode(_predictor, _mixBufferU, numSamples, _coefsU, numU, chanBits, denShiftU);
                        }

                        ag = AgParams.Create(Config.Mb, pb * pbFactorV / 4, Config.Kb, (uint)numSamples, (uint)numSamples, Config.MaxRun);
                        if (!AdaptiveGolomb.Decode(ref ag, ref bits, _predictor, numSamples, (int)chanBits, out _))
                        {
                            return -1;
                        }

                        if (modeV == 0)
                        {
                            Predictor.Decode(_predictor, _mixBufferV, numSamples, _coefsV, numV, chanBits, denShiftV);
                        }
                        else
                        {
                            Predictor.Decode(_predictor, _predictor, numSamples, null, 31, chanBits, 0);
                            Predictor.Decode(_predictor, _mixBufferV, numSamples, _coefsV, numV, chanBits, denShiftV);
                        }
                    }
                    else
                    {
                        chanBits = Config.BitDepth;
                        var shift = (int)(32 - chanBits);
                        for (var i = 0; i < numSamples; i++)
                        {
                            var val = (int)bits.Read((int)chanBits);
                            _mixBufferU[i] = (val << shift) >> shift;
                            val = (int)bits.Read((int)chanBits);
                            _mixBufferV[i] = (val << shift) >> shift;
                        }

                        mixBits = 0;
                        mixRes = 0;
                    }

                    StereoMatrix.Unmix16(_mixBufferU, _mixBufferV, output[channelIndex..], numChannels, numSamples, mixBits, mixRes);
                    channelIndex += 2;
                    outNumSamples = numSamples;
                    break;
                }

                case AlacElement.Cce:
                case AlacElement.Pce:
                    return -1;

                case AlacElement.Dse:
                {
                    bits.ReadSmall(4);
                    var byteAlign = bits.ReadOne();
                    int count = bits.ReadSmall(8);
                    if (count == 255)
                    {
                        count += bits.ReadSmall(8);
                    }

                    if (byteAlign != 0)
                    {
                        bits.ByteAlign(false);
                    }

                    bits.Advance((uint)count * 8);
                    if (bits.Cur > bits.ByteSize)
                    {
                        return -1;
                    }

                    break;
                }

                case AlacElement.Fil:
                {
                    int count = bits.ReadSmall(4);
                    if (count == 15)
                    {
                        count += bits.ReadSmall(8) - 1;
                    }

                    bits.Advance((uint)count * 8);
                    if (bits.Cur > bits.ByteSize)
                    {
                        return -1;
                    }

                    break;
                }

                case AlacElement.End:
                    bits.ByteAlign(false);
                    return FillRemaining(output, channelIndex, numChannels, outNumSamples);

                default:
                    return -1;
            }

            if (channelIndex >= numChannels)
            {
                return outNumSamples;
            }
        }
    }

    private static int FillRemaining(Span<short> output, int channelIndex, int numChannels, int numSamples)
    {
        for (; channelIndex < numChannels; channelIndex++)
        {
            for (int i = 0, j = channelIndex; i < numSamples; i++, j += numChannels)
            {
                output[j] = 0;
            }
        }

        return numSamples;
    }
}
