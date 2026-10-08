// Tests for Decoder/Ogg.cs, the port of the `ogg` crate (v0.9.2) subset used by the passthrough decoder.
// The vectors and scenarios are the crate's own tests (crc.rs test_crc32, writing.rs test_recapture,
// test.rs test_packet_rw / test_page_end_after_first_packet / test_write_large / test_seeking).
using Librespot.Playback.Decoder;
using Xunit;

namespace Librespot.Tests.Playback;

public class OggTests
{
    private const uint Serial = 0xdeadb33f;

    private static readonly byte[] TestArr = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
    private static readonly byte[] TestArr2 = [2, 4, 8, 16, 32, 64, 128, 127, 126, 125, 124];
    private static readonly byte[] TestArr3 = [3, 5, 9, 17, 33, 65, 129, 129, 127, 126, 125];

    [Fact]
    public void Test_crc32()
    {
        // Test page taken from real Ogg file (CRC field zeroed as the spec requires)
        byte[] testArr =
        [
            0x4f, 0x67, 0x67, 0x53, 0x00, 0x02, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x74, 0xa3,
            0x90, 0x5b, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x01, 0x1e, 0x01, 0x76, 0x6f, 0x72,
            0x62, 0x69, 0x73, 0x00, 0x00, 0x00, 0x00, 0x02,
            0x44, 0xac, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x80, 0xb5, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00,
            0xb8, 0x01,
        ];
        Assert.Equal(0x9f858776u, OggCrc.Compute([61, 61, 33]));
        Assert.Equal(0x3d4e946du, OggCrc.Compute(testArr));
        Assert.Equal(0x7b374db8u, OggCrc.Compute(testArr.AsSpan(0, 27)));
    }

    [Fact]
    public void Test_packet_rw()
    {
        var w = new OggPacketWriter();
        w.WritePacket(TestArr, Serial, PacketWriteEndInfo.NormalPacket, 0);
        w.WritePacket(TestArr2, Serial, PacketWriteEndInfo.NormalPacket, 1);
        w.WritePacket(TestArr3, Serial, PacketWriteEndInfo.EndPage, 2);

        var r = new OggPacketReader(new MemoryStream(w.TakeData()));
        var p1 = r.ReadPacket()!;
        Assert.Equal(TestArr, p1.Data);
        Assert.True(p1.FirstInPage);
        Assert.True(p1.FirstInStream);
        Assert.Equal(2ul, p1.AbsgpPage);
        Assert.Equal(TestArr2, r.ReadPacket()!.Data);
        var p3 = r.ReadPacket()!;
        Assert.Equal(TestArr3, p3.Data);
        Assert.True(p3.LastInPage);
        Assert.False(p3.LastInStream);
        Assert.Equal(Serial, p3.StreamSerial);
        Assert.Null(r.ReadPacket());

        // Now test packets spanning multiple segments
        var big = new byte[700];
        for (var i = 0; i < big.Length; i++)
        {
            big[i] = (byte)((byte)i / 4);
        }

        w = new OggPacketWriter();
        w.WritePacket(TestArr, Serial, PacketWriteEndInfo.NormalPacket, 0);
        w.WritePacket(big, Serial, PacketWriteEndInfo.NormalPacket, 1);
        w.WritePacket(TestArr3, Serial, PacketWriteEndInfo.EndPage, 2);
        r = new OggPacketReader(new MemoryStream(w.TakeData()));
        Assert.Equal(TestArr, r.ReadPacket()!.Data);
        Assert.Equal(big, r.ReadPacket()!.Data);
        Assert.Equal(TestArr3, r.ReadPacket()!.Data);

        // Now test packets spanning multiple pages
        var huge = new byte[14_000];
        for (var i = 0; i < huge.Length; i++)
        {
            huge[i] = (byte)((byte)i / 4);
        }

        w = new OggPacketWriter();
        w.WritePacket(huge, Serial, PacketWriteEndInfo.NormalPacket, 1);
        w.WritePacket(TestArr3, Serial, PacketWriteEndInfo.EndPage, 2);
        r = new OggPacketReader(new MemoryStream(w.TakeData()));
        Assert.Equal(huge, r.ReadPacket()!.Data);
        Assert.Equal(TestArr3, r.ReadPacket()!.Data);
    }

    [Fact]
    public void Test_page_end_after_first_packet()
    {
        var w = new OggPacketWriter();
        w.WritePacket(TestArr, Serial, PacketWriteEndInfo.EndPage, 0);
        w.WritePacket(TestArr2, Serial, PacketWriteEndInfo.NormalPacket, 1);
        w.WritePacket(TestArr3, Serial, PacketWriteEndInfo.EndPage, 2);

        var r = new OggPacketReader(new MemoryStream(w.TakeData()));
        var p1 = r.ReadPacket()!;
        Assert.Equal(TestArr, p1.Data);
        Assert.True(p1.LastInPage);
        Assert.Equal(0ul, p1.AbsgpPage);
        Assert.Equal(TestArr2, r.ReadPacket()!.Data);
        Assert.Equal(TestArr3, r.ReadPacket()!.Data);
    }

    [Fact]
    public void Test_write_large()
    {
        // A page can contain at most 255 * 255 = 65025 bytes of payload; 70 000 forces a page break.
        var testArr = GenPck(1234, 70_000 / 4);
        var w = new OggPacketWriter();
        w.WritePacket(testArr, 0x5b90a374, PacketWriteEndInfo.EndPage, 0);

        var r = new OggPacketReader(new MemoryStream(w.TakeData()));
        Assert.Equal(testArr, r.ReadPacket()!.Data);
    }

    [Fact]
    public void Test_recapture()
    {
        // Test that we can deal with recapture at varying distances.
        var c = new MemoryStream();
        var w = new OggPacketWriter();
        w.WritePacket(TestArr, Serial, PacketWriteEndInfo.EndPage, 0);
        c.Write(w.TakeData());

        // Now, after the end of the page, put in some noise.
        c.Write(new byte[38]);

        w.WritePacket(TestArr2, Serial, PacketWriteEndInfo.NormalPacket, 1);
        w.WritePacket(TestArr3, Serial, PacketWriteEndInfo.EndPage, 2);
        c.Write(w.TakeData());
        c.Position = 0;

        var r = new OggPacketReader(c);
        Assert.Equal(TestArr, r.ReadPacket()!.Data);
        Assert.Equal(TestArr2, r.ReadPacket()!.Data);
        Assert.Equal(TestArr3, r.ReadPacket()!.Data);
    }

    [Fact]
    public void Writer_marks_end_of_stream_and_reader_reports_it()
    {
        var w = new OggPacketWriter();
        w.WritePacket(TestArr, Serial, PacketWriteEndInfo.EndPage, 0);
        w.WritePacket(TestArr2, Serial, PacketWriteEndInfo.EndStream, 5);

        var bytes = w.TakeData();
        Assert.True(w.IsEmpty);

        // Second page header: flags = last page (0x04), absgp = 5, sequence number 1.
        var secondPage = bytes.AsSpan(27 + 1 + TestArr.Length);
        Assert.Equal("OggS"u8.ToArray(), secondPage[..4].ToArray());
        Assert.Equal(0x04, secondPage[5]);
        Assert.Equal(5ul, BitConverter.ToUInt64(secondPage.Slice(6, 8)));
        Assert.Equal(1u, BitConverter.ToUInt32(secondPage.Slice(18, 4)));

        var r = new OggPacketReader(new MemoryStream(bytes));
        Assert.False(r.ReadPacket()!.LastInStream);
        var last = r.ReadPacket()!;
        Assert.True(last.LastInStream);
        Assert.Equal(5ul, last.AbsgpPage);
    }

    [Fact]
    public void Corrupted_page_fails_the_checksum()
    {
        var w = new OggPacketWriter();
        w.WritePacket(TestArr, Serial, PacketWriteEndInfo.EndPage, 0);
        var bytes = w.TakeData();
        bytes[^1] ^= 0xff;

        var r = new OggPacketReader(new MemoryStream(bytes));
        var e = Assert.Throws<OggReadException>(() => r.ReadPacket());
        Assert.Equal(OggReadException.Reason.HashMismatch, e.Cause);
        Assert.Equal("CRC32 hash mismatch", e.Message);
    }

    [Fact]
    public void Non_ogg_data_has_no_capture_pattern()
    {
        var r = new OggPacketReader(new MemoryStream(new byte[100]));
        var e = Assert.Throws<OggReadException>(() => r.ReadPacket());
        Assert.Equal(OggReadException.Reason.NoCapturePatternFound, e.Cause);
    }

    [Fact]
    public void Test_seeking()
    {
        const uint pckCount = 402;
        var rng = new XorShift(0x9899eb03, 0x54138143);

        var w = new OggPacketWriter();
        for (uint ctr = 0; ctr < pckCount; ctr++)
        {
            w.WritePacket(
                GenPck(ctr, (int)(rng.Next() & 127)),
                Serial,
                (ctr + 1) % 3 == 0 ? PacketWriteEndInfo.EndPage : PacketWriteEndInfo.NormalPacket,
                ctr);
        }

        var r = new OggPacketReader(new MemoryStream(w.TakeData()));
        TestSeek(r, 32);
        TestSeek(r, 300);
        TestSeek(r, 314);
        TestSeek(r, 100);
        EnsureContinues(r, 101);
        TestSeek(r, 10);
        EnsureContinues(r, 11);

        // Ensure that if we seek to the same place multiple times, it doesn't fill data needlessly.
        r.SeekAbsgp(null, 377);
        r.SeekAbsgp(null, 377);
        TestSeek(r, 377);
        EnsureContinues(r, 378);
        r.SeekAbsgp(null, 200);
        r.SeekAbsgp(null, 200);
        TestSeek(r, 200);
        EnsureContinues(r, 201);

        // Ensure the final page can be sought to
        TestSeek(r, 401);

        // After we sought to the final page, we should be able to seek before it again.
        TestSeek(r, 250);
    }

    private static void TestSeek(OggPacketReader r, uint absgp)
    {
        // First, perform the seek
        Assert.True(r.SeekAbsgp(null, absgp));

        // Then go to the searched packet inside the page. We know that all groups of three packets form one.
        for (var i = 0; i < absgp % 3; i++)
        {
            Assert.NotNull(r.ReadPacket());
        }

        // Now read the actual packet we are interested in and
        var pck = r.ReadPacket()!;

        // a) ensure we have a correct absolute granule pos for the page and
        Assert.True(Math.Abs((long)absgp - (long)pck.AbsgpPage) <= 3);

        // b) ensure the packet's content matches with the one we have put in.
        Assert.Equal(GenPck(absgp, pck.Data.Length / 4), pck.Data);
    }

    private static void EnsureContinues(OggPacketReader r, uint absgp)
    {
        for (uint i = 0; i < 4; i++)
        {
            var pck = r.ReadPacket()!;
            Assert.Equal(GenPck(absgp + i, pck.Data.Length / 4), pck.Data);
        }
    }

    private static byte[] GenPck(uint seed, int lenDFour)
    {
        var ret = new List<byte>(lenDFour * 4);
        var xs = new XorShift(seed, (uint)lenDFour);
        if (lenDFour > 0)
        {
            ret.AddRange(BitConverter.GetBytes(seed));
        }

        for (var i = 1; i < lenDFour; i++)
        {
            ret.AddRange(BitConverter.GetBytes(xs.Next()));
        }

        return [.. ret];
    }

    private sealed class XorShift
    {
        private uint _s0;
        private uint _s1;
        private uint _s2;
        private uint _s3;

        public XorShift(uint seed0, uint seed1)
        {
            _s0 = seed0 ^ 0x2a24a930;
            _s1 = seed1 ^ 0xa9f60227;
            _s2 = ~seed0 ^ 0x68c44d2d;
            _s3 = ~seed1 ^ 0xa1f9794a;
            Next();
            Next();
            Next();
        }

        public uint Next()
        {
            var r = _s3;
            r ^= r << 11;
            r ^= r >> 8;
            _s3 = _s2;
            _s2 = _s1;
            _s1 = _s0;
            r ^= _s0;
            r ^= _s0 >> 19;
            _s0 = r;
            return r;
        }
    }
}
