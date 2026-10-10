using System.Net;
using Airplay2.FakeReceiver;
using Airplay2.Sender;
using Airplay2.Sender.Playback;
using Bridge.Service.Output;
using Librespot.Playback;
using Librespot.Playback.Decoder;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bridge.Tests.Service;

/// <summary>
/// <see cref="AirplaySink"/> driven like librespot drives it (synthetic
/// <c>AudioPacket.Samples</c> decoded by the fakes): f64 samples in, the same S16 the converter produces out of two fake
/// receivers' ALAC decoders; Stop flushes, Start re-anchors; without a player the audio is discarded.
/// </summary>
public sealed class AirplaySinkTests
{
    private const int PacketFrames = 1024; // what a librespot Vorbis packet roughly carries

    [Fact]
    public async Task Samples_written_to_the_sink_arrive_bit_exact_at_two_fake_receivers()
    {
        await using var a = await RecordedFake.StartAsync(new FakeReceiverOptions { Name = "Sink A" });
        await using var b = await RecordedFake.StartAsync(new FakeReceiverOptions { Name = "Sink B" });
        await using var sender = new AirplaySender(new AirplaySenderOptions { LocalAddress = IPAddress.Loopback }, new MemoryPairingStore(), NullLoggerFactory.Instance);
        var receivers = new[] { a.Describe(), b.Describe() };
        await using var player = await sender.CreateGroupPlayerAsync(receivers, new GroupPlayerOptions { VolumePollInterval = null }, CancellationToken.None);
        await player.AddReceiverAsync(receivers[0], 15, CancellationToken.None);
        await player.AddReceiverAsync(receivers[1], 15, CancellationToken.None);
        await using var sink = new AirplaySink(AudioFormat.S16, NullLogger<AirplaySink>.Instance);
        await sink.AttachAsync(player, CancellationToken.None);

        // librespot's converter without a ditherer is deterministic: the expected wire PCM is its output.
        var converter = new Converter(null);
        var packets = Enumerable.Range(0, 40).Select(Synthetic).ToList();
        var expected = packets.SelectMany(p => converter.F64ToS16(p.Data)).ToArray();

        var writer = new Thread(() =>
        {
            sink.Start();
            foreach (var packet in packets)
            {
                sink.Write(packet, converter);
            }
        }) { IsBackground = true, Name = "librespot player (test)" };
        writer.Start();

        var expectedFrames = expected.Length / 2;
        var audioA = await CollectAsync(a, expectedFrames);
        var audioB = await CollectAsync(b, expectedFrames);
        Assert.True(writer.Join(TimeSpan.FromSeconds(20)));

        Assert.True(audioA[0].Marker, "the first packet after the start carries the marker bit (SPEC-audio.md 8.4)");
        Assert.Equal(expected, audioA.SelectMany(p => p.Pcm).Take(expected.Length).ToArray());
        Assert.Equal(expected, audioB.SelectMany(p => p.Pcm).Take(expected.Length).ToArray());
        Assert.Equal(expectedFrames, sink.FramesWritten);
        Assert.Equal(0, sink.FramesDiscarded);
        Assert.True(sink.IsRunning);

        // Stop (librespot pause): every receiver is flushed; Start re-anchors with a new marker packet.
        sink.Stop();
        Assert.False(sink.IsRunning);
        await a.WaitAsync<FakeReceiverEvent.Flush>();
        await b.WaitAsync<FakeReceiverEvent.Flush>();
        var markersBefore = a.Count<FakeReceiverEvent.AudioPacket>(p => p.Marker);
        var resume = new Thread(() =>
        {
            sink.Start();
            for (var i = 0; i < 20; i++)
            {
                sink.Write(Synthetic(100 + i), converter);
            }
        }) { IsBackground = true };
        resume.Start();
        await a.WaitAsync<FakeReceiverEvent.AudioPacket>(p => p.Marker, skip: markersBefore);
        Assert.True(resume.Join(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task Without_a_player_the_audio_is_discarded_and_start_stop_do_nothing()
    {
        await using var sink = new AirplaySink(AudioFormat.S16, NullLogger<AirplaySink>.Instance);
        var converter = new Converter(null);
        sink.Start();
        sink.Write(Synthetic(0), converter);
        sink.Write(new AudioPacket.Raw([1, 2, 3, 4]), converter); // passthrough cannot be forwarded: ignored
        sink.Stop();
        Assert.Equal(PacketFrames, sink.FramesDiscarded);
        Assert.Equal(0, sink.FramesWritten);
        Assert.Throws<Librespot.Playback.AudioBackend.SinkException>(() => new AirplaySink(AudioFormat.F32, NullLogger<AirplaySink>.Instance));
    }

    /// <summary>A 1024-frame stereo packet of two different tones, distinct per index.</summary>
    private static AudioPacket.Samples Synthetic(int index)
    {
        var samples = new double[PacketFrames * 2];
        for (var i = 0; i < PacketFrames; i++)
        {
            var t = (index * PacketFrames) + i;
            samples[2 * i] = 0.4 * Math.Sin(2 * Math.PI * 440 * t / 44100.0);
            samples[(2 * i) + 1] = 0.3 * Math.Sin(2 * Math.PI * 660 * t / 44100.0);
        }

        return new AudioPacket.Samples(samples);
    }

    private static async Task<List<FakeReceiverEvent.AudioPacket>> CollectAsync(RecordedFake fake, int frames)
    {
        var packets = (frames + 351) / 352;
        await fake.WaitAsync<FakeReceiverEvent.AudioPacket>(skip: packets - 1, timeout: TimeSpan.FromSeconds(30));
        return fake.Events.OfType<FakeReceiverEvent.AudioPacket>().Take(packets).ToList();
    }
}
