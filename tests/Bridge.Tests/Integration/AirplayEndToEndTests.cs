using System.Net;
using Airplay2.FakeReceiver;
using Airplay2.Receiver;
using Airplay2.Receiver.Discovery;
using Airplay2.Receiver.Timing;
using Airplay2.Sender;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Bridge.Contracts;
using Bridge.Service.Airplay;
using Bridge.Service.Api;
using Bridge.Service.Output;
using Bridge.Service.Spotify;
using Bridge.Tests.Service;
using Librespot.Core;
using Librespot.Discovery.Mdns;
using Librespot.Metadata;
using Librespot.Metadata.Audio;
using Librespot.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Bridge.Tests.Integration;

/// <summary>
/// The AirPlay receiver end to end through the bridge (SPEC-receiver.md 11.1 "Bridge", 13 wave 5 <c>integration</c>): the
/// bridge in-process (<see cref="BridgeFactory"/>) with the receiver enabled through the settings API on loopback, RTSP and
/// PTP on port 0 and its advertisement on a private mDNS group; a fake AirPlay receiver as the selected output; our own
/// <see cref="AirplaySender"/> playing the phone (PTP grandmaster on its own loopback <see cref="PtpPort"/>, a buffered ALAC
/// session, then a realtime one). No AirPlay traffic leaves 127.0.0.1 and nothing binds 7000, 319 or 320 (R13), so the test is
/// not gated; the advertisement goes to a private mDNS group and port (TTL 1) that real AirPlay devices do not listen on.
/// </summary>
public sealed class AirplayEndToEndTests(ITestOutputHelper output)
{
    private const int Packet = 352;
    private const int Frames = Packet * 376; // about 3 s (SPEC-receiver.md 11.1: a 3 s tone)
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly SenderIdentity Phone = new("End-to-end phone", "02:00:00:00:0E:01", "02:00:00:00:0E:01", Model: "TestPhone1,1");

    /// <summary>A private mDNS group on its own port (the receiver tests use 53541 and 53543, the sender tests 53530-53533).</summary>
    private static readonly MdnsEndpoint PrivateMdns = new(IPAddress.Parse("239.255.0.251"), IPAddress.Parse("ff05::1:fb"), 53545, HopLimit: 1);

    [Fact]
    public Task A_sender_plays_through_the_bridge_to_the_selected_output_and_spotify_evicts_it() => RunBoundedAsync(EndToEndAsync, TimeSpan.FromSeconds(150));

    private async Task EndToEndAsync()
    {
        // The output: one fake AirPlay receiver, connected through the API at level 20 (so the sender's level 50 is visible).
        await using var speaker = await RecordedFake.StartAsync(new FakeReceiverOptions { Name = "End-to-end speaker" });
        await using var senderPort = new PtpPort(new PtpPortOptions { BindAddress = IPAddress.Loopback, EventPort = 0, GeneralPort = 0 });
        await senderPort.EnsureBoundAsync(None);
        await using var factory = new BridgeFactory();
        factory.Browser.Add(speaker.Describe());
        var client = factory.CreateClient();
        await using var events = await EventClient.ConnectAsync(factory);

        await client.SendJsonAsync<ReceiverDto>(HttpMethod.Put, Http.Receiver(ApiRoutes.ReceiverVolume, speaker.Id), new VolumeRequest(20));
        var connected = await client.SendJsonAsync<ReceiverDto>(HttpMethod.Post, Http.Receiver(ApiRoutes.ReceiverConnect, speaker.Id));
        Assert.Equal(ReceiverConnectionState.Connected, connected.State);
        await speaker.WaitAsync<FakeReceiverEvent.StreamSetup>();
        Assert.Equal(20, (await client.GetJsonAsync<OutputDto>(ApiRoutes.Output)).MasterLevel);

        // The receiver: loopback, port 0 (BridgeFactory), its PTP slave told the test sender's ports (6.2 PeerEventPort), the
        // advertisement on a private group; then switched on the way the Settings page does it (9.1 PUT /api/settings).
        var source = factory.Get<AirplaySource>();
        source.ConfigureReceiver = options => options with
        {
            Mdns = PrivateMdns,
            MdnsHostname = "ap2-bridge-e2e-" + options.Identity.DeviceIdCompact[^4..].ToLowerInvariant(),
            PtpSlave = new PtpSlaveOptions { PeerEventPort = senderPort.EventPort, PeerGeneralPort = senderPort.GeneralPort },
            StatisticsInterval = TimeSpan.FromMilliseconds(500),
        };
        var settings = await client.GetJsonAsync<SettingsDto>(ApiRoutes.Settings);
        Assert.False(settings.AirplayEnabled);
        await client.SendJsonAsync<SettingsDto>(HttpMethod.Put, ApiRoutes.Settings, settings with { AirplayEnabled = true, AirplayName = "Bridge End To End" });
        await events.WaitAsync<BridgeEvent.StatusChanged>(e => e.Status.Airplay is { State: "advertising", Name: "Bridge End To End", SenderName: null });
        var receiver = source.Receiver ?? throw new InvalidOperationException("the receiver is not running");
        Assert.True(receiver.RtspPort > 0);
        Assert.NotEqual(7000, receiver.RtspPort);
        var bridgePtp = factory.Get<IPtpPort>();
        await bridgePtp.EnsureBoundAsync(None);
        Assert.True(source.PtpBound);
        Assert.DoesNotContain(bridgePtp.EventPort, new[] { 319, 320 });
        var advertisement = receiver.Advertisement;
        Assert.NotNull(advertisement); // 3.1: the receiver advertises on the private group
        Assert.Equal("Bridge End To End", advertisement.Name);
        Assert.Equal(receiver.RtspPort, advertisement.Port);

        var sender = new AirplaySender(
            new AirplaySenderOptions
            {
                LocalAddress = IPAddress.Loopback,
                Identity = Phone,
                PreferredStream = StreamType.Buffered,
                DefaultTimingProtocol = TimingProtocol.Ptp,
                Quirks = new Dictionary<string, ReceiverQuirks> { [receiver.Options.Identity.DeviceId] = new() { TimingProtocol = TimingProtocol.Ptp } },
                PtpPort = senderPort,
                Ptp = new PtpMasterOptions { PeerEventPort = bridgePtp.EventPort, PeerGeneralPort = bridgePtp.GeneralPort },
            },
            new MemoryPairingStore(),
            NullLoggerFactory.Instance);
        var described = Describe(receiver);

        // ---- 1. Buffered ALAC under PTP: claim, volume, metadata, audio on the output, TEARDOWN releases ----------------
        var buffered = Tone(Frames, seed: 1);
        var packetsBefore = speaker.Count<FakeReceiverEvent.AudioPacket>();
        var eventsBeforeRelease = 0;
        var player = await sender.CreateGroupPlayerAsync([described], new GroupPlayerOptions { VolumePollInterval = null, Sender = Phone }, None);
        try
        {
            await player.AddReceiverAsync(described, 50, None);

            // R8: the stream SETUP claims the output; the status names the sender (9.1).
            var streaming = await events.WaitAsync<BridgeEvent.StatusChanged>(e => e.Status.Airplay is { State: "streaming" } && e.Status.ActiveSource == "airplay");
            Assert.Equal((Phone.Name, Phone.Model), (streaming.Status.Airplay!.SenderName, streaming.Status.Airplay.SenderModel));
            Assert.Equal(AudioSource.Airplay, factory.Get<SourceSwitch>().Owner);
            Assert.Equal(StreamType.Buffered, source.StreamType);

            // R9: SET_PARAMETER volume -15 dB is master level 50 on the output (source airplay), and the speaker gets -15 dB.
            await events.WaitAsync<BridgeEvent.VolumeChanged>(e => e.ReceiverId == speaker.Id && e.Level == 50 && e.Source == OutputManager.SourceAirplay);
            await speaker.WaitAsync<FakeReceiverEvent.Volume>(v => Math.Abs(v.Db - -15.0) < 0.001);
            Assert.Equal(50, (await client.GetJsonAsync<OutputDto>(ApiRoutes.Output)).MasterLevel);

            // The tone through the playout buffer, the source and the bridge's group player to the speaker.
            await player.StartAsync(None);
            await Task.Run(() => player.Write(buffered));
            var recorded = await WaitForToneAsync(speaker, packetsBefore, buffered);
            AssertTone(recorded, buffered, mutedFrames: Packet); // the first block after a start is muted (5.2)

            // R10: DMAP, artwork and progress become the AirPlay playback view (9.1) and reach the speaker (now-playing path).
            var artwork = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 4, 5, 6 };
            await player.SetNowPlayingAsync(new TrackMetadata("Bridge Tone", "Loopback Artist", "Loopback Album", TimeSpan.FromSeconds(30)), artwork, "image/jpeg", TimeSpan.FromSeconds(2), None);
            var playback = await events.WaitAsync<BridgeEvent.PlaybackChanged>(e => e.Playback is { Source: "airplay", Title: "Bridge Tone" });
            Assert.Equal(("Loopback Artist", "Loopback Album", 30_000L), (playback.Playback.Artist, playback.Playback.Album, playback.Playback.DurationMs));
            Assert.Null(playback.Playback.TrackId);
            Assert.StartsWith("/api/playback/artwork", playback.Playback.ArtworkUrl, StringComparison.Ordinal);
            await speaker.WaitAsync<FakeReceiverEvent.Metadata>(m => m.Items.TryGetValue("minm", out var title) && Equals(title, "Bridge Tone"));
            var current = await client.GetJsonAsync<PlaybackDto>(ApiRoutes.Playback);
            Assert.Equal(("airplay", "Bridge Tone"), (current.Source, current.Title));
            using (var art = await client.GetAsync(ApiRoutes.PlaybackArtwork))
            {
                Assert.Equal(HttpStatusCode.OK, art.StatusCode);
                Assert.Equal(artwork, await art.Content.ReadAsByteArrayAsync());
            }

            var diagnostics = await client.GetJsonAsync<DiagnosticsDto>(ApiRoutes.Diagnostics);
            Assert.Equal(receiver.RtspPort, diagnostics.Airplay!.RtspPort);
            Assert.True(diagnostics.Airplay.PtpBound);
        }
        finally
        {
            eventsBeforeRelease = events.Events.Count; // the release status must be a new event, not the one from switching on
            await player.DisposeAsync(); // TEARDOWN {streams}, then {}
            await sender.DisposeAsync(); // its grandmaster leaves the test PTP port to the next sender
        }

        // 4.9, 8.2: TEARDOWN ends the session and releases the output; the view goes back to no owner.
        await events.WaitAsync<BridgeEvent.StatusChanged>(e => e.Status.Airplay is { State: "advertising", SenderName: null } && e.Status.ActiveSource is null, skip: eventsBeforeRelease);
        await WaitUntilAsync(() => factory.Get<SourceSwitch>().Owner == AudioSource.None, "the output released");
        Assert.Null(source.Sender);
        Assert.Equal(ReceiverConnectionState.Connected, (await client.GetJsonAsync<OutputDto>(ApiRoutes.Output)).Members.Single().State);

        // ---- 2. Realtime under PTP: audio again, then a Spotify Playing event evicts the sender (R8) -----------------------
        var realtime = Tone(Frames, seed: 2);
        packetsBefore = speaker.Count<FakeReceiverEvent.AudioPacket>();
        await using var realtimeSender = new AirplaySender(
            new AirplaySenderOptions
            {
                LocalAddress = IPAddress.Loopback,
                Identity = Phone,
                PreferredStream = StreamType.Realtime,
                DefaultTimingProtocol = TimingProtocol.Ptp,
                Quirks = new Dictionary<string, ReceiverQuirks> { [receiver.Options.Identity.DeviceId] = new() { TimingProtocol = TimingProtocol.Ptp } },
                PtpPort = senderPort,
                Ptp = new PtpMasterOptions { PeerEventPort = bridgePtp.EventPort, PeerGeneralPort = bridgePtp.GeneralPort },
            },
            new MemoryPairingStore(),
            NullLoggerFactory.Instance);
        var second = await realtimeSender.CreateGroupPlayerAsync([described], new GroupPlayerOptions { VolumePollInterval = null, Sender = Phone }, None);
        try
        {
            await second.AddReceiverAsync(described, 50, None);
            await WaitUntilAsync(() => source.StreamType == StreamType.Realtime && factory.Get<SourceSwitch>().Owner == AudioSource.Airplay, "the realtime stream owning the output");
            await second.StartAsync(None);
            await Task.Run(() => second.Write(realtime));
            var recorded = await WaitForToneAsync(speaker, packetsBefore, realtime);
            AssertTone(recorded, realtime, mutedFrames: 0);
            Assert.True(source.PtpStatistics is { PairsSeen: > 0 }, "the bridge's PTP slave follows the sender's grandmaster");

            // A stubbed Spotify "Playing" through the product mapper and switch: Spotify claims, the AirPlay session ends first.
            var mapper = new PlayerEventMapper(factory.Get<OutputManager>(), new NoArtwork(), factory.Get<RuntimeState>(), factory.Get<EventHub>(), NullLogger.Instance, sources: factory.Get<SourceSwitch>());
            await mapper.HandleAsync(new PlayerEvent.Playing(1, SpotifyUri.FromUri("spotify:track:4uLU6hMCjMI75M1A2tKUQC"), 0), None);
            Assert.Equal(AudioSource.Spotify, factory.Get<SourceSwitch>().Owner);
            Assert.Null(source.Sender);
            Assert.NotEqual(ReceiverStatus.Streaming, receiver.Status);
            await events.WaitAsync<BridgeEvent.StatusChanged>(e => e.Status.ActiveSource == "spotify" && e.Status.Airplay is { State: "advertising", SenderName: null });
        }
        finally
        {
            await second.DisposeAsync();
        }

        Assert.Equal(AudioSource.Spotify, factory.Get<SourceSwitch>().Owner);
        Assert.Equal(ReceiverStatus.Advertising, receiver.Status);
    }

    private static async Task RunBoundedAsync(Func<Task> test, TimeSpan bound) => await test().WaitAsync(bound);

    private static DiscoveredReceiver Describe(AirplayReceiver receiver)
    {
        var advertisement = ReceiverTxtRecords.Advertisement(receiver.Options with { Name = receiver.Name }, receiver.RtspPort, receiver.Features);
        return new DiscoveredReceiver(
            receiver.Options.Identity.DeviceId,
            receiver.Name,
            [IPAddress.Loopback],
            receiver.RtspPort,
            AirplayTxtRecord.Parse(advertisement.AirplayTxt),
            RaopTxtRecord.Parse(advertisement.RaopTxt),
            DateTimeOffset.UtcNow);
    }

    /// <summary>A stereo tone with a rising amplitude, so every 352-frame window occurs once (as the receiver loopback tests).</summary>
    private static short[] Tone(int frames, int seed)
    {
        var pcm = new short[frames * 2];
        var frequency = 440.0 + (seed * 110.0);
        for (var i = 0; i < frames; i++)
        {
            var amplitude = 3000.0 + (6000.0 * i / frames);
            var value = (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * frequency * i / 44100.0));
            pcm[2 * i] = value;
            pcm[(2 * i) + 1] = (short)-value;
        }

        return pcm;
    }

    /// <summary>The speaker's audio since <paramref name="skip"/> packets, once the tone's last full block arrived.</summary>
    private async Task<short[]> WaitForToneAsync(RecordedFake speaker, int skip, short[] tone)
    {
        var last = tone.AsSpan(tone.Length - (2 * Packet * 2), Packet * 2).ToArray();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var recorded = speaker.Events.OfType<FakeReceiverEvent.AudioPacket>().Skip(skip).SelectMany(p => p.Pcm).ToArray();
            if (recorded.AsSpan().IndexOf(last) >= 0)
            {
                // The final partial block: wait until the recording stops growing for two consecutive checks.
                var length = recorded.Length;
                var stable = 0;
                while (stable < 2 && DateTime.UtcNow <= deadline)
                {
                    await Task.Delay(50);
                    recorded = speaker.Events.OfType<FakeReceiverEvent.AudioPacket>().Skip(skip).SelectMany(p => p.Pcm).ToArray();
                    stable = recorded.Length == length ? stable + 1 : 0;
                    length = recorded.Length;
                }

                return recorded;
            }

            if (DateTime.UtcNow > deadline)
            {
                output.WriteLine($"recorded {recorded.Length / 2} frames; tone start at {FindTone(recorded, tone)}");
                Assert.Fail("the tone's end did not reach the speaker within 30 s");
            }

            await Task.Delay(50);
        }
    }

    /// <summary>Where <paramref name="tone"/> (from its third block on) starts in <paramref name="recorded"/>, in samples, or -1.</summary>
    private static int FindTone(short[] recorded, short[] tone)
    {
        var at = recorded.AsSpan().IndexOf(tone.AsSpan(2 * Packet * 2, Packet * 2));
        return at < 0 ? -1 : at - (2 * Packet * 2);
    }

    /// <summary>
    /// SPEC-receiver.md 11.1: the speaker got the tone sample for sample (except <paramref name="mutedFrames"/> at its start),
    /// with its frame count within one packet of the tone's.
    /// </summary>
    private void AssertTone(short[] recorded, short[] tone, int mutedFrames)
    {
        var start = FindTone(recorded, tone);
        Assert.True(start >= 0, "the tone is in the speaker's audio");
        var available = Math.Min(tone.Length, recorded.Length - start);
        output.WriteLine($"tone found at frame {start / 2}; {available / 2} of {tone.Length / 2} frames recorded");
        Assert.True(available >= tone.Length - (Packet * 2), $"{available / 2} of {tone.Length / 2} frames recorded");
        var played = recorded.AsSpan(start, available);
        var mismatch = played[(mutedFrames * 2)..].CommonPrefixLength(tone.AsSpan(mutedFrames * 2, available - (mutedFrames * 2)));
        Assert.True(mismatch == available - (mutedFrames * 2), $"the speaker's audio equals the sent tone (first difference at frame {(mismatch / 2) + mutedFrames})");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + RecordedFake.Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for {what}.");
            }

            await Task.Delay(20);
        }
    }

    private sealed class NoArtwork : IArtworkLoader
    {
        public string? UrlOf(AudioItem item) => null;

        public Task<Artwork?> LoadAsync(AudioItem item, CancellationToken cancellationToken) => Task.FromResult<Artwork?>(null);
    }
}
