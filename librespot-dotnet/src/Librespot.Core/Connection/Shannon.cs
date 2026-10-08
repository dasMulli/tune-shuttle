// Port of the `shannon` crate (shannon-0.2.0/src/lib.rs), itself a transliteration of Greg Rose's
// reference C implementation of the Shannon stream cipher + MAC (Qualcomm, 2006).
//
// This is a byte-exact port of the Rust crate, including its handling of partially buffered words in
// `Process` (the Rust version feeds `MacFunc(mbuf)` after *every* buffered byte, unlike the C original
// which does it once the word is full). librespot only ever encrypts whole packets and decrypts
// header (3 bytes) + payload, so the quirk is only observable through the test vectors, which were
// generated with the Rust crate and must keep passing.
using System.Buffers.Binary;

namespace Librespot.Core.Connection;

/// <summary>Shannon stream cipher with integrated MAC, as used on the Spotify access-point connection.</summary>
public sealed class Shannon
{
    private const int N = 16;
    private const int Fold = N;
    private const uint InitKonst = 0x6996c53a;
    private const int KeyP = 13;

    private readonly uint[] _r = new uint[N];
    private readonly uint[] _crc = new uint[N];
    private readonly uint[] _initR = new uint[N];
    private uint _konst;
    private uint _sbuf;
    private uint _mbuf;
    private int _nbuf;

    /// <summary>Initialises the cipher to a known state from <paramref name="key"/> (any length).</summary>
    public Shannon(ReadOnlySpan<byte> key)
    {
        _konst = InitKonst;

        // Register initialised to Fibonacci numbers; Counter zeroed.
        _r[0] = 1;
        _r[1] = 1;
        for (var i = 2; i < N; i++)
        {
            _r[i] = _r[i - 1] + _r[i - 2];
        }

        LoadKey(key);
        GenKonst();
        SaveState();
    }

    private static uint RotL(uint w, int x) => (w << x) | (w >> (32 - x));

    private static uint Sbox1(uint w)
    {
        w ^= RotL(w, 5) | RotL(w, 7);
        w ^= RotL(w, 19) | RotL(w, 22);
        return w;
    }

    private static uint Sbox2(uint w)
    {
        w ^= RotL(w, 7) | RotL(w, 22);
        w ^= RotL(w, 5) | RotL(w, 19);
        return w;
    }

    // Save the current register state
    private void SaveState() => _r.CopyTo(_initR, 0);

    // initialise to previously saved register state
    private void ReloadState() => _initR.CopyTo(_r, 0);

    // Initialise "konst"
    private void GenKonst() => _konst = _r[0];

    // cycle the contents of the register and calculate output word in sbuf.
    private void Cycle()
    {
        // nonlinear feedback function
        var t = _r[12] ^ _r[13] ^ _konst;
        t = Sbox1(t) ^ RotL(_r[0], 1);

        // shift register
        for (var i = 1; i < N; i++)
        {
            _r[i - 1] = _r[i];
        }

        _r[N - 1] = t;
        t = Sbox2(_r[2] ^ _r[15]);
        _r[0] ^= t;
        _sbuf = t ^ _r[8] ^ _r[12];
    }

    // extra nonlinear diffusion of register for key and MAC
    private void Diffuse()
    {
        for (var i = 0; i < Fold; i++)
        {
            Cycle();
        }
    }

    // Common actions for loading key material.
    // Allow non-word-multiple key and nonce material.
    // Note also initializes the CRC register as a side effect.
    private void LoadKey(ReadOnlySpan<byte> key)
    {
        // start folding in key
        Span<byte> xtra = stackalloc byte[4];
        for (var offset = 0; offset < key.Length; offset += 4)
        {
            var word = key.Slice(offset, Math.Min(4, key.Length - offset));
            if (word.Length == 4)
            {
                _r[KeyP] ^= BinaryPrimitives.ReadUInt32LittleEndian(word);
            }
            else
            {
                // if there were any extra key bytes, zero pad to a word
                xtra.Clear();
                word.CopyTo(xtra);
                _r[KeyP] ^= BinaryPrimitives.ReadUInt32LittleEndian(xtra);
            }

            Cycle();
        }

        // also fold in the length of the key
        _r[KeyP] ^= (uint)key.Length;
        Cycle();

        // save a copy of the register
        _r.CopyTo(_crc, 0);

        // now diffuse
        Diffuse();

        // now xor the copy back -- makes key loading irreversible
        for (var i = 0; i < N; i++)
        {
            _r[i] ^= _crc[i];
        }
    }

    /// <summary>Starts a new message with the given nonce (any length); resets the MAC accumulator.</summary>
    public void Nonce(ReadOnlySpan<byte> nonce)
    {
        ReloadState();
        _konst = InitKonst;
        LoadKey(nonce);
        GenKonst();
        _nbuf = 0;
    }

    /// <summary>Upstream <c>nonce_u32</c>: the nonce is the big-endian encoding of <paramref name="n"/>.</summary>
    public void NonceU32(uint n)
    {
        Span<byte> nonce = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(nonce, n);
        Nonce(nonce);
    }

    // Accumulate a CRC of input words, later to be fed into MAC.
    // This is actually 32 parallel CRC-16s, using the IBM CRC-16
    // polynomial x^16 + x^15 + x^2 + 1.
    private void CrcFunc(uint i)
    {
        var t = _crc[0] ^ _crc[2] ^ _crc[15] ^ i;
        for (var j = 1; j < N; j++)
        {
            _crc[j - 1] = _crc[j];
        }

        _crc[N - 1] = t;
    }

    // Normal MAC word processing: do both stream register and CRC.
    private void MacFunc(uint i)
    {
        CrcFunc(i);
        _r[KeyP] ^= i;
    }

    private void Process(Span<byte> buf, bool encrypt)
    {
        var idx = 0;

        // handle any previously buffered bytes
        if (_nbuf != 0)
        {
            while (_nbuf > 0)
            {
                if (idx < buf.Length)
                {
                    Partial(ref buf[idx++], encrypt);
                    _nbuf -= 8;
                }
                else
                {
                    // not a whole word yet
                    return;
                }

                // LFSR already cycled
                MacFunc(_mbuf);
            }
        }

        var rest = buf[idx..];

        // handle whole words
        var len = rest.Length & ~0x3;
        for (var offset = 0; offset < len; offset += 4)
        {
            Cycle();
            var word = rest.Slice(offset, 4);
            var t = BinaryPrimitives.ReadUInt32LittleEndian(word);
            if (encrypt)
            {
                MacFunc(t);
                t ^= _sbuf;
            }
            else
            {
                t ^= _sbuf;
                MacFunc(t);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(word, t);
        }

        // handle any trailing bytes
        var extra = rest[len..];
        if (extra.Length > 0)
        {
            Cycle();
            _mbuf = 0;
            _nbuf = 32;
            for (var i = 0; i < extra.Length; i++)
            {
                Partial(ref extra[i], encrypt);
                _nbuf -= 8;
            }
        }
    }

    private void Partial(ref byte b, bool encrypt)
    {
        if (encrypt)
        {
            _mbuf ^= (uint)b << (32 - _nbuf);
            b ^= (byte)((_sbuf >> (32 - _nbuf)) & 0xFF);
        }
        else
        {
            b ^= (byte)((_sbuf >> (32 - _nbuf)) & 0xFF);
            _mbuf ^= (uint)b << (32 - _nbuf);
        }
    }

    /// <summary>Combined MAC and encryption, in place. Note that plaintext is accumulated for the MAC.</summary>
    public void Encrypt(Span<byte> buf) => Process(buf, encrypt: true);

    /// <summary>Combined MAC and decryption, in place. Note that plaintext is accumulated for the MAC.</summary>
    public void Decrypt(Span<byte> buf) => Process(buf, encrypt: false);

    /// <summary>
    /// Having accumulated a MAC, finish processing and write it to <paramref name="buf"/> (any length; 4 bytes
    /// for the AP protocol). Any unprocessed bytes are treated as if they were encrypted zero bytes.
    /// </summary>
    public void Finish(Span<byte> buf)
    {
        // handle any previously buffered bytes
        if (_nbuf != 0)
        {
            MacFunc(_mbuf);
        }

        // perturb the MAC to mark end of input.
        // Note that only the stream register is updated, not the CRC. This is an
        // action that can't be duplicated by passing in plaintext, hence
        // defeating any kind of extension attack.
        Cycle();
        _r[KeyP] ^= InitKonst ^ ((uint)_nbuf << 3);
        _nbuf = 0;

        // now add the CRC to the stream register and diffuse it
        for (var i = 0; i < N; i++)
        {
            _r[i] ^= _crc[i];
        }

        Diffuse();

        // produce output from the stream buffer
        for (var offset = 0; offset < buf.Length; offset += 4)
        {
            Cycle();
            var word = buf.Slice(offset, Math.Min(4, buf.Length - offset));
            if (word.Length == 4)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(word, _sbuf);
            }
            else
            {
                for (var i = 0; i < word.Length; i++)
                {
                    word[i] = (byte)((_sbuf >> (8 * i)) & 0xFF);
                }
            }
        }
    }

    /// <summary>Computes the MAC and compares it with <paramref name="expected"/>; throws on mismatch.</summary>
    /// <exception cref="InvalidDataException">The MAC does not match (upstream: <c>io::Error</c> "MAC mismatch").</exception>
    public void CheckMac(ReadOnlySpan<byte> expected)
    {
        Span<byte> actual = expected.Length <= 64 ? stackalloc byte[expected.Length] : new byte[expected.Length];
        Finish(actual);

        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidDataException("MAC mismatch");
        }
    }
}
