using System.Net;
using Airplay2.Sender.Alac;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Xunit;

namespace Airplay2.Sender.Tests.ReceiverLibrary;

/// <summary>
/// The sender-side types the wave 0 scaffold adds for the receiver (SPEC-receiver.md 13): the <c>PtpPort</c> surface
/// (6.1), <c>DmapItem</c> (4.8), the receiver's ALAC cookie (7.1) and <c>IGroupPlayer.QueuedFrames</c> (8.6).
/// </summary>
public class SenderScaffoldTests
{
    [Fact]
    public void Ptp_port_options_default_to_the_standard_ports_and_the_wildcard()
    {
        var options = new PtpPortOptions();

        Assert.Null(options.BindAddress);
        Assert.Equal(319, options.EventPort);
        Assert.Equal(320, options.GeneralPort);
        Assert.Null(new AirplaySenderOptions().PtpPort); // 6.1: null keeps today's private grandmaster sockets
    }

    [Fact]
    public async Task Ptp_port_is_a_pending_type_that_never_binds_on_construction()
    {
        await using var port = new PtpPort(new PtpPortOptions { BindAddress = IPAddress.Loopback, EventPort = 0, GeneralPort = 0 });

        Assert.False(port.IsBound);
        Assert.Equal(0, port.EventPort);
        var pending = await Assert.ThrowsAsync<NotImplementedException>(() => port.EnsureBoundAsync(CancellationToken.None));
        Assert.Equal("SPEC-receiver.md 6.1, wave 1", pending.Message);
        var failure = new PtpPortException("taken", 319);
        Assert.Equal("taken", failure.Message);
        Assert.Equal(319, failure.Port);
        Assert.IsAssignableFrom<IOException>(failure); // PtpMasterException wraps it on the sender path (6.1)
    }

    [Fact]
    public void Dmap_item_maps_onto_track_metadata()
    {
        var item = new DmapItem("Title", "Artist", "Album", TimeSpan.FromSeconds(200), 3, 1, "Genre");

        Assert.Equal(new TrackMetadata("Title", "Artist", "Album", TimeSpan.FromSeconds(200), 3, 1, "Genre"), item.ToTrackMetadata());
        Assert.Equal("SPEC-receiver.md 4.8, wave 1", Assert.Throws<NotImplementedException>(() => DmapDecoder.Decode(DmapEncoder.EncodeListingItem(item.ToTrackMetadata()))).Message);
    }

    [Fact]
    public void Receiver_cookie_is_352_frames_16_bit_stereo_at_44100()
    {
        var cookie = AlacMagicCookie.Default44100Stereo16;

        Assert.Equal(352u, cookie.FrameLength);
        Assert.Equal(16, cookie.BitDepth);
        Assert.Equal(2, cookie.NumChannels);
        Assert.Equal(44100u, cookie.SampleRate);
        Assert.Equal(AlacMagicCookie.AirPlay, cookie);
    }
}
