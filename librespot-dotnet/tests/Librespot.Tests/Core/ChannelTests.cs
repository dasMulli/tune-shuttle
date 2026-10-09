// Tests for core/src/channel.rs (no upstream tests; ChannelManager is dead code upstream, kept faithful).
using Librespot.Core;
using Xunit;

namespace Librespot.Tests.Core;

public class ChannelTests
{
    private static byte[] Packet(ushort id, params byte[] body) => [(byte)(id >> 8), (byte)id, .. body];

    [Fact]
    public async Task HeadersThenDataThenEnd()
    {
        var manager = new Session(SessionConfig.Default(), null).Channel;
        var (id, channel) = manager.Allocate();
        Assert.Equal(0, id);
        Assert.Equal(1, manager.Allocate().Id);

        // header (id 5, "aa bb"), header (id 6, empty), end of headers (the rest of this packet is dropped)
        manager.Dispatch(PacketType.StreamChunkRes, Packet(id, 0x00, 0x03, 0x05, 0xaa, 0xbb, 0x00, 0x01, 0x06, 0x00, 0x00, 0xff));
        manager.Dispatch(PacketType.StreamChunkRes, Packet(id, 1, 2, 3));
        manager.Dispatch(PacketType.StreamChunkRes, Packet(id));

        var h1 = Assert.IsType<ChannelEvent.Header>(await channel.NextAsync());
        Assert.Equal(5, h1.Id);
        Assert.Equal([0xaa, 0xbb], h1.Payload.ToArray());
        var h2 = Assert.IsType<ChannelEvent.Header>(await channel.NextAsync());
        Assert.Equal(6, h2.Id);
        Assert.Empty(h2.Payload.ToArray());
        var data = Assert.IsType<ChannelEvent.Data>(await channel.NextAsync());
        Assert.Equal([1, 2, 3], data.Bytes.ToArray());
        Assert.Null(await channel.NextAsync());
        Assert.Null(await channel.NextAsync()); // closed
    }

    [Fact]
    public async Task ChannelErrorClosesTheChannel()
    {
        var manager = new Session(SessionConfig.Default(), null).Channel;
        var (id, channel) = manager.Allocate();

        manager.Dispatch(PacketType.ChannelError, Packet(id, 0x00, 0x07));

        var error = await Assert.ThrowsAsync<ChannelException>(() => channel.NextAsync().AsTask());
        Assert.Equal(ErrorKind.Aborted, error.Kind);
        Assert.Null(await channel.NextAsync());
    }

    [Fact]
    public async Task SplitViews()
    {
        var manager = new Session(SessionConfig.Default(), null).Channel;
        var (id, channel) = manager.Allocate();
        var (headers, data) = channel.Split();

        manager.Dispatch(PacketType.StreamChunkRes, Packet(id, 0x00, 0x02, 0x09, 0x42, 0x00, 0x00));
        manager.Dispatch(PacketType.StreamChunkRes, Packet(id, 7, 8));
        manager.Dispatch(PacketType.StreamChunkRes, Packet(id, 9));
        manager.Dispatch(PacketType.StreamChunkRes, Packet(id));

        var header = await headers.NextAsync();
        Assert.NotNull(header);
        Assert.Equal(9, header.Value.Id);
        Assert.Equal([0x42], header.Value.Data);

        Assert.Equal([7, 8], (await data.NextAsync())!.Value.ToArray());
        Assert.Equal([9], (await data.NextAsync())!.Value.ToArray());
        Assert.Null(await data.NextAsync());
    }

    [Fact]
    public async Task ShutdownEndsPendingReads()
    {
        var manager = new Session(SessionConfig.Default(), null).Channel;
        var (_, channel) = manager.Allocate();
        var read = channel.NextAsync().AsTask();

        manager.Shutdown();

        await Assert.ThrowsAsync<ChannelException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));

        // Allocations after shutdown get a closed channel.
        var (_, late) = manager.Allocate();
        await Assert.ThrowsAsync<ChannelException>(() => late.NextAsync().AsTask());
    }

    [Fact]
    public void PacketsForUnknownChannelsAreIgnored()
    {
        var manager = new Session(SessionConfig.Default(), null).Channel;
        manager.Dispatch(PacketType.StreamChunkRes, Packet(42, 1, 2, 3));
        Assert.Equal(0, manager.GetDownloadRateEstimate());
    }
}
