using System.Buffers;
using Librespot.Core.Connection;
using Xunit;

namespace Librespot.Tests.Core;

public class ApCodecTests
{
    private static readonly byte[] SendKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] RecvKey = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void Ping_packet_frame_matches_shannon_vector()
    {
        // cmd 0x04 (Ping), 4-byte payload => plaintext frame 04 0004 00000000, encrypted with nonce 0 under the key
        // [0..20). The expected bytes come from the Rust shannon crate vector for that plaintext ... except the
        // vector used nonce 3, so encode three packets first to advance the nonce.
        var key = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        var codec = new ApCodec(key, key);
        codec.Encode(0, [], new ArrayBufferWriter<byte>());
        codec.Encode(0, [], new ArrayBufferWriter<byte>());
        codec.Encode(0, [], new ArrayBufferWriter<byte>());

        var frame = codec.Encode(0x04, [0, 0, 0, 0]);
        Assert.Equal("e4dcb4839c3a01" + "57222313", Convert.ToHexStringLower(frame));
    }

    [Fact]
    public void Encode_then_decode_roundtrips_multiple_packets()
    {
        var sender = new ApCodec(SendKey, RecvKey);
        var receiver = new ApCodec(RecvKey, SendKey); // mirrored keys

        var writer = new ArrayBufferWriter<byte>();
        sender.Encode(0x04, [0, 0, 0, 0], writer);
        sender.Encode(0xab, "hello mercury"u8, writer);
        sender.Encode(0x49, [], writer);

        var buffer = new ReadOnlySequence<byte>(writer.WrittenMemory);

        Assert.True(receiver.TryDecode(ref buffer, out var p1));
        Assert.Equal(0x04, p1.Cmd);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, p1.Payload.ToArray());

        Assert.True(receiver.TryDecode(ref buffer, out var p2));
        Assert.Equal(0xab, p2.Cmd);
        Assert.Equal("hello mercury"u8.ToArray(), p2.Payload.ToArray());

        Assert.True(receiver.TryDecode(ref buffer, out var p3));
        Assert.Equal(0x49, p3.Cmd);
        Assert.Empty(p3.Payload.ToArray());

        Assert.Equal(0, buffer.Length);
        Assert.False(receiver.TryDecode(ref buffer, out _));
    }

    [Fact]
    public void Decode_waits_for_partial_frames()
    {
        var sender = new ApCodec(SendKey, RecvKey);
        var receiver = new ApCodec(RecvKey, SendKey);

        var frame = sender.Encode(0x50, "<product><type>premium</type></product>"u8);

        // Feed 2 bytes: not even a header.
        var partial = new ReadOnlySequence<byte>(frame.AsMemory(0, 2));
        Assert.False(receiver.TryDecode(ref partial, out _));
        Assert.Equal(2, partial.Length);

        // Feed header + half the payload: header consumed, payload pending.
        var half = new ReadOnlySequence<byte>(frame.AsMemory(0, 10));
        Assert.False(receiver.TryDecode(ref half, out _));
        Assert.Equal(7, half.Length);

        // Now the rest (the decoder remembers the header it already consumed).
        var rest = new ReadOnlySequence<byte>(frame.AsMemory(3));
        Assert.True(receiver.TryDecode(ref rest, out var packet));
        Assert.Equal(0x50, packet.Cmd);
        Assert.Equal("<product><type>premium</type></product>"u8.ToArray(), packet.Payload.ToArray());
    }

    [Fact]
    public void Corrupted_frame_fails_mac_check()
    {
        var sender = new ApCodec(SendKey, RecvKey);
        var receiver = new ApCodec(RecvKey, SendKey);

        var frame = sender.Encode(0x04, [1, 2, 3, 4, 5]);
        frame[4] ^= 0xff;
        var buffer = new ReadOnlySequence<byte>(frame);
        Assert.Throws<InvalidDataException>(() => receiver.TryDecode(ref buffer, out _));
    }
}
