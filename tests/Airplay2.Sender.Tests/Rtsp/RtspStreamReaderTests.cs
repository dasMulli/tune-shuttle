using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Airplay2.Sender.Crypto;
using Airplay2.Sender.Rtsp;
using Xunit;

namespace Airplay2.Sender.Tests.Rtsp;

public class RtspStreamReaderTests
{
    private static readonly HapChannelKeys Keys = HapChannelKeys.ForControl(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    /// <summary>One HAP frame of any size (the sender's own cipher caps at 1024; receivers may not).</summary>
    internal static byte[] Frame(byte[] key, ref ulong counter, ReadOnlySpan<byte> plaintext)
    {
        var output = new byte[2 + plaintext.Length + 16];
        BinaryPrimitives.WriteUInt16LittleEndian(output, (ushort)plaintext.Length);
        var nonce = ChaCha20Poly1305Aead.NonceFromCounter(counter++);
        ChaCha20Poly1305Aead.Encrypt(key, nonce, plaintext, output.AsSpan(0, 2), output.AsSpan(2, plaintext.Length), output.AsSpan(2 + plaintext.Length, 16));
        return output;
    }

    private static byte[] Response(int cseq, int bodyLength)
    {
        var body = Enumerable.Range(0, bodyLength).Select(i => (byte)(i * 7)).ToArray();
        return RtspMessageCodec.EncodeResponse("RTSP/1.0", 200, "OK", new Dictionary<string, string> { ["CSeq"] = cseq.ToString(System.Globalization.CultureInfo.InvariantCulture) }, body);
    }

    [Fact]
    public void Split_read_headers_in_one_chunk_body_in_three()
    {
        var head = Encoding.ASCII.GetBytes(SpeakerACapture.ProbeSection('A', "response head"));
        var body = SpeakerACapture.InfoBody;
        var reader = new RtspStreamReader();

        reader.Append(head);
        Assert.Equal(RtspMessageKind.Response, reader.PeekKind());
        Assert.False(reader.TryReadResponse(out _));
        reader.Append(body.AsSpan(0, 100));
        Assert.False(reader.TryReadResponse(out _));
        reader.Append(body.AsSpan(100, 400));
        Assert.False(reader.TryReadResponse(out _));
        reader.Append(body.AsSpan(500));
        Assert.True(reader.TryReadResponse(out var response));
        Assert.Equal(body, response!.Body.ToArray());
        Assert.Equal(0, reader.BufferedPlaintext);
    }

    [Fact]
    public void Byte_by_byte_plaintext()
    {
        var raw = Response(1, 300);
        var reader = new RtspStreamReader();
        RtspResponse? response = null;
        foreach (var b in raw)
        {
            Assert.Null(response);
            reader.Append([b]);
            reader.TryReadResponse(out response);
        }

        Assert.Equal(300, response!.Body.Length);
    }

    [Fact]
    public void Encrypted_frames_not_aligned_with_messages()
    {
        // Two responses, cut into frames of 7, 1, 300, 1024 and the rest: boundaries fall inside heads and bodies.
        byte[] plaintext = [.. Response(0, 2000), .. Response(1, 50)];
        var sizes = new[] { 7, 1, 300, 1024 };
        var wire = new List<byte>();
        ulong counter = 0;
        var offset = 0;
        foreach (var size in sizes)
        {
            wire.AddRange(Frame(Keys.ReadKey, ref counter, plaintext.AsSpan(offset, size)));
            offset += size;
        }

        wire.AddRange(Frame(Keys.ReadKey, ref counter, plaintext.AsSpan(offset)));

        var reader = new RtspStreamReader();
        reader.EnableDecryption(new HapFrameCipher(Keys));
        var responses = new List<RtspResponse>();
        var bytes = wire.ToArray();
        for (var i = 0; i < bytes.Length; i += 333)
        {
            reader.Append(bytes.AsSpan(i, Math.Min(333, bytes.Length - i)));
            while (reader.TryReadResponse(out var r))
            {
                responses.Add(r!);
            }
        }

        Assert.Equal(2, responses.Count);
        Assert.Equal(0, responses[0].CSeq);
        Assert.Equal(2000, responses[0].Body.Length);
        Assert.Equal(1, responses[1].CSeq);
        Assert.Equal(5, reader.FramesDecrypted);
        Assert.Equal(0, reader.BufferedCiphertext);
    }

    [Theory]
    [InlineData(4000)]
    [InlineData(65535)]
    public void Accepts_frames_larger_than_1024(int frameSize)
    {
        var padded = Response(9, frameSize - 60);
        Assert.InRange(padded.Length, frameSize - 60, frameSize);
        ulong counter = 0;
        var frame = Frame(Keys.ReadKey, ref counter, padded);
        var reader = new RtspStreamReader();
        reader.EnableDecryption(new HapFrameCipher(Keys));
        reader.Append(frame.AsSpan(0, frame.Length - 1));
        Assert.Equal(0, reader.FramesDecrypted);
        reader.Append(frame.AsSpan(frame.Length - 1));
        Assert.True(reader.TryReadResponse(out var response));
        Assert.Equal(9, response!.CSeq);
        Assert.Equal(1, reader.FramesDecrypted);
        Assert.Equal(padded.Length, reader.LargestFramePlaintext);
    }

    [Fact]
    public void Tampered_frame_throws()
    {
        ulong counter = 0;
        var frame = Frame(Keys.ReadKey, ref counter, Response(0, 10));
        frame[5] ^= 1;
        var reader = new RtspStreamReader();
        reader.EnableDecryption(new HapFrameCipher(Keys));
        Assert.Throws<CryptographicException>(() => reader.Append(frame));
    }

    [Fact]
    public void Wrong_direction_key_throws()
    {
        ulong counter = 0;
        var frame = Frame(Keys.WriteKey, ref counter, Response(0, 10));
        var reader = new RtspStreamReader();
        reader.EnableDecryption(new HapFrameCipher(Keys));
        Assert.Throws<CryptographicException>(() => reader.Append(frame));
    }

    [Fact]
    public void Bytes_after_the_plaintext_pairing_response_are_reread_as_ciphertext()
    {
        var plain = Response(3, 37);
        ulong counter = 0;
        var encrypted = Frame(Keys.ReadKey, ref counter, Response(4, 5));
        var reader = new RtspStreamReader();
        reader.Append([.. plain, .. encrypted.AsSpan(0, 10)]);
        Assert.True(reader.TryReadResponse(out var first));
        Assert.Equal(3, first!.CSeq);
        Assert.Equal(10, reader.BufferedPlaintext);

        reader.EnableDecryption(new HapFrameCipher(Keys));
        Assert.Equal(0, reader.BufferedPlaintext);
        Assert.False(reader.TryReadResponse(out _));
        reader.Append(encrypted.AsSpan(10));
        Assert.True(reader.TryReadResponse(out var second));
        Assert.Equal(4, second!.CSeq);
        Assert.Throws<InvalidOperationException>(() => reader.EnableDecryption(new HapFrameCipher(Keys)));
    }

    [Fact]
    public void A_leftover_frame_whose_length_byte_is_CR_survives_the_switch()
    {
        // A 13-byte frame starts with 0x0D 0x00: it must not be taken for a keep-alive CRLF before the switch.
        var plain = Response(3, 4);
        var unsolicited = Response(4, 0);
        ulong counter = 0;
        byte[] frames = [.. Frame(Keys.ReadKey, ref counter, unsolicited.AsSpan(0, 13)), .. Frame(Keys.ReadKey, ref counter, unsolicited.AsSpan(13))];
        Assert.Equal(0x0D, frames[0]);
        var reader = new RtspStreamReader();
        reader.Append([.. plain, .. frames]);
        Assert.True(reader.TryReadResponse(out var first));
        Assert.Equal(3, first!.CSeq);
        Assert.Equal(frames.Length, reader.BufferedPlaintext);

        reader.EnableDecryption(new HapFrameCipher(Keys));
        Assert.Equal(2, reader.FramesDecrypted);
        Assert.True(reader.TryReadResponse(out var second));
        Assert.Equal(4, second!.CSeq);
        Assert.Equal(0, reader.BufferedPlaintext);
        Assert.Equal(0, reader.BufferedCiphertext);
    }

    [Fact]
    public void Requests_and_responses_can_share_a_stream()
    {
        var reader = new RtspStreamReader();
        reader.Append("\r\nPOST /command RTSP/1.0\r\nContent-Length: 0\r\n\r\nRTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Length: 0\r\n\r\n"u8);
        reader.SkipEmptyLines();
        Assert.Equal(RtspMessageKind.Request, reader.PeekKind());
        Assert.True(reader.TryReadRequest(out var request));
        Assert.Equal("/command", request!.Uri);
        Assert.Equal(RtspMessageKind.Response, reader.PeekKind());
        Assert.True(reader.TryReadResponse(out var response));
        Assert.Equal(1, response!.CSeq);
        Assert.Equal(RtspMessageKind.None, reader.PeekKind());
    }
}
