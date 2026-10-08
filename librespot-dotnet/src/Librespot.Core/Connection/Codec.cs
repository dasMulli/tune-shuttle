// Port of core/src/connection/codec.rs (tokio_util Encoder/Decoder for the AP framing).
using System.Buffers;
using System.Buffers.Binary;

namespace Librespot.Core.Connection;

/// <summary>A decoded AP packet: command byte + payload (without header and MAC).</summary>
public readonly record struct ApPacket(byte Cmd, ReadOnlyMemory<byte> Payload);

/// <summary>Thrown by <see cref="ApCodec"/> on a malformed frame (upstream <c>ApCodecError::Payload</c>).</summary>
public sealed class ApCodecException(string message) : IOException(message);

/// <summary>
/// Frames AP packets as <c>cmd(1) | len(2, BE) | payload | mac(4)</c>, Shannon-encrypted with a per-direction
/// key and a nonce that is the packet counter. Encode and decode each keep their own cipher and counter.
/// </summary>
public sealed class ApCodec
{
    private const int HeaderSize = 3;
    private const int MacSize = 4;

    private uint _encodeNonce;
    private readonly Shannon _encodeCipher;

    private uint _decodeNonce;
    private readonly Shannon _decodeCipher;
    private (byte Cmd, int Size)? _decodeState; // null == DecodeState::Header

    public ApCodec(ReadOnlySpan<byte> sendKey, ReadOnlySpan<byte> recvKey)
    {
        _encodeCipher = new Shannon(sendKey);
        _decodeCipher = new Shannon(recvKey);
    }

    /// <summary>Encrypts one packet and appends the frame to <paramref name="output"/>.</summary>
    public void Encode(byte cmd, ReadOnlySpan<byte> payload, IBufferWriter<byte> output)
    {
        var frameLength = HeaderSize + payload.Length;
        var span = output.GetSpan(frameLength + MacSize);

        span[0] = cmd;
        BinaryPrimitives.WriteUInt16BigEndian(span[1..], checked((ushort)payload.Length));
        payload.CopyTo(span[HeaderSize..]);

        _encodeCipher.NonceU32(_encodeNonce);
        _encodeNonce++;

        _encodeCipher.Encrypt(span[..frameLength]);
        _encodeCipher.Finish(span.Slice(frameLength, MacSize));

        output.Advance(frameLength + MacSize);
    }

    /// <summary>Encrypts one packet and returns the complete frame.</summary>
    public byte[] Encode(byte cmd, ReadOnlySpan<byte> payload)
    {
        var writer = new ArrayBufferWriter<byte>(HeaderSize + payload.Length + MacSize);
        Encode(cmd, payload, writer);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Tries to decode one packet from the front of <paramref name="buffer"/>. Consumes the bytes it used
    /// (header as soon as 3 bytes are available, then payload + MAC once complete) and returns false when more
    /// input is needed, exactly like the upstream tokio <c>Decoder</c>.
    /// </summary>
    /// <exception cref="InvalidDataException">The MAC check failed.</exception>
    public bool TryDecode(ref ReadOnlySequence<byte> buffer, out ApPacket packet)
    {
        if (_decodeState is null && buffer.Length >= HeaderSize)
        {
            Span<byte> header = stackalloc byte[HeaderSize];
            buffer.Slice(0, HeaderSize).CopyTo(header);
            buffer = buffer.Slice(HeaderSize);

            _decodeCipher.NonceU32(_decodeNonce);
            _decodeNonce++;

            _decodeCipher.Decrypt(header);

            var cmd = header[0];
            var size = BinaryPrimitives.ReadUInt16BigEndian(header[1..]);
            _decodeState = (cmd, size);
        }

        if (_decodeState is (var pendingCmd, var pendingSize) && buffer.Length >= pendingSize + MacSize)
        {
            _decodeState = null;

            var payload = new byte[pendingSize];
            buffer.Slice(0, pendingSize).CopyTo(payload);
            Span<byte> mac = stackalloc byte[MacSize];
            buffer.Slice(pendingSize, MacSize).CopyTo(mac);
            buffer = buffer.Slice(pendingSize + MacSize);

            _decodeCipher.Decrypt(payload);
            _decodeCipher.CheckMac(mac);

            packet = new ApPacket(pendingCmd, payload);
            return true;
        }

        packet = default;
        return false;
    }
}
