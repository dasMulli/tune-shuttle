using Airplay2.Sender.Rtp;
using Xunit;

namespace Airplay2.Sender.Tests.Rtp;

/// <summary>The retransmit ring (SPEC-audio.md 2.4): 1000 packets, wrap-safe window, cleared on TEARDOWN only.</summary>
public class PacketHistoryTests
{
    private static byte[] PacketFor(ushort sequence, int length = 40)
    {
        var packet = new byte[length];
        packet[0] = 0x80;
        packet[1] = 0x60;
        packet[2] = (byte)(sequence >> 8);
        packet[3] = (byte)sequence;
        for (var i = 4; i < length; i++)
        {
            packet[i] = (byte)(sequence * 31 + i);
        }

        return packet;
    }

    private static void StoreRange(PacketHistory history, ushort first, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var sequence = (ushort)(first + i);
            history.Store(sequence, PacketFor(sequence));
        }
    }

    [Fact]
    public void Stored_packets_come_back_byte_identical()
    {
        var history = new PacketHistory();
        StoreRange(history, 100, 10);
        var buffer = new byte[RealtimeStreamConstants.MaxPacketSize];
        for (ushort s = 100; s < 110; s++)
        {
            Assert.True(history.TryGet(s, buffer, out var length));
            Assert.Equal(PacketFor(s), buffer[..length]);
        }

        Assert.False(history.TryGet(110, buffer, out _));
        Assert.False(history.TryGet(99, buffer, out _));
    }

    [Fact]
    public void Window_is_the_last_1000_packets()
    {
        var history = new PacketHistory();
        StoreRange(history, 0, 2500);
        var buffer = new byte[RealtimeStreamConstants.MaxPacketSize];
        Assert.Equal((ushort)2499, history.Newest);
        Assert.True(history.TryGet(1500, buffer, out _));
        Assert.True(history.TryGet(2499, buffer, out _));
        Assert.False(history.TryGet(1499, buffer, out _));
        Assert.False(history.TryGet(500, buffer, out _));
    }

    [Fact]
    public void Window_is_wrap_safe_across_65535_to_0()
    {
        var history = new PacketHistory();
        StoreRange(history, 65000, 1100); // 65000 .. 65535, 0 .. 563
        var buffer = new byte[RealtimeStreamConstants.MaxPacketSize];
        Assert.Equal((ushort)563, history.Newest);

        // Window: [65100, 65535] U [0, 563].
        Assert.True(history.TryGet(65100, buffer, out var length));
        Assert.Equal(PacketFor(65100), buffer[..length]);
        Assert.True(history.TryGet(65535, buffer, out _));
        Assert.True(history.TryGet(0, buffer, out length));
        Assert.Equal(PacketFor(0), buffer[..length]);
        Assert.False(history.TryGet(65099, buffer, out _));
        Assert.False(history.TryGet(564, buffer, out _));
        Assert.False(history.TryGet(30000, buffer, out _));
    }

    [Fact]
    public void Sequence_numbers_sharing_a_mod_1000_slot_around_the_wrap_do_not_collide()
    {
        // 65000 % 1000 == 0 % 1000; both are inside one 1000-packet window when the newest is 0.
        var history = new PacketHistory();
        StoreRange(history, 64999, 538); // 64999 .. 65535, 0
        var buffer = new byte[RealtimeStreamConstants.MaxPacketSize];
        Assert.True(history.TryGet(65000, buffer, out var length));
        Assert.Equal(PacketFor(65000), buffer[..length]);
        Assert.True(history.TryGet(0, buffer, out length));
        Assert.Equal(PacketFor(0), buffer[..length]);
    }

    [Fact]
    public void A_forward_jump_invalidates_the_skipped_slots()
    {
        var history = new PacketHistory();
        StoreRange(history, 0, 1000);
        history.Store(1500, PacketFor(1500));
        var buffer = new byte[RealtimeStreamConstants.MaxPacketSize];
        Assert.True(history.TryGet(1500, buffer, out _));
        Assert.True(history.TryGet(999, buffer, out _));
        Assert.False(history.TryGet(1499, buffer, out _));
        Assert.False(history.TryGet(1000, buffer, out _));

        // A jump of a whole lap: slot of 1500 + 1024 still holds 1500 and must not answer for anything else.
        history.Store(1500 + 1024 + 1024, PacketFor(1500 + 2048));
        Assert.False(history.TryGet(1500 + 1024, buffer, out _));
        Assert.False(history.TryGet(1500 + 2047, buffer, out _));
        Assert.True(history.TryGet(1500 + 2048, buffer, out _));
    }

    [Fact]
    public void Clear_empties_the_ring()
    {
        var history = new PacketHistory();
        StoreRange(history, 10, 5);
        history.Clear();
        Assert.Null(history.Newest);
        Assert.False(history.TryGet(12, new byte[2000], out _));

        // A fresh stream after TEARDOWN may start anywhere.
        history.Store(40000, PacketFor(40000));
        Assert.True(history.TryGet(40000, new byte[2000], out _));
        Assert.False(history.TryGet(12, new byte[2000], out _));
    }

    [Fact]
    public void Rejects_oversized_packets_and_short_destinations()
    {
        var history = new PacketHistory();
        Assert.Throws<ArgumentException>(() => history.Store(1, new byte[RealtimeStreamConstants.MaxPacketSize + 1]));
        Assert.Throws<ArgumentException>(() => history.Store(1, Array.Empty<byte>()));
        history.Store(1, PacketFor(1, 100));
        Assert.False(history.TryGet(1, new byte[99], out _));
        Assert.True(history.TryGet(1, new byte[100], out var length));
        Assert.Equal(100, length);
    }

    [Fact]
    public void Concurrent_store_and_get_do_not_tear_packets()
    {
        var history = new PacketHistory();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var writer = new Thread(() =>
        {
            ushort s = 0;
            while (!cts.IsCancellationRequested)
            {
                history.Store(s, PacketFor(s, 200));
                s++;
            }
        });
        writer.Start();
        var buffer = new byte[RealtimeStreamConstants.MaxPacketSize];
        var hits = 0;
        while (!cts.IsCancellationRequested)
        {
            if (history.Newest is { } newest && history.TryGet((ushort)(newest - 10), buffer, out var length))
            {
                var expected = PacketFor((ushort)(newest - 10), 200);
                Assert.Equal(expected, buffer[..length]);
                hits++;
            }
        }

        writer.Join();
        Assert.True(hits > 0);
    }
}
