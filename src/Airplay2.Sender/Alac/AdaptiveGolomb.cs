// Port of reference/alac/codec/ag_enc.c and ag_dec.c (Apache-2.0, Copyright (c) 2011 Apple Inc.).
// Adaptive Golomb-Rice coding of the predictor residuals. All arithmetic is unchecked 32-bit, as in the C source.
using System.Numerics;

namespace Airplay2.Sender.Alac;

/// <summary>Parameters of the adaptive Golomb coder (<c>AGParamRec</c>).</summary>
public struct AgParams
{
    public uint Mb;
    public uint Mb0;
    public uint Pb;
    public uint Kb;
    public uint Wb;
    public uint Qb;
    public uint Fw;
    public uint Sw;
    public uint MaxRun;

    /// <summary><c>set_ag_params</c>.</summary>
    public static AgParams Create(uint m, uint p, uint k, uint f, uint s, uint maxRun) => new()
    {
        Mb = m,
        Mb0 = m,
        Pb = p,
        Kb = k,
        Wb = (1u << (int)k) - 1,
        Qb = AdaptiveGolomb.Qb - p,
        Fw = f,
        Sw = s,
        MaxRun = maxRun,
    };
}

/// <summary>The adaptive Golomb encoder (<c>dyn_comp</c>) and decoder (<c>dyn_decomp</c>).</summary>
public static class AdaptiveGolomb
{
    public const int QbShift = 9;
    public const uint Qb = 1u << QbShift;
    public const uint Pb0 = 40;
    public const uint Mb0 = 10;
    public const uint Kb0 = 14;
    public const uint MaxRunDefault = 255;
    public const int MMulShift = 2;
    public const int MDenShift = QbShift - MMulShift - 1;
    public const uint MOff = 1u << (MDenShift - 2);
    public const int BitOff = 24;
    public const uint MaxPrefix16 = 9;
    public const uint MaxPrefix32 = 9;
    public const uint MaxDataTypeBits16 = 16;
    private const uint NMaxMeanClamp = 0xFFFF;
    private const uint NMeanClampVal = 0xFFFF;

    /// <summary>Number of leading zero bits of <paramref name="m"/> (32 for zero).</summary>
    public static int Lead(int m) => BitOperations.LeadingZeroCount((uint)m);

    /// <summary><c>lg3a</c>: 31 - lead(x + 3).</summary>
    public static int Lg3a(int x) => 31 - Lead(unchecked(x + 3));

    private static int Abs(int a)
    {
        var isNeg = a >> 31;
        var xorVal = a ^ isNeg;
        return unchecked(xorVal - isNeg);
    }

    private static uint Read32(byte[] buffer, int index)
    {
        uint v = 0;
        for (var i = 0; i < 4; i++)
        {
            v <<= 8;
            if (index + i < buffer.Length)
            {
                v |= buffer[index + i];
            }
        }

        return v;
    }

    private static void Write32(byte[] buffer, int index, uint value)
    {
        for (var i = 3; i >= 0; i--)
        {
            if (index + i < buffer.Length)
            {
                buffer[index + i] = (byte)value;
            }

            value >>= 8;
        }
    }

    private static uint DynCode(int m, int k, int n, out uint outNumBits)
    {
        var um = (uint)m;
        var un = (uint)n;
        var div = un / um;
        uint numBits;
        uint value;
        if (div >= MaxPrefix16)
        {
            numBits = MaxPrefix16 + MaxDataTypeBits16;
            value = unchecked((((1u << (int)MaxPrefix16) - 1) << (int)MaxDataTypeBits16) + un);
        }
        else
        {
            var mod = un % um;
            var de = mod == 0 ? 1u : 0u;
            numBits = unchecked(div + (uint)k + 1 - de);
            value = unchecked((((1u << (int)div) - 1) << (int)(numBits - div)) + mod + 1 - de);
            if (numBits > MaxPrefix16 + MaxDataTypeBits16)
            {
                numBits = MaxPrefix16 + MaxDataTypeBits16;
                value = unchecked((((1u << (int)MaxPrefix16) - 1) << (int)MaxDataTypeBits16) + un);
            }
        }

        outNumBits = numBits;
        return value;
    }

    private static bool DynCode32(int maxBits, uint m, uint k, uint n, out uint outNumBits, out uint outValue, out uint overflow, out uint overflowBits)
    {
        var div = n / m;
        overflow = 0;
        overflowBits = 0;
        if (div < MaxPrefix32)
        {
            var mod = unchecked(n - m * div);
            var de = mod == 0 ? 1u : 0u;
            var numBits = unchecked(div + k + 1 - de);
            var value = unchecked((((1u << (int)div) - 1) << (int)(numBits - div)) + mod + 1 - de);
            if (numBits <= 25)
            {
                outNumBits = numBits;
                outValue = value;
                return false;
            }
        }

        outNumBits = MaxPrefix32;
        outValue = (1u << (int)MaxPrefix32) - 1;
        overflow = n;
        overflowBits = (uint)maxBits;
        return true;
    }

    private static void Jam(byte[] output, int baseIndex, uint bitPos, uint numBits, uint value)
    {
        var index = baseIndex + (int)(bitPos >> 3);
        var curr = Read32(output, index);
        var shift = (int)(32 - (bitPos & 7) - numBits);
        var mask = ~0u >> (int)(32 - numBits);
        mask <<= shift;
        value = (value << shift) & mask;
        value |= curr & ~mask;
        Write32(output, index, value);
    }

    private static void JamLarge(byte[] output, int baseIndex, uint bitPos, uint numBits, uint value)
    {
        var index = baseIndex + (int)(bitPos >> 3);
        var curr = Read32(output, index);
        var shiftValue = (int)(32 - (bitPos & 7) - numBits);
        uint w;
        if (shiftValue < 0)
        {
            w = value >> -shiftValue;
            var mask = ~0u >> -shiftValue;
            w |= curr & ~mask;
            var tailByte = (byte)((value << (8 + shiftValue)) & 0xFF);
            if (index + 4 < output.Length)
            {
                output[index + 4] = tailByte;
            }
        }
        else
        {
            var mask = ~0u >> (int)(32 - numBits);
            mask <<= shiftValue;
            w = (value << shiftValue) & mask;
            w |= curr & ~mask;
        }

        Write32(output, index, w);
    }

    /// <summary><c>dyn_comp</c>: entropy-codes <paramref name="numSamples"/> residuals from <paramref name="pc"/> into the bit stream.</summary>
    public static void Encode(ref AgParams p, int[] pc, ref BitBuffer bitstream, int numSamples, int bitSize, out uint outNumBits)
    {
        if (bitSize is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(bitSize));
        }

        var output = bitstream.Buffer;
        var baseIndex = bitstream.Cur;
        var startPos = (uint)bitstream.BitIndex;
        var bitPos = startPos;
        var rowPos = 0;
        var rowSize = (int)p.Sw;
        var rowJump = (int)p.Fw - rowSize;
        var inIdx = 0;

        var mb = p.Mb0;
        p.Mb = mb;
        var pb = p.Pb;
        var kb = p.Kb;
        var wb = p.Wb;
        var zmode = 0;
        var c = 0;

        while (c < numSamples)
        {
            var m = mb >> QbShift;
            var k = (uint)Lg3a((int)m);
            if (k > kb)
            {
                k = kb;
            }

            m = (1u << (int)k) - 1;

            var del = pc[inIdx++];
            rowPos++;

            var n = unchecked((uint)((Abs(del) << 1) - ((del >> 31) & 1) - zmode));

            if (DynCode32(bitSize, m, k, n, out var numBits, out var value, out var overflow, out var overflowBits))
            {
                Jam(output, baseIndex, bitPos, numBits, value);
                bitPos += numBits;
                JamLarge(output, baseIndex, bitPos, overflowBits, overflow);
                bitPos += overflowBits;
            }
            else
            {
                Jam(output, baseIndex, bitPos, numBits, value);
                bitPos += numBits;
            }

            c++;
            if (rowPos >= rowSize)
            {
                rowPos = 0;
                inIdx += rowJump;
            }

            mb = unchecked(pb * (n + (uint)zmode) + mb - ((pb * mb) >> QbShift));
            if (n > NMaxMeanClamp)
            {
                mb = NMeanClampVal;
            }

            zmode = 0;

            if ((mb << MMulShift) < Qb && c < numSamples)
            {
                zmode = 1;
                var nz = 0;
                while (c < numSamples && pc[inIdx] == 0)
                {
                    inIdx++;
                    nz++;
                    c++;
                    if (++rowPos >= rowSize)
                    {
                        rowPos = 0;
                        inIdx += rowJump;
                    }

                    if (nz >= 65535)
                    {
                        zmode = 0;
                        break;
                    }
                }

                k = unchecked((uint)(Lead((int)mb) - BitOff + (int)((mb + MOff) >> MDenShift)));
                var mz = ((1u << (int)k) - 1) & wb;
                value = DynCode((int)mz, (int)k, nz, out numBits);
                Jam(output, baseIndex, bitPos, numBits, value);
                bitPos += numBits;
                mb = 0;
            }
        }

        outNumBits = bitPos - startPos;
        bitstream.Advance(outNumBits);
    }

    private static uint GetStreamBits(byte[] input, int baseIndex, uint bitOffset, int numBits)
    {
        var byteOffset = (int)(bitOffset / 8);
        var load1 = Read32(input, baseIndex + byteOffset);
        uint result;
        if (numBits + (int)(bitOffset & 7) > 32)
        {
            result = load1 << (int)(bitOffset & 7);
            var load2 = baseIndex + byteOffset + 4 < input.Length ? (uint)input[baseIndex + byteOffset + 4] : 0u;
            var load2Shift = 8 - (numBits + (int)(bitOffset & 7) - 32);
            load2 >>= load2Shift;
            result >>= 32 - numBits;
            result |= load2;
        }
        else
        {
            result = load1 >> (32 - numBits - (int)(bitOffset & 7));
        }

        if (numBits != 32)
        {
            result &= ~(0xFFFFFFFFu << numBits);
        }

        return result;
    }

    private static uint DynGet(byte[] input, int baseIndex, ref uint bitPos, uint m, uint k)
    {
        var tempBits = bitPos;
        var streamLong = Read32(input, baseIndex + (int)(tempBits >> 3));
        streamLong <<= (int)(tempBits & 7);

        var pre = (uint)Lead((int)~streamLong);
        uint result;
        if (pre >= MaxPrefix16)
        {
            pre = MaxPrefix16;
            tempBits += pre;
            streamLong <<= (int)pre;
            result = streamLong >> (int)(32 - MaxDataTypeBits16);
            tempBits += MaxDataTypeBits16;
        }
        else
        {
            tempBits += pre;
            tempBits += 1;
            streamLong <<= (int)(pre + 1);
            var v = streamLong >> (int)(32 - k);
            tempBits += k;
            result = unchecked(pre * m + v - 1);
            if (v < 2)
            {
                result = unchecked(result - (v - 1));
                tempBits -= 1;
            }
        }

        bitPos = tempBits;
        return result;
    }

    private static uint DynGet32(byte[] input, int baseIndex, ref uint bitPos, uint m, uint k, int maxBits)
    {
        var tempBits = bitPos;
        var streamLong = Read32(input, baseIndex + (int)(tempBits >> 3));
        streamLong <<= (int)(tempBits & 7);

        var result = (uint)Lead((int)~streamLong);
        if (result >= MaxPrefix32)
        {
            result = GetStreamBits(input, baseIndex, tempBits + MaxPrefix32, maxBits);
            tempBits += MaxPrefix32 + (uint)maxBits;
        }
        else
        {
            tempBits += result;
            tempBits += 1;
            if (k != 1)
            {
                streamLong <<= (int)(result + 1);
                var v = streamLong >> (int)(32 - k);
                tempBits += k;
                tempBits -= 1;
                result = unchecked(result * m);
                if (v >= 2)
                {
                    result = unchecked(result + (v - 1));
                    tempBits += 1;
                }
            }
        }

        bitPos = tempBits;
        return result;
    }

    /// <summary><c>dyn_decomp</c>: decodes <paramref name="numSamples"/> residuals into <paramref name="pc"/>. Returns false on a malformed stream.</summary>
    public static bool Decode(ref AgParams p, ref BitBuffer bitstream, int[] pc, int numSamples, int maxSize, out uint outNumBits)
    {
        var input = bitstream.Buffer;
        var baseIndex = bitstream.Cur;
        var startPos = (uint)bitstream.BitIndex;
        var maxPos = (uint)Math.Max(0, bitstream.ByteSize - bitstream.Cur) * 8;
        var bitPos = startPos;
        var pbLocal = p.Pb;
        var kbLocal = p.Kb;
        var wbLocal = p.Wb;
        var mb = p.Mb0;
        var zmode = 0;
        var c = 0;
        var outIdx = 0;
        var ok = true;

        while (c < numSamples)
        {
            if (bitPos >= maxPos)
            {
                ok = false;
                break;
            }

            var m = mb >> QbShift;
            var k = (uint)Lg3a((int)m);
            k = Math.Min(k, kbLocal);
            m = (1u << (int)k) - 1;

            var n = DynGet32(input, baseIndex, ref bitPos, m, k, maxSize);

            var ndecode = unchecked(n + (uint)zmode);
            var multiplier = unchecked(-(int)(ndecode & 1));
            multiplier |= 1;
            var del = unchecked((int)((ndecode + 1) >> 1) * multiplier);

            pc[outIdx++] = del;
            c++;

            mb = unchecked(pbLocal * (n + (uint)zmode) + mb - ((pbLocal * mb) >> QbShift));
            if (n > NMaxMeanClamp)
            {
                mb = NMeanClampVal;
            }

            zmode = 0;

            if ((mb << MMulShift) < Qb && c < numSamples)
            {
                zmode = 1;
                k = unchecked((uint)(Lead((int)mb) - BitOff + (int)((mb + MOff) >> MDenShift)));
                var mz = ((1u << (int)k) - 1) & wbLocal;
                var run = DynGet(input, baseIndex, ref bitPos, mz, k);
                if (c + run > (uint)numSamples)
                {
                    ok = false;
                    break;
                }

                for (uint j = 0; j < run; j++)
                {
                    pc[outIdx++] = 0;
                    c++;
                }

                if (run >= 65535)
                {
                    zmode = 0;
                }

                mb = 0;
            }
        }

        outNumBits = bitPos - startPos;
        bitstream.Advance(outNumBits);
        return ok && bitstream.Cur <= bitstream.ByteSize;
    }
}
