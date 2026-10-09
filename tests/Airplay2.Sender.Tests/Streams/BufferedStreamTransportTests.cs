using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Airplay2.Sender.Alac;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Airplay2.Sender.Tests.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Airplay2.Sender.Tests.Streams;

/// <summary>
/// <see cref="BufferedStreamTransport"/> against the loopback buffered reader and a recording session (WORK-PLAN.md
/// buffered-stream "Verify": fake-receiver buffered reader): TCP packets, the 23-bit extension, anchoring with the first
/// packet (SPEC-audio.md 4.3), flushing (4.5), run-ahead flow control (4.2), failures.
/// </summary>
public sealed class BufferedStreamTransportTests
{
    private const int L = RealtimeStreamConstants.DefaultLatencyFrames;
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Packets_arrive_over_tcp_decrypted_in_order_and_the_first_one_anchors_the_receiver()
    {
        await using var h = await Harness.StartAsync();
        var (session, reader) = await h.AddAsync("rx");

        var input = TestPcm.Sweep(352 * 20);
        var ntp = h.Clock.NtpNow;
        h.SendPcm(input, firstSequence: 65530, firstTimestamp: 1000, ntp);

        var packets = await reader.ReadAsync(20);
        Assert.All(packets, p => Assert.True(p.Marker));
        Assert.All(packets, p => Assert.Equal(BufferedAudioPacket.SsrcAlac44100S16, p.Ssrc));
        Assert.Equal(Enumerable.Range(0, 20).Select(i => (uint)(65530 + i)).ToArray(), packets.Select(p => p.Sequence).ToArray()); // past 65535: 23 bits
        Assert.Equal(Enumerable.Range(0, 20).Select(i => 1000u + (uint)(352 * i)).ToArray(), packets.Select(p => p.Timestamp).ToArray());
        Assert.Equal(input, packets.SelectMany(p => p.Pcm!).ToArray());
        Assert.Equal(1, reader.Connections);

        var anchor = await session.NextCallAsync();
        Assert.Equal("SETRATEANCHORTIME", anchor.Method);
        Assert.Equal(1, anchor.Rate);
        Assert.Equal(1000u, anchor.RtpTime);
        Assert.Equal(h.Ptp.ClockId, anchor.TimelineId);
        var expected = (ulong)NtpTimestamp.ToUnixNanos(ntp) + (ulong)(L * 1_000_000_000L / 44100);
        Assert.InRange((long)(BufferedAssert.Nanos(anchor) - expected), -5_000, 5_000); // MasterClock: PTP time = Unix time
        Assert.Empty(await session.DrainCallsAsync(TimeSpan.FromMilliseconds(200))); // one anchor, not one per packet
        Assert.Equal(20, h.Transport.PacketsSent);
        await WaitForAsync(() => h.Transport.BytesWritten == packets.Sum(p => (long)p.Length));
    }

    [Fact]
    public async Task A_receiver_offset_moves_its_anchor_and_a_late_joiner_shares_the_timeline()
    {
        await using var h = await Harness.StartAsync();
        var (a, readerA) = await h.AddAsync("a");
        var frame = Frame();
        var ntp = h.Clock.NtpNow;
        h.Transport.Send(100, 50_000, frame, ntp);
        var anchorA = await a.NextCallAsync();

        // b joins 10 packets later with an offset of -4410 frames (its output lags by 0.1 s).
        var (b, readerB) = await h.AddAsync("b", offsetFrames: -4410);
        var later = NtpTimestamp.FromUnixNanos(NtpTimestamp.ToUnixNanos(ntp) + (10 * 352 * 1_000_000_000L / 44100));
        h.Transport.Send(110, 50_000 + (10 * 352), frame, later);
        var anchorB = await b.NextCallAsync();
        Assert.Equal(50_000u + 3520, anchorB.RtpTime);

        // Both anchors describe one timeline: frame x plays at anchor + (x - rtpTime) / 44100, b 0.1 s earlier.
        var bFrameAtA = (long)BufferedAssert.Nanos(anchorB) - (3520 * 1_000_000_000L / 44100) + (4410 * 1_000_000_000L / 44100);
        Assert.InRange(bFrameAtA - (long)BufferedAssert.Nanos(anchorA), -5_000, 5_000);
        Assert.Equal(110u, Assert.Single(await readerB.ReadAsync(1)).Sequence);
        Assert.Equal(2, (await readerA.ReadAsync(2)).Count);
    }

    [Fact]
    public async Task A_flush_sends_rate_0_and_flushbuffered_with_the_23_bit_sequence_drops_queued_packets_and_reanchors()
    {
        await using var h = await Harness.StartAsync();
        var (session, reader) = await h.AddAsync("rx");
        h.Transport.SetAudioBufferSize("rx", 256L << 20);
        reader.Pause();

        // Enough data to back up in TCP while the reader is paused.
        var frame = RandomNumberGenerator.GetBytes(1400);
        var ntp = h.Clock.NtpNow;
        ushort sequence = 65000;
        uint timestamp = 7;
        for (var i = 0; i < 6000; i++)
        {
            h.Transport.Send(sequence++, timestamp, frame, ntp);
            timestamp += 352;
        }

        Assert.Equal(1, (await session.NextCallAsync()).Rate);
        await Task.Delay(200);
        var wire = h.Transport.WireSequence(sequence);
        Assert.Equal(65000u + 6000, wire); // 16-bit sequence 4464 extended past the wrap
        await h.Transport.FlushReceiverAsync("rx", sequence, timestamp, None);

        var pause = await session.NextCallAsync();
        Assert.Equal(("SETRATEANCHORTIME", 0), (pause.Method, pause.Rate));
        var flush = await session.NextCallAsync();
        Assert.Equal("FLUSHBUFFERED", flush.Method);
        Assert.Equal((wire, timestamp, (uint?)null), (flush.UntilSeq, flush.UntilTs, flush.FromSeq));

        // The next packet re-anchors at its own timestamp; the queued pre-flush packets never reach the socket.
        h.Transport.Send(sequence, timestamp, frame, h.Clock.NtpNow);
        var anchor = await session.NextCallAsync();
        Assert.Equal((1, timestamp), (anchor.Rate, anchor.RtpTime));
        reader.Resume();
        var seen = await reader.DrainAsync(TimeSpan.FromMilliseconds(500));
        Assert.True(h.Transport.PacketsDiscarded > 0, "nothing was discarded");
        Assert.Equal(6001 - h.Transport.PacketsDiscarded, seen.Count);
        Assert.Equal(wire, seen[^1].Sequence);
        Assert.All(seen[..^1], p => Assert.True(BufferedAudioPacket.Distance(p.Sequence, wire) < 0));
    }

    [Fact]
    public async Task A_full_window_drops_packets_and_frees_as_the_anchored_audio_plays()
    {
        await using var h = await Harness.StartAsync(new BufferedStreamOptions { LatencyFrames = 4410 }); // anchor 0.1 s ahead
        var (_, reader) = await h.AddAsync("rx");
        h.Transport.SetAudioBufferSize("rx", 100_000); // window max(64 KiB, 75 000)
        var frame = new byte[1000];
        var ntp = h.Clock.NtpNow;
        uint timestamp = 0;
        for (ushort i = 0; i < 100; i++)
        {
            h.Transport.Send(i, timestamp, frame, ntp); // 100 x 1038 bytes > 75 000; timestamps cover 0.8 s
            timestamp += 352;
        }

        Assert.True(h.Transport.PacketsDropped > 0);
        var status = Assert.Single(h.Transport.ReceiverStates);
        Assert.Equal(75_000, status.Window);
        Assert.True(status.BufferedBytes <= 75_000);
        Assert.Contains(await EventsAsync(h.Transport), e => e is BufferedStreamEvent.WindowFull { ReceiverId: "rx" });

        // The window frees as the anchored audio plays (0.1 s lead, ~8 ms per packet).
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await h.Transport.WaitForWindowAsync(30_000, None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(watch.Elapsed > TimeSpan.FromMilliseconds(50), $"returned after {watch.Elapsed.TotalMilliseconds} ms");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => h.Transport.WaitForWindowAsync(80_000, None).AsTask());
        Assert.Equal(100 - h.Transport.PacketsDropped, (await reader.DrainAsync(TimeSpan.FromMilliseconds(300))).Count);
    }

    [Fact]
    public async Task An_explicit_anchor_lets_a_producer_run_ahead_and_applies_to_receivers_that_join_later()
    {
        await using var h = await Harness.StartAsync();
        var (a, readerA) = await h.AddAsync("a");
        var anchorTime = h.Clock.PtpNowNanos + 200_000_000UL;

        // The anchor goes out once audio is written (AnchorAfterAudio): start pushing before awaiting it.
        var anchoring = h.Transport.AnchorAsync(5000, anchorTime, None);

        // Run ahead: 3 s of audio as fast as the window allows, ntpNow ignored under the explicit anchor.
        var frame = Frame();
        uint timestamp = 5000;
        for (ushort i = 0; i < 376; i++)
        {
            await h.Transport.WaitForWindowAsync(BufferedAudioPacket.PacketLength(frame.Length), None);
            h.Transport.Send(i, timestamp, frame, 0);
            timestamp += 352;
        }

        await anchoring.WaitAsync(TimeSpan.FromSeconds(1)); // well inside AnchorAudioWait: released by the audio
        var call = await a.NextCallAsync();
        Assert.Equal((1, 5000u), (call.Rate, call.RtpTime));
        Assert.InRange((long)(BufferedAssert.Nanos(call) - anchorTime), -1, 0);
        Assert.Equal(376, (await readerA.ReadAsync(376)).Count);
        Assert.Empty(await a.DrainCallsAsync(TimeSpan.FromMilliseconds(100)));

        var (b, _) = await h.AddAsync("b");
        h.Transport.Send(376, timestamp, frame, 0);
        var callB = await b.NextCallAsync();
        Assert.Equal(5000u, callB.RtpTime); // the group's explicit anchor, not this packet
        Assert.Equal(BufferedAssert.Nanos(call), BufferedAssert.Nanos(callB));

        // Pause: rate 0 to everyone; the next packet re-anchors from itself.
        await h.Transport.PauseAsync(None);
        Assert.Equal(0, (await a.NextCallAsync()).Rate);
        Assert.Equal(0, (await b.NextCallAsync()).Rate);
        h.Transport.Send(377, timestamp + 352, frame, h.Clock.NtpNow);
        Assert.Equal(timestamp + 352, (await a.NextCallAsync()).RtpTime);
    }

    [Fact]
    public async Task An_anchor_waits_for_audio_on_the_data_connection_but_not_forever()
    {
        await using var h = await Harness.StartAsync(new BufferedStreamOptions { AnchorAudioWait = TimeSpan.FromMilliseconds(400) });
        var (session, reader) = await h.AddAsync("rx");
        await WaitForAsync(() => h.Transport.ReceiverStates.Single().DataConnected);

        // No audio: the anchor is held for AnchorAudioWait, then sent anyway.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await h.Transport.AnchorAsync(0, h.Clock.PtpNowNanos + 1_000_000_000UL, None);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(350), $"answered after {watch.Elapsed.TotalMilliseconds} ms");
        Assert.Equal(1, (await session.NextCallAsync()).Rate);

        // With audio: released as soon as the first packet is written, and the packet reached the reader first.
        await h.Transport.PauseAsync(None); // clears the explicit anchor
        Assert.Equal(0, (await session.NextCallAsync()).Rate);
        watch.Restart();
        h.Transport.Send(1, 352, Frame(), h.Clock.NtpNow);
        var anchor = await session.NextCallAsync();
        Assert.Equal(352u, anchor.RtpTime);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(350), $"anchored after {watch.Elapsed.TotalMilliseconds} ms");
        Assert.Single(await reader.ReadAsync(1));

        // Disabled: no wait at all.
        await using var free = await Harness.StartAsync(new BufferedStreamOptions { AnchorAfterAudio = false, AnchorAudioWait = TimeSpan.FromSeconds(5) });
        var (freeSession, _) = await free.AddAsync("rx");
        watch.Restart();
        await free.Transport.AnchorAsync(0, free.Clock.PtpNowNanos, None);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(1, (await freeSession.NextCallAsync()).Rate);
    }

    [Fact]
    public async Task Deferred_and_group_flushes_carry_23_bit_sequence_numbers()
    {
        await using var h = await Harness.StartAsync(new BufferedStreamOptions { PauseBeforeFlush = false });
        var (session, _) = await h.AddAsync("rx");
        var frame = Frame();
        for (var i = 0; i < 10; i++)
        {
            h.Transport.Send((ushort)(65530 + i), (uint)(i * 352), frame, h.Clock.NtpNow);
        }

        await session.NextCallAsync(); // anchor
        await h.Transport.FlushRangeAsync(65535, 5 * 352, 3, 9 * 352, None);
        var deferred = await session.NextCallAsync();
        Assert.Equal(("FLUSHBUFFERED", 65535u, 5u * 352, 65536u + 3, 9u * 352), (deferred.Method, deferred.FromSeq!.Value, deferred.FromTs!.Value, deferred.UntilSeq, deferred.UntilTs));

        await h.Transport.FlushAsync(None);
        var immediate = await session.NextCallAsync();
        Assert.Equal(("FLUSHBUFFERED", 65540u, 10u * 352, (uint?)null), (immediate.Method, immediate.UntilSeq, immediate.UntilTs, immediate.FromSeq));
        Assert.Equal(65540u, h.Transport.NextWireSequence);
        Assert.Equal(2, h.Transport.FlushesSent);
    }

    [Fact]
    public async Task Control_failures_and_timeouts_surface_on_the_awaited_flush_and_as_events()
    {
        await using var h = await Harness.StartAsync(new BufferedStreamOptions { ControlTimeout = TimeSpan.FromMilliseconds(200) });
        var (session, _) = await h.AddAsync("rx");
        h.Transport.Send(1, 0, Frame(), h.Clock.NtpNow);
        await session.NextCallAsync();

        session.Failure = new IOException("connection reset");
        await Assert.ThrowsAsync<IOException>(() => h.Transport.FlushReceiverAsync("rx", 2, 352, None));
        session.Failure = null;
        session.Delay = TimeSpan.FromSeconds(2);
        await Assert.ThrowsAsync<TimeoutException>(() => h.Transport.FlushReceiverAsync("rx", 2, 352, None));
        Assert.True(h.Transport.ControlErrors >= 3); // rate 0 + flush, then the timed-out rate 0 (and flush)
        Assert.Contains(await EventsAsync(h.Transport), e => e is BufferedStreamEvent.ControlFailed { Request: "FLUSHBUFFERED" });

        // Without a session: no anchor can be sent.
        session.Delay = TimeSpan.Zero;
        var reader = new BufferedTestReceiver();
        await using var _ = reader;
        h.Transport.AddReceiver("orphan", new ReceiverStreamEndpoint(reader.Endpoint, new IPEndPoint(IPAddress.Loopback, 0), RandomNumberGenerator.GetBytes(32), 0, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Transport.FlushReceiverAsync("orphan", 1, 0, None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Transport.AnchorAsync(0, h.Clock.PtpNowNanos, None));
    }

    [Fact]
    public async Task A_data_port_that_refuses_fails_the_receiver_and_its_audio_is_dropped()
    {
        await using var h = await Harness.StartAsync(new BufferedStreamOptions { ConnectAttempts = 2, ConnectRetryDelay = TimeSpan.FromMilliseconds(50), ConnectTimeout = TimeSpan.FromSeconds(2) });
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var closedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var session = new RecordingSession("dead");
        h.Transport.AttachSession("dead", session);
        h.Transport.AddReceiver("dead", new ReceiverStreamEndpoint(new IPEndPoint(IPAddress.Loopback, closedPort), new IPEndPoint(IPAddress.Loopback, 0), RandomNumberGenerator.GetBytes(32), 0, false));
        await WaitForAsync(() => h.Transport.ReceiverStates.Single().Failed);
        h.Transport.Send(1, 0, Frame(), h.Clock.NtpNow);
        Assert.Equal(1, h.Transport.PacketsDropped);
        Assert.Contains(await EventsAsync(h.Transport), e => e is BufferedStreamEvent.DataClosed { ReceiverId: "dead" });
    }

    [Fact]
    public async Task Removing_a_receiver_closes_its_data_connection_and_resync_reanchors()
    {
        await using var h = await Harness.StartAsync();
        var (session, reader) = await h.AddAsync("rx");
        h.Transport.Send(1, 0, Frame(), h.Clock.NtpNow);
        await reader.ReadAsync(1);
        await session.NextCallAsync();
        h.Transport.Resync("rx");
        h.Transport.Send(2, 352, Frame(), h.Clock.NtpNow);
        Assert.Equal(352u, (await session.NextCallAsync()).RtpTime);
        Assert.Contains(await EventsAsync(h.Transport), e => e is BufferedStreamEvent.DataConnected { ReceiverId: "rx" });

        h.Transport.RemoveReceiver("rx");
        await reader.WaitClosedAsync();
        Assert.Empty(h.Transport.ReceiverStates);
        h.Transport.Send(3, 704, Frame(), h.Clock.NtpNow); // nobody left: nothing happens
        Assert.Equal(2, h.Transport.PacketsSent);
    }

    [Fact]
    public async Task Start_is_required_idempotent_and_timing_peers_are_reference_counted()
    {
        var clock = new MasterClock();
        var ptp = new StubPtpMaster();
        await using var control = new ControlPort(bindAddress: IPAddress.Loopback);
        await using var transport = new BufferedStreamTransport(clock, ptp, control);
        Assert.Equal((StreamType.Buffered, TimingProtocol.Ptp, 0), (transport.Type, transport.TimingProtocol, transport.TimingPort));
        Assert.Throws<InvalidOperationException>(() => transport.AddReceiver("x", new ReceiverStreamEndpoint(new IPEndPoint(IPAddress.Loopback, 1), new IPEndPoint(IPAddress.Loopback, 1), new byte[32], 0, false)));
        Assert.Throws<InvalidOperationException>(() => transport.Send(0, 0, new byte[1], 0));
        await transport.StartAsync(None);
        await transport.StartAsync(None);
        Assert.Equal(1, ptp.Starts);
        Assert.NotEqual(0, transport.ControlPort);
        Assert.Throws<ArgumentException>(() => transport.AddReceiver("x", new ReceiverStreamEndpoint(new IPEndPoint(IPAddress.Loopback, 1), new IPEndPoint(IPAddress.Loopback, 1), new byte[16], 0, false)));

        var peer = IPAddress.Parse("192.0.2.7");
        transport.AddTimingPeer(peer);
        transport.AddTimingPeer(peer.MapToIPv6());
        transport.RemoveTimingPeer(peer);
        Assert.Equal(["add 192.0.2.7"], ptp.Calls.ToArray());
        transport.RemoveTimingPeer(peer);
        Assert.Equal(["add 192.0.2.7", "remove 192.0.2.7"], ptp.Calls.ToArray());
        transport.AddTimingPeer(peer);
        await transport.DisposeAsync();
        Assert.Equal("remove 192.0.2.7", ptp.Calls.Last()); // released on dispose
    }

    [Fact]
    public async Task The_session_wrapper_makes_the_session_ptp_applies_the_format_and_turns_flush_into_flushbuffered()
    {
        await using var h = await Harness.StartAsync();
        var reader = new BufferedTestReceiver();
        await using var _ = reader;
        var inner = new RecordingSession("rx") { DataPort = reader.Port, AudioBufferSize = 4L << 20 };
        var factory = h.Transport.WrapSessionFactory((_, _) => Task.FromResult<IReceiverSession>(inner));
        var session = Assert.IsType<BufferedReceiverSession>(await factory(inner.Receiver, None));

        var ntpRequest = new SessionSetupRequest(SessionTestSupportSender, TimingProtocol.Ntp, 6002, "U", null, false, true, null, null);
        await session.SetupSessionAsync(ntpRequest, None);
        Assert.Equal((TimingProtocol.Ptp, 0, h.Ptp.ClockId), (inner.LastSessionSetup!.TimingProtocol, inner.LastSessionSetup.TimingPort, inner.LastSessionSetup.PtpClockId));
        Assert.Equal(IPAddress.Loopback, session.TimingPeer);
        Assert.Equal(["add 127.0.0.1"], h.Ptp.Calls.ToArray());

        var key = RandomNumberGenerator.GetBytes(32);
        var stream = await session.SetupStreamAsync(new StreamSetupRequest(StreamType.Realtime, key, h.Transport.ControlPort), None);
        Assert.Equal((StreamType.Buffered, 0x40000u, 44100), (inner.LastStreamSetup!.Type, inner.LastStreamSetup.AudioFormat, inner.LastStreamSetup.SampleRate));
        Assert.Equal(4L << 20, session.AudioBufferSize);

        reader.Key = key;
        h.Transport.AddReceiver("rx", new ReceiverStreamEndpoint(new IPEndPoint(IPAddress.Loopback, stream.DataPort), new IPEndPoint(IPAddress.Loopback, 0), key, 0, false));
        Assert.Equal(3L << 20, h.Transport.ReceiverStates.Single().Window);
        h.Transport.Send(10, 3520, Frame(), h.Clock.NtpNow);
        await reader.ReadAsync(1);
        Assert.Equal(1, (await inner.NextCallAsync()).Rate);

        await session.FlushAsync(11, 3872, None);
        Assert.Equal(0, (await inner.NextCallAsync()).Rate);
        var flush = await inner.NextCallAsync();
        Assert.Equal(("FLUSHBUFFERED", 11u, 3872u), (flush.Method, flush.UntilSeq, flush.UntilTs));
        Assert.DoesNotContain("FLUSH", inner.Log);

        await session.TeardownAsync(None);
        Assert.Equal("remove 127.0.0.1", h.Ptp.Calls.Last());
        Assert.Null(session.TimingPeer);
        await session.DisposeAsync();
        Assert.Contains("DISPOSE", inner.Log);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Transport.FlushReceiverAsync("rx", 12, 0, None));
    }

    [Fact]
    public async Task Detaching_while_an_anchor_waits_for_audio_fails_the_awaited_anchor_instead_of_hanging()
    {
        await using var h = await Harness.StartAsync(new BufferedStreamOptions { AnchorAudioWait = TimeSpan.FromSeconds(30) });
        var (session, _) = await h.AddAsync("rx");
        await WaitForAsync(() => h.Transport.ReceiverStates.Single().DataConnected);

        // No audio is sent: the anchor sits in the control loop waiting for it (AnchorAfterAudio).
        var anchoring = h.Transport.AnchorAsync(0, h.Clock.PtpNowNanos + 1_000_000_000UL, None);
        await Task.Delay(200);
        Assert.False(anchoring.IsCompleted);
        h.Transport.DetachSession("rx");
        await Assert.ThrowsAsync<ObjectDisposedException>(() => anchoring.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(await session.DrainCallsAsync(TimeSpan.FromMilliseconds(100)));
        Assert.False(h.Transport.ReceiverStates.Single().HasSession);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Transport.FlushReceiverAsync("rx", 1, 0, None));

        // The same through dispose (the transport closes every link).
        await using var other = await Harness.StartAsync(new BufferedStreamOptions { AnchorAudioWait = TimeSpan.FromSeconds(30) });
        await other.AddAsync("rx");
        await WaitForAsync(() => other.Transport.ReceiverStates.Single().DataConnected);
        var pending = other.Transport.AnchorAsync(0, other.Clock.PtpNowNanos, None);
        await Task.Delay(100);
        await other.Transport.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task An_unexpected_session_exception_fails_only_its_request_and_the_control_loop_goes_on()
    {
        await using var h = await Harness.StartAsync(new BufferedStreamOptions { PauseBeforeFlush = false });
        var (session, _) = await h.AddAsync("rx");
        h.Transport.Send(1, 0, Frame(), h.Clock.NtpNow);
        await session.NextCallAsync();

        session.Failure = new NotSupportedException("unexpected");
        await Assert.ThrowsAsync<NotSupportedException>(() => h.Transport.FlushReceiverAsync("rx", 2, 352, None).WaitAsync(TimeSpan.FromSeconds(5)));
        session.Failure = null;
        await h.Transport.FlushReceiverAsync("rx", 2, 352, None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("FLUSHBUFFERED", (await session.NextCallAsync()).Method);
        Assert.True(h.Transport.ReceiverStates.Single().HasSession);
        Assert.Contains(await EventsAsync(h.Transport), e => e is BufferedStreamEvent.ControlFailed { Request: "FLUSHBUFFERED", Error: "unexpected" });
    }

    [Fact]
    public async Task A_receiver_added_before_its_session_is_attached_is_anchored_by_the_first_packet_after_attach()
    {
        await using var h = await Harness.StartAsync();
        var reader = new BufferedTestReceiver();
        await using var _ = reader;
        var key = RandomNumberGenerator.GetBytes(32);
        reader.Key = key;
        h.Transport.AddReceiver("late", new ReceiverStreamEndpoint(reader.Endpoint, new IPEndPoint(IPAddress.Loopback, 0), key, 0, false));
        h.Transport.Send(1, 0, Frame(), h.Clock.NtpNow);
        Assert.False(h.Transport.ReceiverStates.Single().Anchored); // nothing could be sent

        var session = new RecordingSession("late");
        h.Transport.AttachSession("late", session);
        h.Transport.Send(2, 352, Frame(), h.Clock.NtpNow);
        var anchor = await session.NextCallAsync();
        Assert.Equal((1, 352u), (anchor.Rate, anchor.RtpTime));
        Assert.True(h.Transport.ReceiverStates.Single().Anchored);
        Assert.Equal(2, (await reader.ReadAsync(2)).Count);
    }

    [Fact]
    public async Task After_an_immediate_flush_the_stale_unwritten_packets_no_longer_count_against_the_window()
    {
        await using var h = await Harness.StartAsync();
        var (session, reader) = await h.AddAsync("rx");
        h.Transport.SetAudioBufferSize("rx", 16L << 20); // window 12 MiB, more than loopback TCP absorbs
        reader.Pause();

        var frame = RandomNumberGenerator.GetBytes(1400);
        var length = BufferedAudioPacket.PacketLength(frame.Length);
        var perBurst = (int)(h.Transport.ReceiverStates.Single().Window / length) - 10;
        ushort sequence = 0;
        uint timestamp = 0;
        void Burst()
        {
            var ntp = h.Clock.NtpNow;
            for (var i = 0; i < perBurst; i++)
            {
                h.Transport.Send(sequence++, timestamp, frame, ntp);
                timestamp += 352;
            }
        }

        Burst();
        Assert.Equal(0, h.Transport.PacketsDropped);
        await session.NextCallAsync(); // anchor
        await Task.Delay(300); // the writer is now stuck in a socket write the paused reader does not drain
        Assert.True(h.Transport.ReceiverStates.Single().QueuedBytes > 0, "nothing stayed queued; the test needs TCP backpressure");

        await h.Transport.FlushReceiverAsync("rx", sequence, timestamp, None);
        Assert.Equal(0, h.Transport.ReceiverStates.Single().QueuedBytes);

        // A whole window's worth right after the flush fits although the stale packets are still in the queue.
        Burst();
        Assert.Equal(0, h.Transport.PacketsDropped);
        reader.Resume();
        var seen = await reader.DrainAsync(TimeSpan.FromMilliseconds(500));
        Assert.Equal(sequence - 1, (int)seen[^1].Sequence);
        Assert.True(h.Transport.PacketsDiscarded > 0);
        Assert.Equal(0, h.Transport.ReceiverStates.Single().QueuedBytes);
    }

    [Fact]
    public async Task CreateGroupPlayer_aligns_the_players_latency_and_rejects_a_format_the_player_cannot_encode()
    {
        var clock = new MasterClock();
        var ptp = new StubPtpMaster();
        await using var control = new ControlPort(bindAddress: IPAddress.Loopback);
        var transport = new BufferedStreamTransport(clock, ptp, control, new BufferedStreamOptions { LatencyFrames = 66150 }, null);
        Assert.Equal(66150, ((IStreamLatency)transport).LatencyFrames);
        await using (var player = transport.CreateGroupPlayer((_, _) => throw new NotSupportedException(), new GroupPlayerOptions(), NullLogger<GroupPlayer>.Instance))
        {
            Assert.Equal(66150, player.Options.LatencyFrames);
            Assert.Same(transport, player.Transport);
        }

        await using var wide = new BufferedStreamTransport(clock, ptp, control, new BufferedStreamOptions { Format = BufferedAudioFormat.Alac48000S24 }, null);
        Assert.Throws<InvalidOperationException>(() => wide.CreateGroupPlayer((_, _) => throw new NotSupportedException(), new GroupPlayerOptions(), NullLogger<GroupPlayer>.Instance));
    }

    private static readonly SenderIdentity SessionTestSupportSender = new("buffered tests", "AA:BB:CC:DD:EE:03", "AA:BB:CC:DD:EE:04");

    private static byte[] Frame() => new AlacEncoder().Encode(TestPcm.Sweep(352));

    private static async Task<List<BufferedStreamEvent>> EventsAsync(BufferedStreamTransport transport)
    {
        await Task.Delay(50);
        var list = new List<BufferedStreamEvent>();
        while (transport.Events.TryRead(out var e))
        {
            list.Add(e);
        }

        return list;
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "condition not met in time");
            await Task.Delay(20);
        }
    }

    /// <summary>A started transport on loopback with a stub grandmaster; receivers are buffered readers with recording sessions.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly List<BufferedTestReceiver> _readers = [];
        private readonly AlacEncoder _encoder = new();

        private Harness(MasterClock clock, StubPtpMaster ptp, ControlPort control, BufferedStreamTransport transport)
        {
            Clock = clock;
            Ptp = ptp;
            Control = control;
            Transport = transport;
        }

        public MasterClock Clock { get; }

        public StubPtpMaster Ptp { get; }

        public ControlPort Control { get; }

        public BufferedStreamTransport Transport { get; }

        public static async Task<Harness> StartAsync(BufferedStreamOptions? options = null)
        {
            var clock = new MasterClock();
            var ptp = new StubPtpMaster();
            var control = new ControlPort(bindAddress: IPAddress.Loopback);
            var transport = new BufferedStreamTransport(clock, ptp, control, options, null);
            await transport.StartAsync(None);
            return new Harness(clock, ptp, control, transport);
        }

        public Task<(RecordingSession Session, BufferedTestReceiver Reader)> AddAsync(string id, int offsetFrames = 0)
        {
            var key = RandomNumberGenerator.GetBytes(32);
            var reader = new BufferedTestReceiver(key);
            _readers.Add(reader);
            var session = new RecordingSession(id);
            Transport.AttachSession(id, session);
            Transport.AddReceiver(id, new ReceiverStreamEndpoint(reader.Endpoint, new IPEndPoint(IPAddress.Loopback, 0), key, offsetFrames, false));
            return Task.FromResult((session, reader));
        }

        public void SendPcm(short[] pcm, ushort firstSequence, uint firstTimestamp, ulong ntp)
        {
            var sequence = firstSequence;
            var timestamp = firstTimestamp;
            for (var offset = 0; offset < pcm.Length; offset += 352 * 2)
            {
                Transport.Send(sequence++, timestamp, _encoder.Encode(pcm.AsSpan(offset, 352 * 2)), ntp);
                timestamp += 352;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Transport.DisposeAsync();
            await Control.DisposeAsync();
            foreach (var reader in _readers)
            {
                await reader.DisposeAsync();
            }
        }
    }
}
