// Port of reference/alac/codec/ALACBitUtilities.c (Apache-2.0, Copyright (c) 2011 Apple Inc.).
namespace Airplay2.Sender.Alac;

/// <summary>
/// Fixed-size bit reader/writer over a byte array, MSB first, as the ALAC codec uses it. A struct with copy semantics
/// so the encoder can save and restore a position (<c>startBits = *bitstream</c> in the C++ source). Bounds are not
/// checked on writes (the caller sizes the buffer); reads past the end return zero bits.
/// </summary>
public struct BitBuffer
{
    /// <summary>The backing array.</summary>
    public byte[] Buffer;

    /// <summary>Index of the current byte.</summary>
    public int Cur;

    /// <summary>Bit index inside the current byte, 0..7.</summary>
    public int BitIndex;

    /// <summary>Logical size of the buffer in bytes (may be less than the array length).</summary>
    public int ByteSize;

    /// <summary>Creates a buffer over <paramref name="buffer"/> with the given logical size.</summary>
    public BitBuffer(byte[] buffer, int byteSize)
    {
        Buffer = buffer;
        Cur = 0;
        BitIndex = 0;
        ByteSize = byteSize;
    }

    /// <summary>Current position in bits from the start.</summary>
    public readonly uint Position => (uint)(Cur * 8 + BitIndex);

    /// <summary>True when the current byte index is before the logical end.</summary>
    public readonly bool HasMore => Cur < ByteSize;

    /// <summary>Resets the position to the start.</summary>
    public void Reset()
    {
        Cur = 0;
        BitIndex = 0;
    }

    private readonly uint ByteAt(int index) => index < ByteSize && index < Buffer.Length ? Buffer[index] : 0u;

    /// <summary>Reads up to 16 bits.</summary>
    public uint Read(int numBits)
    {
        var returnBits = (ByteAt(Cur) << 16) | (ByteAt(Cur + 1) << 8) | ByteAt(Cur + 2);
        returnBits <<= BitIndex;
        returnBits &= 0x00FFFFFF;
        BitIndex += numBits;
        returnBits >>= 24 - numBits;
        Cur += BitIndex >> 3;
        BitIndex &= 7;
        return returnBits;
    }

    /// <summary>Reads up to 8 bits.</summary>
    public byte ReadSmall(int numBits)
    {
        var returnBits = (ByteAt(Cur) << 8) | ByteAt(Cur + 1);
        returnBits = (returnBits << BitIndex) & 0xFFFF;
        BitIndex += numBits;
        returnBits >>= 16 - numBits;
        Cur += BitIndex >> 3;
        BitIndex &= 7;
        return (byte)returnBits;
    }

    /// <summary>Reads one bit.</summary>
    public byte ReadOne()
    {
        var returnBits = (byte)((ByteAt(Cur) >> (7 - BitIndex)) & 1);
        BitIndex++;
        Cur += BitIndex >> 3;
        BitIndex &= 7;
        return returnBits;
    }

    /// <summary>Skips <paramref name="numBits"/> bits.</summary>
    public void Advance(uint numBits)
    {
        if (numBits != 0)
        {
            BitIndex += (int)numBits;
            Cur += BitIndex >> 3;
            BitIndex &= 7;
        }
    }

    /// <summary>Moves to the next byte boundary, writing zero bits when <paramref name="addZeros"/> is set.</summary>
    public void ByteAlign(bool addZeros)
    {
        if (BitIndex == 0)
        {
            return;
        }

        if (addZeros)
        {
            Write(0, 8 - BitIndex);
        }
        else
        {
            Advance((uint)(8 - BitIndex));
        }
    }

    /// <summary>Writes the low <paramref name="numBits"/> bits of <paramref name="value"/> (up to 32).</summary>
    public void Write(uint value, int numBits)
    {
        if (numBits <= 0)
        {
            return;
        }

        var invBitIndex = 8 - BitIndex;
        while (numBits > 0)
        {
            var curNum = Math.Min(invBitIndex, numBits);
            var tmp = value >> (numBits - curNum);
            var shift = invBitIndex - curNum;
            var mask = (byte)((0xFF >> (8 - curNum)) << shift);
            Buffer[Cur] = (byte)((Buffer[Cur] & ~mask) | ((byte)(tmp << shift) & mask));
            numBits -= curNum;
            invBitIndex -= curNum;
            if (invBitIndex == 0)
            {
                invBitIndex = 8;
                Cur++;
            }
        }

        BitIndex = 8 - invBitIndex;
    }
}
