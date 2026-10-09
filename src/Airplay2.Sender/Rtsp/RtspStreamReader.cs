using System.Buffers.Binary;
using Airplay2.Sender.Crypto;

namespace Airplay2.Sender.Rtsp;

/// <summary>
/// Receive side of an RTSP-over-TCP stream, plaintext or HAP-encrypted (SPEC-control.md 2.5): socket bytes are appended
/// with <see cref="Append"/>; while encryption is off they are the plaintext, once <see cref="EnableDecryption"/> was
/// called they are decoded frame by frame (<c>2 + len + 16</c> bytes each, <c>len</c> up to 65 535, the receive rule
/// of D9) and the plaintext is concatenated, so a frame boundary never has to align with a message boundary
/// [pyatv:pyatv/auth/hap_session.py:36-51; sps:rtsp.c:483-518]. Complete messages are taken out with
/// <see cref="TryReadResponse"/> / <see cref="TryReadRequest"/>. Used by <see cref="RtspConnection"/>, the event channel
/// and the fake receiver's server connection. Not thread-safe: one owner (the connection's read loop, under its lock).
/// </summary>
internal sealed class RtspStreamReader
{
    /// <summary>Bytes one received frame can occupy on the wire: length field, 65 535 bytes of ciphertext, tag.</summary>
    public const int MaxWireFrame = 2 + HapFrameCipher.MaxReceiveFramePlaintext + ChaCha20Poly1305Aead.TagSize;

    private const int InitialPlainCapacity = 16 * 1024;
    private const int InitialRawCapacity = 16 * 1024;

    // Allocated on the first ciphertext byte: plaintext-only connections (GET /info probes, the fake receiver's short
    // connections) never need it; it grows to a full 65 535-byte frame (MaxWireFrame) only when one arrives.
    private byte[] _raw = [];
    private int _rawStart;
    private int _rawEnd;
    private byte[] _plain = new byte[InitialPlainCapacity];
    private int _plainStart;
    private int _plainEnd;
    private HapFrameCipher? _cipher;

    /// <summary>True once <see cref="EnableDecryption"/> was called.</summary>
    public bool IsEncrypted => _cipher is not null;

    /// <summary>Number of HAP frames decrypted so far.</summary>
    public long FramesDecrypted { get; private set; }

    /// <summary>Plaintext length of the largest frame received so far (P21 logs it).</summary>
    public int LargestFramePlaintext { get; private set; }

    /// <summary>Plaintext length of the most recent frame.</summary>
    public int LastFramePlaintext { get; private set; }

    /// <summary>Plaintext bytes buffered but not yet taken out as a message.</summary>
    public int BufferedPlaintext => _plainEnd - _plainStart;

    /// <summary>Ciphertext bytes buffered (an incomplete frame).</summary>
    public int BufferedCiphertext => _rawEnd - _rawStart;

    /// <summary>
    /// Appends received socket bytes and decodes every complete frame. Throws
    /// <see cref="System.Security.Cryptography.CryptographicException"/> on an authentication failure (fatal for the connection).
    /// </summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        if (_cipher is null)
        {
            AppendPlain(data);
            return;
        }

        EnsureRawSpace(data.Length);
        data.CopyTo(_raw.AsSpan(_rawEnd));
        _rawEnd += data.Length;
        DecryptAvailable();
    }

    /// <summary>
    /// Switches the receive side to HAP frames. Plaintext that is buffered but not yet parsed into a message arrived
    /// after the last plaintext message (the pairing response), so it is re-interpreted as ciphertext: the receiver
    /// activates encryption right after that response [sps:rtsp.c:1811-1817, 556-557].
    /// </summary>
    public void EnableDecryption(HapFrameCipher cipher)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        if (_cipher is not null)
        {
            throw new InvalidOperationException("Decryption is already enabled.");
        }

        _cipher = cipher;
        var leftover = _plainEnd - _plainStart;
        if (leftover > 0)
        {
            EnsureRawSpace(leftover);
            _plain.AsSpan(_plainStart, leftover).CopyTo(_raw.AsSpan(_rawEnd));
            _rawEnd += leftover;
            _plainStart = _plainEnd = 0;
            DecryptAvailable();
        }
    }

    /// <summary>What kind of message the buffered plaintext starts with.</summary>
    public RtspMessageKind PeekKind() => RtspMessageCodec.Classify(_plain.AsSpan(_plainStart, _plainEnd - _plainStart));

    /// <summary>Takes one complete response out of the buffer; false when more bytes are needed.</summary>
    public bool TryReadResponse(out RtspResponse? response)
    {
        if (!RtspMessageCodec.TryParseResponse(_plain.AsSpan(_plainStart, _plainEnd - _plainStart), out response, out var consumed))
        {
            return false;
        }

        ConsumePlain(consumed);
        return true;
    }

    /// <summary>Takes one complete request out of the buffer; false when more bytes are needed.</summary>
    public bool TryReadRequest(out RtspIncomingRequest? request)
    {
        if (!RtspMessageCodec.TryParseRequest(_plain.AsSpan(_plainStart, _plainEnd - _plainStart), out request, out var consumed))
        {
            return false;
        }

        ConsumePlain(consumed);
        return true;
    }

    /// <summary>Drops leading empty lines (keep-alive CRLFs) so an idle buffer does not look like a partial message.</summary>
    public void SkipEmptyLines()
    {
        var skip = RtspMessageCodec.SkipEmptyLines(_plain.AsSpan(_plainStart, _plainEnd - _plainStart));
        if (skip > 0)
        {
            ConsumePlain(skip);
        }
    }

    private void DecryptAvailable()
    {
        while (_rawEnd - _rawStart >= 2)
        {
            var input = _raw.AsSpan(_rawStart, _rawEnd - _rawStart);
            int len = BinaryPrimitives.ReadUInt16LittleEndian(input);
            if (input.Length < 2 + len + ChaCha20Poly1305Aead.TagSize)
            {
                break;
            }

            EnsurePlainSpace(len);
            if (!_cipher!.TryDecryptFrame(input, _plain.AsSpan(_plainEnd, len), out var frameLength, out var plaintextLength))
            {
                break; // cannot happen: completeness was checked above
            }

            _plainEnd += plaintextLength;
            _rawStart += frameLength;
            FramesDecrypted++;
            LastFramePlaintext = plaintextLength;
            LargestFramePlaintext = Math.Max(LargestFramePlaintext, plaintextLength);
        }

        if (_rawStart == _rawEnd)
        {
            _rawStart = _rawEnd = 0;
        }
    }

    private void AppendPlain(ReadOnlySpan<byte> data)
    {
        EnsurePlainSpace(data.Length);
        data.CopyTo(_plain.AsSpan(_plainEnd));
        _plainEnd += data.Length;
    }

    private void ConsumePlain(int count)
    {
        _plainStart += count;
        if (_plainStart == _plainEnd)
        {
            _plainStart = _plainEnd = 0;
        }
    }

    private void EnsurePlainSpace(int count)
    {
        if (_plain.Length - _plainEnd >= count)
        {
            return;
        }

        var used = _plainEnd - _plainStart;
        if (_plain.Length - used >= count && _plainStart > 0)
        {
            Buffer.BlockCopy(_plain, _plainStart, _plain, 0, used);
        }
        else
        {
            var limit = RtspMessageCodec.MaxHeadBytes + RtspMessageCodec.MaxBodyBytes + MaxWireFrame;
            if (used + count > limit)
            {
                throw new InvalidDataException($"More than {limit} bytes of unparsed plaintext buffered.");
            }

            var grown = new byte[Math.Max(_plain.Length * 2, used + count)];
            Buffer.BlockCopy(_plain, _plainStart, grown, 0, used);
            _plain = grown;
        }

        _plainStart = 0;
        _plainEnd = used;
    }

    private void EnsureRawSpace(int count)
    {
        if (_raw.Length - _rawEnd >= count)
        {
            return;
        }

        var used = _rawEnd - _rawStart;
        if (_raw.Length - used >= count)
        {
            Buffer.BlockCopy(_raw, _rawStart, _raw, 0, used);
        }
        else
        {
            var grown = new byte[Math.Max(used + count, Math.Max(_raw.Length * 2, InitialRawCapacity))];
            Buffer.BlockCopy(_raw, _rawStart, grown, 0, used);
            _raw = grown;
        }

        _rawStart = 0;
        _rawEnd = used;
    }
}
