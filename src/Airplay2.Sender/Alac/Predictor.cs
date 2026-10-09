// Port of reference/alac/codec/dp_enc.c and dp_dec.c (Apache-2.0, Copyright (c) 2011 Apple Inc.).
// Only the general-case loops are ported: the C sources' numactive == 4 and == 8 branches are arithmetic-identical
// unrolled copies, so the general path reproduces their output bit for bit.
namespace Airplay2.Sender.Alac;

/// <summary>The adaptive FIR predictor (<c>pc_block</c> / <c>unpc_block</c>) and its coefficient initialisation.</summary>
public static class Predictor
{
    public const int DenShiftMax = 15;
    public const int DenShiftDefault = 9;
    public const int AInit = 38;
    public const int BInit = -29;
    public const int CInit = -2;
    public const int NumCoefPairs = 16;

    /// <summary><c>init_coefs</c>.</summary>
    public static void InitCoefs(short[] coefs, int denShift, int numPairs)
    {
        var den = 1 << denShift;
        coefs[0] = (short)((AInit * den) >> 4);
        coefs[1] = (short)((BInit * den) >> 4);
        coefs[2] = (short)((CInit * den) >> 4);
        for (var k = 3; k < numPairs; k++)
        {
            coefs[k] = 0;
        }
    }

    private static int SignOfInt(int i)
    {
        var negishift = (int)((uint)unchecked(-i) >> 31);
        return negishift | (i >> 31);
    }

    /// <summary><c>pc_block</c>: computes residuals <paramref name="pc"/> from <paramref name="input"/>, adapting <paramref name="coefs"/>.</summary>
    public static void Encode(int[] input, int[] pc, int num, short[]? coefs, int numActive, uint chanBits, uint denShift)
    {
        var chanShift = (int)(32 - chanBits);
        pc[0] = input[0];
        if (numActive == 0)
        {
            if (num > 1 && !ReferenceEquals(input, pc))
            {
                Array.Copy(input, 1, pc, 1, num - 1);
            }

            return;
        }

        if (numActive == 31)
        {
            for (var j = 1; j < num; j++)
            {
                var d = unchecked(input[j] - input[j - 1]);
                pc[j] = (d << chanShift) >> chanShift;
            }

            return;
        }

        ArgumentNullException.ThrowIfNull(coefs);
        var denHalf = 1 << ((int)denShift - 1);
        for (var j = 1; j <= numActive; j++)
        {
            var d = unchecked(input[j] - input[j - 1]);
            pc[j] = (d << chanShift) >> chanShift;
        }

        var lim = numActive + 1;
        for (var j = lim; j < num; j++)
        {
            var top = input[j - lim];
            var sum1 = 0;
            for (var k = 0; k < numActive; k++)
            {
                sum1 = unchecked(sum1 - coefs[k] * (top - input[j - 1 - k]));
            }

            var del = unchecked(input[j] - top - ((sum1 + denHalf) >> (int)denShift));
            del = (del << chanShift) >> chanShift;
            pc[j] = del;
            var del0 = del;

            var sg = SignOfInt(del);
            if (sg > 0)
            {
                for (var k = numActive - 1; k >= 0; k--)
                {
                    var dd = unchecked(top - input[j - 1 - k]);
                    var sgn = SignOfInt(dd);
                    coefs[k] = unchecked((short)(coefs[k] - sgn));
                    del0 = unchecked(del0 - (numActive - k) * ((sgn * dd) >> (int)denShift));
                    if (del0 <= 0)
                    {
                        break;
                    }
                }
            }
            else if (sg < 0)
            {
                for (var k = numActive - 1; k >= 0; k--)
                {
                    var dd = unchecked(top - input[j - 1 - k]);
                    var sgn = SignOfInt(dd);
                    coefs[k] = unchecked((short)(coefs[k] + sgn));
                    del0 = unchecked(del0 - (numActive - k) * ((-sgn * dd) >> (int)denShift));
                    if (del0 >= 0)
                    {
                        break;
                    }
                }
            }
        }
    }

    /// <summary><c>unpc_block</c>: reconstructs samples <paramref name="output"/> from residuals <paramref name="pc"/>, adapting <paramref name="coefs"/>.</summary>
    public static void Decode(int[] pc, int[] output, int num, short[]? coefs, int numActive, uint chanBits, uint denShift)
    {
        var chanShift = (int)(32 - chanBits);
        output[0] = pc[0];
        if (numActive == 0)
        {
            if (num > 1 && !ReferenceEquals(pc, output))
            {
                Array.Copy(pc, 1, output, 1, num - 1);
            }

            return;
        }

        if (numActive == 31)
        {
            var prev = output[0];
            for (var j = 1; j < num; j++)
            {
                var d = unchecked(pc[j] + prev);
                prev = (d << chanShift) >> chanShift;
                output[j] = prev;
            }

            return;
        }

        ArgumentNullException.ThrowIfNull(coefs);
        var denHalf = 1 << ((int)denShift - 1);
        for (var j = 1; j <= numActive; j++)
        {
            var d = unchecked(pc[j] + output[j - 1]);
            output[j] = (d << chanShift) >> chanShift;
        }

        var lim = numActive + 1;
        for (var j = lim; j < num; j++)
        {
            var sum1 = 0;
            var top = output[j - lim];
            for (var k = 0; k < numActive; k++)
            {
                sum1 = unchecked(sum1 + coefs[k] * (output[j - 1 - k] - top));
            }

            var del = pc[j];
            var del0 = del;
            var sg = SignOfInt(del);
            del = unchecked(del + top + ((sum1 + denHalf) >> (int)denShift));
            output[j] = (del << chanShift) >> chanShift;

            if (sg > 0)
            {
                for (var k = numActive - 1; k >= 0; k--)
                {
                    var dd = unchecked(top - output[j - 1 - k]);
                    var sgn = SignOfInt(dd);
                    coefs[k] = unchecked((short)(coefs[k] - sgn));
                    del0 = unchecked(del0 - (numActive - k) * ((sgn * dd) >> (int)denShift));
                    if (del0 <= 0)
                    {
                        break;
                    }
                }
            }
            else if (sg < 0)
            {
                for (var k = numActive - 1; k >= 0; k--)
                {
                    var dd = unchecked(top - output[j - 1 - k]);
                    var sgn = SignOfInt(dd);
                    coefs[k] = unchecked((short)(coefs[k] + sgn));
                    del0 = unchecked(del0 - (numActive - k) * ((-sgn * dd) >> (int)denShift));
                    if (del0 >= 0)
                    {
                        break;
                    }
                }
            }
        }
    }
}

/// <summary>Stereo decorrelation for 16-bit input (<c>mix16</c> / <c>unmix16</c> from matrix_enc.c / matrix_dec.c).</summary>
public static class StereoMatrix
{
    /// <summary><c>mix16</c>: u = (mixres*L + (2^mixbits - mixres)*R) >> mixbits, v = L - R (or plain L/R when mixres == 0).</summary>
    public static void Mix16(ReadOnlySpan<short> input, int stride, int[] u, int[] v, int numSamples, int mixBits, int mixRes)
    {
        var ip = 0;
        if (mixRes != 0)
        {
            var mod = 1 << mixBits;
            var m2 = mod - mixRes;
            for (var j = 0; j < numSamples; j++)
            {
                int l = input[ip];
                int r = input[ip + 1];
                ip += stride;
                u[j] = (mixRes * l + m2 * r) >> mixBits;
                v[j] = l - r;
            }
        }
        else
        {
            for (var j = 0; j < numSamples; j++)
            {
                u[j] = input[ip];
                v[j] = input[ip + 1];
                ip += stride;
            }
        }
    }

    /// <summary><c>unmix16</c>: L = u + v - ((mixres*v) >> mixbits), R = L - v.</summary>
    public static void Unmix16(int[] u, int[] v, Span<short> output, int stride, int numSamples, int mixBits, int mixRes)
    {
        var op = 0;
        if (mixRes != 0)
        {
            for (var j = 0; j < numSamples; j++)
            {
                var l = unchecked(u[j] + v[j] - ((mixRes * v[j]) >> mixBits));
                var r = unchecked(l - v[j]);
                output[op] = (short)l;
                output[op + 1] = (short)r;
                op += stride;
            }
        }
        else
        {
            for (var j = 0; j < numSamples; j++)
            {
                output[op] = (short)u[j];
                output[op + 1] = (short)v[j];
                op += stride;
            }
        }
    }
}
