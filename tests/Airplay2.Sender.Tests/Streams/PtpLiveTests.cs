using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Airplay2.Sender.Alac;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Airplay2.Sender.Tests.Session;
using Xunit;
using Xunit.Abstractions;

namespace Airplay2.Sender.Tests.Streams;

/// <summary>
/// Live PTP tests (docs/DEVELOPMENT.md, "Live-device rules"). They return early unless <c>AIRPLAY_LIVE=1</c> and
/// write the decrypted wire exchange and every PTP observation to <c>artifacts/live/&lt;date&gt;-&lt;probe&gt;.log</c>.
/// <list type="bullet">
/// <item><b>ShairportLive</b>: shairport-sync 5.5.2 in the Docker VM (<c>SPS_HOST</c>/<c>SPS_PORT</c>). nqptp owns UDP
/// 319/320 of the VM's host network, so the SDK container must run in its <b>own</b> network namespace for our grandmaster
/// to bind 319/320 and for nqptp's answers to reach us; <c>docker/shairport-sync/run-ptp-live-tests-in-vm.ps1</c> runs:
/// <c>docker run --rm -v &lt;repo&gt;:/repo -e AIRPLAY_LIVE=1
/// -e SPS_HOST=172.17.0.1 -w /repo mcr.microsoft.com/dotnet/sdk:10.0 dotnet test tests/Airplay2.Sender.Tests --filter
/// "FullyQualifiedName~PtpLiveTests&amp;Category=ShairportLive"</c> (default bridge network; 172.17.0.1 is the VM side of
/// the bridge where shairport-sync listens). With <c>--net host</c> set <c>PTP_EVENT_PORT=0 PTP_GENERAL_PORT=0</c>.</item>
/// <item><b>ReceiverLive</b>: probe P17 during a P1-PTP control probe on the live receiver (AIRPLAY_LIVE_RECEIVER_HOST; pairing, PTP session SETUP,
/// event channel, RECORD, SETPEERS, stream SETUP, <b>no audio</b>, TEARDOWN), with our grandmaster on 319/320.</item>
/// </list>
/// </summary>
public sealed class PtpLiveTests(ITestOutputHelper output)
{
    private static readonly string[] SpeakerAAirplay =
    [
        "acl=0", "deviceid=02:1A:2B:3C:4D:5E", "features=0x445F8A00,0x1C340", "rsf=0x0", "fv=p20.1.1.36.0x8b4c480", "flags=0x404",
        "model=Test Speaker ABCD", "manufacturer=Example Audio Maker GmbH", "serialNumber=00000000-0000-4000-8000-000000000026",
        "protovers=1.1", "srcvers=366.0", "pi=02:1A:2B:3C:4D:5E", "gid=02:1A:2B:3C:4D:5E", "gcgl=0",
        "pk=d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
    ];

    private static bool LiveEnabled => Environment.GetEnvironmentVariable("AIRPLAY_LIVE") == "1";

    /// <summary>
    /// shairport-sync under our clock: PTP session SETUP with our clock id, SETPEERS, stream SETUP (type 96), then 3 s of
    /// ALAC silence anchored by 0xD7 syncs carrying our PTP time and clock id; the dummy backend consumes it. nqptp's
    /// answers (if any) are logged for P17. Acceptance is read from shairport-sync's log (<c>docker logs sps-test</c>).
    /// </summary>
    [Fact]
    [Trait("Category", "ShairportLive")]
    public async Task Shairport_follows_our_clock_and_plays_ptp_synced_silence()
    {
        if (!Live())
        {
            return;
        }

        var host = IPAddress.Parse(Environment.GetEnvironmentVariable("SPS_HOST") ?? "127.0.0.1");
        var port = int.Parse(Environment.GetEnvironmentVariable("SPS_PORT") ?? "7000", CultureInfo.InvariantCulture);
        using var log = new SessionLiveLog("ptp-shairport", output);
        var receiver = new DiscoveredReceiver("SPS", "shairport-sync", [host], port, AirplayTxtRecord.Parse(["features=0x405FCA00,0x1C340", "flags=0x4", "model=ShairportSync"]), null, DateTimeOffset.UtcNow);
        var clock = new MasterClock();
        await using var master = new PtpMaster(clock, log.CreateLogger<PtpMaster>(), PtpOptions());
        await master.StartAsync(CancellationToken.None);
        log.Write($"grandmaster {master.ClockId:X16} on UDP {master.EventPort}/{master.GeneralPort}, peer timeout {master.Options.PeerTimeout}");

        var connection = await RtspConnection.ConnectAsync(new IPEndPoint(host, port), new RtspConnectionOptions(), log.CreateLogger("rtsp"), CancellationToken.None);
        await using var session = new ReceiverSession(receiver, connection, new HapPairingClient(log.CreateLogger<HapPairingClient>()), ReceiverQuirks.ForReceiver(receiver), log.CreateLogger<ReceiverSession>());
        await using var control = new ControlPort(log.CreateLogger<ControlPort>());
        await control.StartAsync(CancellationToken.None);
        IPAddress? peer = null;
        try
        {
            await session.GetInfoAsync(false, CancellationToken.None);
            var paired = await session.PairAsync(new InMemoryPairingStore(), null, CancellationToken.None);
            var setup = await session.SetupSessionAsync(PtpSession(master), CancellationToken.None);
            log.Write($"PTP session SETUP: eventPort {setup.EventPort}, timingPeerInfo [{string.Join(", ", setup.TimingPeerAddresses)}], our address {connection.LocalAddress}");
            await session.ConnectEventChannelAsync(CancellationToken.None);
            await session.RecordAsync(CancellationToken.None);
            peer = await PtpSessionWiring.AttachAsync(session, master, setup, null, CancellationToken.None);
            log.Write($"SETPEERS sent; PTP peer {peer}");
            var stream = await session.SetupStreamAsync(new StreamSetupRequest(StreamType.Realtime, paired.DefaultStreamKey, control.Port), CancellationToken.None);
            log.Write($"stream SETUP: data {stream.DataPort}, control {stream.ControlPort}");
            await session.SetVolumeAsync(-30.0, CancellationToken.None);

            // Give nqptp time to take our clock as master (shairport-sync waits for it before it anchors).
            await Task.Delay(1500);
            var sent = await StreamSilenceAsync(clock, master, paired.DefaultStreamKey, new IPEndPoint(host, stream.DataPort), new IPEndPoint(host, stream.ControlPort), TimeSpan.FromSeconds(Seconds("PTP_SILENCE_SECONDS", 3)), log);
            log.Write($"silence: {sent.Packets} packets, {sent.Syncs} 0xD7 syncs");
            await Task.Delay(TimeSpan.FromSeconds(Seconds("PTP_OBSERVE_SECONDS", 3)));
            var feedback = await session.FeedbackAsync(CancellationToken.None);
            log.Write($"feedback: {(feedback is null ? "empty" : string.Join(",", feedback.Keys))}");

            var stats = master.GetStatistics();
            log.Write($"grandmaster: {stats}");
            Assert.True(stats.SyncsSent > 20);
            Assert.Equal(0, stats.SendErrors);
            Assert.True(sent.Packets >= 370);
            Assert.Equal(0, stats.PeersTimedOut);
        }
        finally
        {
            var result = await session.TeardownWithResultAsync(CancellationToken.None);
            log.Write($"TEARDOWN: session {result.SessionResponse?.StatusCode.ToString(CultureInfo.InvariantCulture) ?? "-"}");
            if (peer is not null)
            {
                master.RemovePeer(peer);
            }

            Report(master, log);
        }
    }

    /// <summary>
    /// The same against shairport-sync through the audio pipeline's <see cref="RealtimeStreamTransport"/> constructed with
    /// this grandmaster (0xD7 syncs accepted by the realtime stream, 3 s of silence into
    /// the dummy backend). The transport starts the master, owns the peer through <see cref="RealtimeStreamTransport.AddTimingPeer"/>
    /// and sends the 0x90/0x80 0xD7 syncs itself; this test only paces <see cref="RealtimeStreamTransport.Send"/>.
    /// </summary>
    [Fact]
    [Trait("Category", "ShairportLive")]
    public async Task Shairport_plays_silence_through_the_realtime_transport_under_ptp()
    {
        if (!Live())
        {
            return;
        }

        var host = IPAddress.Parse(Environment.GetEnvironmentVariable("SPS_HOST") ?? "127.0.0.1");
        var port = int.Parse(Environment.GetEnvironmentVariable("SPS_PORT") ?? "7000", CultureInfo.InvariantCulture);
        using var log = new SessionLiveLog("ptp-shairport-transport", output);
        var receiver = new DiscoveredReceiver("SPS", "shairport-sync", [host], port, AirplayTxtRecord.Parse(["features=0x405FCA00,0x1C340", "flags=0x4", "model=ShairportSync"]), null, DateTimeOffset.UtcNow);
        var clock = new MasterClock();
        await using var master = new PtpMaster(clock, log.CreateLogger<PtpMaster>(), PtpOptions());
        await using var timing = new TimingResponder(clock, log.CreateLogger<TimingResponder>());
        await using var control = new ControlPort(log.CreateLogger<ControlPort>());
        await using var transport = new RealtimeStreamTransport(clock, timing, control, RealtimeStreamConstants.DefaultLatencyFrames, master, log.CreateLogger<RealtimeStreamTransport>());
        await transport.StartAsync(CancellationToken.None);
        Assert.Equal(TimingProtocol.Ptp, transport.TimingProtocol);
        Assert.Equal(0, transport.TimingPort);
        log.Write($"transport started: grandmaster {master.ClockId:X16} on UDP {master.EventPort}/{master.GeneralPort}, control port {transport.ControlPort}");

        var connection = await RtspConnection.ConnectAsync(new IPEndPoint(host, port), new RtspConnectionOptions(), log.CreateLogger("rtsp"), CancellationToken.None);
        await using var session = new ReceiverSession(receiver, connection, new HapPairingClient(log.CreateLogger<HapPairingClient>()), ReceiverQuirks.ForReceiver(receiver), log.CreateLogger<ReceiverSession>());
        IPAddress? peer = null;
        try
        {
            await session.GetInfoAsync(false, CancellationToken.None);
            var paired = await session.PairAsync(new InMemoryPairingStore(), null, CancellationToken.None);
            var setup = await session.SetupSessionAsync(PtpSession(master), CancellationToken.None);
            await session.ConnectEventChannelAsync(CancellationToken.None);
            await session.RecordAsync(CancellationToken.None);
            peer = await PtpSessionWiring.AttachAsync(session, setup, transport.AddTimingPeer, transport.RemoveTimingPeer, null, CancellationToken.None);
            var stream = await session.SetupStreamAsync(new StreamSetupRequest(StreamType.Realtime, paired.DefaultStreamKey, transport.ControlPort), CancellationToken.None);
            log.Write($"PTP peer {peer}; stream SETUP: data {stream.DataPort}, control {stream.ControlPort}");
            await session.SetVolumeAsync(-30.0, CancellationToken.None);
            transport.AddReceiver("sps", new ReceiverStreamEndpoint(new IPEndPoint(host, stream.DataPort), new IPEndPoint(host, stream.ControlPort), paired.DefaultStreamKey, 0, false));
            await Task.Delay(1500);

            var frame = new AlacEncoder().Encode(new short[RealtimeStreamConstants.FramesPerPacket * 2]);
            var total = (int)(Seconds("PTP_SILENCE_SECONDS", 3) * RealtimeStreamConstants.SampleRate / RealtimeStreamConstants.FramesPerPacket);
            var sequence = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue);
            var timestamp = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
            var start = Stopwatch.StartNew();
            for (var sent = 0; sent < total;)
            {
                var due = Math.Min(total, (int)(start.Elapsed.TotalSeconds * RealtimeStreamConstants.SampleRate / RealtimeStreamConstants.FramesPerPacket) + 1);
                for (; sent < due; sent++)
                {
                    transport.Send(sequence++, timestamp, frame, clock.NtpNow);
                    timestamp += RealtimeStreamConstants.FramesPerPacket;
                }

                await Task.Delay(2);
            }

            await Task.Delay(1000);
            log.Write($"transport: {transport.PacketsSent} packets, {transport.SyncsSent} syncs, {transport.SendErrors} send errors; grandmaster {master.GetStatistics()}");
            Assert.True(transport.PacketsSent >= total);
            Assert.True(transport.SyncsSent >= 3);
            Assert.Equal(0, transport.SendErrors);
            Assert.Equal(peer, Assert.Single(master.Peers).Address);
        }
        finally
        {
            transport.RemoveReceiver("sps");
            var result = await session.TeardownWithResultAsync(CancellationToken.None);
            log.Write($"TEARDOWN: session {result.SessionResponse?.StatusCode.ToString(CultureInfo.InvariantCulture) ?? "-"}");
            if (peer is not null)
            {
                transport.RemoveTimingPeer(peer);
            }

            Report(master, log);
        }
    }

    /// <summary>
    /// P17 during a P1-PTP control probe on the live receiver (SPEC.md P17, A-Q8): which PTP messages does its slave send us, at what
    /// rate, and does it need Delay_Resp? No audio is sent (docs/DEVELOPMENT.md, "Live-device rules": pairing, SETUP, event channel, TEARDOWN).
    /// </summary>
    [Fact]
    [Trait("Category", "ReceiverLive")]
    public async Task Live_P17_ptp_capture_during_ptp_control_probe()
    {
        if (!Live())
        {
            return;
        }

        if (!LiveTarget.TryHost(output, out var host))
        {
            return;
        }

        var receiver = new DiscoveredReceiver("02:1A:2B:3C:4D:5E", "Test Speaker ABCD", [host], LiveTarget.Port, AirplayTxtRecord.Parse(SpeakerAAirplay), null, DateTimeOffset.UtcNow);
        using var log = new SessionLiveLog("P17-ptp-capture", output);
        var clock = new MasterClock();
        await using var master = new PtpMaster(clock, log.CreateLogger<PtpMaster>(), PtpOptions() with { PeerTimeout = Timeout.InfiniteTimeSpan });
        await master.StartAsync(CancellationToken.None);
        log.Write($"grandmaster {master.ClockId:X16} on UDP {master.EventPort}/{master.GeneralPort}");
        var connection = await RtspConnection.ConnectAsync(new IPEndPoint(host, LiveTarget.Port), new RtspConnectionOptions { RequestTimeout = TimeSpan.FromSeconds(30) }, log.CreateLogger("rtsp"), CancellationToken.None);
        await using var session = new ReceiverSession(receiver, connection, new HapPairingClient(log.CreateLogger<HapPairingClient>()), ReceiverQuirks.ForReceiver(receiver), log.CreateLogger<ReceiverSession>());
        using var control = new UdpClient(0);
        IPAddress? peer = null;
        try
        {
            await session.GetInfoAsync(false, CancellationToken.None);
            var paired = await session.PairAsync(new InMemoryPairingStore(), null, CancellationToken.None);
            log.Write($"paired: {paired.Kind}");
            var setup = await session.SetupSessionAsync(PtpSession(master), CancellationToken.None);
            log.Write($"PTP session SETUP: eventPort {setup.EventPort}, timingPort {setup.TimingPort}, timingPeerInfo [{string.Join(", ", setup.TimingPeerAddresses)}], keys [{string.Join(", ", setup.Raw.Keys)}]");
            await session.ConnectEventChannelAsync(CancellationToken.None);
            await session.RecordAsync(CancellationToken.None);
            peer = await PtpSessionWiring.AttachAsync(session, master, setup, null, CancellationToken.None);
            log.Write($"SETPEERS sent; PTP peer {peer}");
            var stream = await session.SetupStreamAsync(new StreamSetupRequest(StreamType.Realtime, paired.DefaultStreamKey, ((IPEndPoint)control.Client.LocalEndPoint!).Port), CancellationToken.None);
            log.Write($"stream SETUP: data {stream.DataPort}, control {stream.ControlPort}, streamID {stream.StreamId?.ToString(CultureInfo.InvariantCulture) ?? "none"}");
            var seconds = Seconds("P17_SECONDS", 20);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                await Task.Delay(2000);
                var feedback = await session.FeedbackAsync(CancellationToken.None);
                var p = master.Peers.FirstOrDefault();
                log.Write($"t={watch.Elapsed.TotalSeconds:0.0}s feedback {(feedback is null ? "empty" : "ok")}; peer received {p?.MessagesReceived ?? 0}, last seen {p?.SinceLastSeen.TotalSeconds ?? -1:0.0}s ago; {master.GetStatistics()}");
            }
        }
        finally
        {
            var result = await session.TeardownWithResultAsync(CancellationToken.None);
            log.Write($"TEARDOWN: session {result.SessionResponse?.StatusCode.ToString(CultureInfo.InvariantCulture) ?? "-"}");
            if (peer is not null)
            {
                master.RemovePeer(peer);
            }

            Report(master, log);
        }
    }

    private static PtpMasterOptions PtpOptions()
    {
        var options = new PtpMasterOptions
        {
            EventPort = (int)Seconds("PTP_EVENT_PORT", PtpMessages.EventPort),
            GeneralPort = (int)Seconds("PTP_GENERAL_PORT", PtpMessages.GeneralPort),
        };
        var timeout = Seconds("PTP_PEER_TIMEOUT_SECONDS", -1);
        return timeout switch
        {
            0 => options with { PeerTimeout = Timeout.InfiniteTimeSpan },
            > 0 => options with { PeerTimeout = TimeSpan.FromSeconds(timeout) },
            _ => options,
        };
    }

    private static SessionSetupRequest PtpSession(PtpMaster master) => new(
        SessionTestSupport.Sender with { Name = "airplay2-streamer PTP live test" },
        TimingProtocol.Ptp,
        0,
        Guid.NewGuid().ToString().ToUpperInvariant(),
        null,
        GroupContainsGroupLeader: false,
        IsMultiSelectAirPlay: true,
        TimingPeerAddresses: null,
        PtpClockId: master.ClockId);

    /// <summary>Logs the grandmaster's counters, its peers and every event it recorded (first message of each type per peer: P17).</summary>
    private static void Report(PtpMaster master, SessionLiveLog log)
    {
        log.Write($"RESULT P17: grandmaster {master.GetStatistics()}");
        foreach (var p in master.Peers)
        {
            log.Write($"RESULT P17: peer {p.Address} received {p.MessagesReceived}, identity {p.PortIdentity?.ToString() ?? "none"}");
        }

        while (master.Events.TryRead(out var e))
        {
            log.Write($"RESULT P17: event {e}");
        }
    }

    /// <summary>3 s of ALAC silence over the realtime stream with 0xD7 syncs (first 0x90 + marker packet, then one per second).</summary>
    private static async Task<(int Packets, int Syncs)> StreamSilenceAsync(IMasterClock clock, PtpMaster master, byte[] key, IPEndPoint data, IPEndPoint controlTarget, TimeSpan duration, SessionLiveLog log)
    {
        const int latency = RealtimeStreamConstants.DefaultLatencyFrames;
        var encoder = new AlacEncoder();
        var frame = encoder.Encode(new short[RealtimeStreamConstants.FramesPerPacket * 2]);
        using var builder = new RtpAudioPacketBuilder(key, (uint)RandomNumberGenerator.GetInt32(int.MaxValue));
        using var socket = new Socket(data.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        var packet = new byte[RealtimeStreamConstants.MaxPacketSize];
        var sync = new byte[PtpSyncPacket.Size];
        var sequence = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue);
        var timestamp = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
        var total = (int)(duration.TotalSeconds * RealtimeStreamConstants.SampleRate / RealtimeStreamConstants.FramesPerPacket);
        var packets = 0;
        var syncs = 0;
        var framesSinceSync = 0;
        var start = Stopwatch.StartNew();
        while (packets < total)
        {
            var due = (int)(start.Elapsed.TotalSeconds * RealtimeStreamConstants.SampleRate / RealtimeStreamConstants.FramesPerPacket) + 1;
            while (packets < Math.Min(due, total))
            {
                if (packets == 0 || framesSinceSync >= RealtimeStreamConstants.SyncIntervalFrames)
                {
                    new PtpSyncPacket(packets == 0, timestamp - latency, clock.PtpNowNanos, timestamp, master.ClockId).Write(sync);
                    await socket.SendToAsync(sync, SocketFlags.None, controlTarget);
                    syncs++;
                    framesSinceSync = 0;
                }

                var length = builder.Build(packet, sequence, timestamp, packets == 0, frame);
                await socket.SendToAsync(packet.AsMemory(0, length), SocketFlags.None, data);
                packets++;
                sequence++;
                timestamp += RealtimeStreamConstants.FramesPerPacket;
                framesSinceSync += RealtimeStreamConstants.FramesPerPacket;
            }

            await Task.Delay(2);
        }

        log.Write($"silence streamed in {start.Elapsed.TotalSeconds:0.000} s");
        return (packets, syncs);
    }

    private bool Live()
    {
        if (!LiveEnabled)
        {
            output.WriteLine("AIRPLAY_LIVE not set");
            return false;
        }

        return true;
    }

    private static double Seconds(string variable, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(variable), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
