using System.Net;
using Airplay2.Receiver.Session;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Airplay2.Receiver.Tests.Session.ControlPlaneTestSupport;

namespace Airplay2.Receiver.Tests.Session;

/// <summary>
/// Session SETUP, the event channel, RECORD, stream SETUP, <c>/feedback</c>, TEARDOWN, eviction and liveness of
/// SPEC-receiver.md 4.3-4.9, R8 and R16 against the product control plane on loopback (SPEC-receiver.md 11.1).
/// </summary>
public class SessionSetupLoopbackTests
{
    [Fact]
    public async Task Full_session_pairs_sets_up_streams_and_tears_down()
    {
        var (plane, host) = await StartAsync();
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectPairedAsync(plane);

        // 4.3: PTP session SETUP; the event listener is accepting before the answer.
        var setup = await sender.SetUpSessionAsync();
        var eventPort = (int)setup.GetInt64("eventPort")!.Value;
        Assert.InRange(eventPort, 1, 65535);
        Assert.Equal(0L, setup.GetInt64("timingPort"));
        var peerInfo = setup.GetDictionary("timingPeerInfo")!;
        Assert.Equal("127.0.0.1", peerInfo.GetArray("Addresses")![0]);
        Assert.Equal("127.0.0.1", peerInfo.GetString("ID"));

        var connected = await host.WaitForEventAsync<ReceiverEvent.SenderConnected>();
        Assert.Equal(ControlPlaneTestSupport.Sender.Name, connected.Sender.Name);
        Assert.Equal(ControlPlaneTestSupport.Sender.Model, connected.Sender.Model);
        Assert.Equal(ControlPlaneTestSupport.Sender.DeviceId, connected.Sender.DeviceId);
        Assert.Equal(IPAddress.Loopback, connected.Sender.Address);
        var timing = Assert.Single(host.Timings);
        Assert.Equal(SenderClockId, timing.Peer.ClockId);
        Assert.Equal([IPAddress.Loopback], timing.Peer.Addresses);
        var session = Assert.Single(plane.Sessions);
        Assert.True(session.IsSessionConnection);
        Assert.Equal(ControlPlaneTestSupport.Sender.Name, session.Sender!.Name);
        Assert.Empty(host.EventsOf<ReceiverEvent.SenderStreamStarted>()); // R8: no claim before the stream SETUP

        // 4.4: the event channel is HAP-encrypted with the event keys and carries updateInfo on accept.
        await using var events = await EventChannel.ConnectAsync(new IPEndPoint(IPAddress.Loopback, eventPort), sender.Pairing!.EventKeys, new EventChannelOptions(), NullLogger.Instance, CancellationToken.None);
        using (var wait = new CancellationTokenSource(Wait))
        {
            var message = await events.Messages.ReadAsync(wait.Token);
            var update = Assert.IsType<ReceiverEventMessage.UpdateInfo>(message);
            Assert.Equal("Loopback Receiver", update.Value.GetString("name"));
            Assert.NotNull(update.Value.GetData("txtAirPlay"));
        }

        // 4.5
        var record = await sender.SendAsync(SessionRequests.Record(sender.Uri));
        Assert.Equal("0", record.Header(RtspHeaders.AudioLatency));
        Assert.Equal(200, (await sender.SendAsync(SessionRequests.SetPeers(sender.Uri, [IPAddress.Parse("192.0.2.10"), IPAddress.Loopback]))).StatusCode);
        Assert.Contains(IPAddress.Parse("192.0.2.10"), timing.Peers);
        Assert.Equal(0x4L, (await sender.SendAsync(SessionRequests.Info(false))).PlistBody().GetInt64("statusFlags"));
        Assert.Equal(200, (await sender.SendAsync(SessionRequests.Feedback())).StatusCode);

        // 4.6: stream SETUP 96 is the claim; status flag 17 while it exists.
        var stream = await sender.SetUpRealtimeStreamAsync();
        Assert.Equal(96L, stream.GetInt64("type"));
        var fake = Assert.Single(host.Streams);
        Assert.Equal(fake.DataPort, (int)stream.GetInt64("dataPort")!.Value);
        Assert.Equal(fake.ControlPort, (int)stream.GetInt64("controlPort")!.Value);
        var streamId = (uint)stream.GetInt64("streamID")!.Value;
        Assert.True(streamId >= 1);
        Assert.Equal(sender.Pairing.DefaultStreamKey, fake.Request.SharedKey);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 6001), fake.Request.SenderControl);
        var started = await host.WaitForEventAsync<ReceiverEvent.SenderStreamStarted>();
        Assert.Equal(StreamType.Realtime, started.StreamType);
        Assert.Equal("ALAC/44100/16/2", started.Format);
        Assert.True(plane.HasActiveStream);
        Assert.Equal(0x4L | (1L << 17), (await sender.SendAsync(SessionRequests.Info(false))).PlistBody().GetInt64("statusFlags"));

        // 4.8: feedback reports the stream while it exists.
        var feedback = (await sender.SendAsync(SessionRequests.Feedback())).PlistBody();
        var entry = (PlistDictionary)feedback.GetArray("streams")![0]!;
        Assert.Equal(96L, entry.GetInt64("type"));
        Assert.Equal(44100.0, entry.GetDouble("sr"));

        // 4.9: TEARDOWN of the stream keeps the session; output released, flag 17 cleared.
        Assert.Equal(200, (await sender.SendAsync(SessionRequests.TeardownStream(sender.Uri, streamId, StreamType.Realtime))).StatusCode);
        Assert.True(fake.Disposed);
        Assert.Contains("release", host.Recorder.Calls);
        Assert.False(plane.HasActiveStream);
        Assert.True(session.IsSessionConnection);
        Assert.Equal(0, (await sender.SendAsync(SessionRequests.Feedback())).Body.Length);
        Assert.Empty(host.EventsOf<ReceiverEvent.SenderGone>());

        // A second stream on the same session gets the next id.
        var second = await sender.SetUpRealtimeStreamAsync();
        Assert.True((uint)second.GetInt64("streamID")!.Value > streamId);

        // TEARDOWN {} ends the session: Connection: close, stream, event channel and PTP slave closed, SenderGone.
        var teardown = await sender.SendAsync(SessionRequests.TeardownSession(sender.Uri));
        Assert.Equal(200, teardown.StatusCode);
        Assert.Equal("close", teardown.Header(RtspHeaders.Connection));
        var gone = await host.WaitForEventAsync<ReceiverEvent.SenderGone>();
        Assert.Equal("TEARDOWN", gone.Reason);
        Assert.All(host.Streams, s => Assert.True(s.Disposed));
        Assert.True(timing.Disposed);
        await sender.Connection.Closed.WaitAsync(Wait);
        await events.Closed.WaitAsync(Wait);
        await WaitUntilAsync(() => plane.Sessions.Count == 0, "the session to be forgotten");
        Assert.Single(host.EventsOf<ReceiverEvent.SenderGone>());
    }

    [Fact]
    public async Task Ntp_session_setup_is_answered_501()
    {
        var (plane, host) = await StartAsync();
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectPairedAsync(plane);

        var response = await sender.SessionSetupAsync(NtpSession());

        Assert.Equal(501, response.StatusCode);
        Assert.Empty(host.EventsOf<ReceiverEvent.SenderConnected>());
        Assert.False(plane.Sessions.Single().IsSessionConnection);
    }

    [Fact]
    public async Task Unknown_timing_protocol_is_400_and_remote_control_only_none_is_accepted_without_a_session()
    {
        var (plane, host) = await StartAsync();
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectPairedAsync(plane);

        var bogus = await sender.SendAsync(RtspRequest.WithPlist("SETUP", sender.Uri, new PlistDictionary { ["timingProtocol"] = "Carrier pigeon" }));
        var noneWithoutFlag = await sender.SendAsync(RtspRequest.WithPlist("SETUP", sender.Uri, new PlistDictionary { ["timingProtocol"] = "None" }));
        var remoteOnly = await sender.SendAsync(RtspRequest.WithPlist("SETUP", sender.Uri, new PlistDictionary { ["timingProtocol"] = "None", ["isRemoteControlOnly"] = true }));

        Assert.Equal(400, bogus.StatusCode);
        Assert.Equal(400, noneWithoutFlag.StatusCode);
        Assert.Equal(200, remoteOnly.StatusCode);
        Assert.True(remoteOnly.PlistBody().GetInt64("eventPort") > 0);
        Assert.Equal(0L, remoteOnly.PlistBody().GetInt64("timingPort"));
        Assert.False(plane.Sessions.Single().IsSessionConnection); // RQ9
        Assert.Empty(host.EventsOf<ReceiverEvent.SenderConnected>());
        Assert.Empty(host.Timings);
    }

    [Fact]
    public async Task Unpaired_setup_is_answered_470()
    {
        var (plane, host) = await StartAsync();
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectAsync(plane);

        var response = await sender.SessionSetupAsync();

        Assert.Equal(470, response.StatusCode);
        Assert.Empty(host.Timings);
    }

    [Fact]
    public async Task Stream_setup_needs_a_session_setup_first()
    {
        var (plane, host) = await StartAsync();
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectPairedAsync(plane);

        var response = await sender.StreamSetupAsync(sender.Realtime());

        Assert.Equal(451, response.StatusCode);
        Assert.Empty(host.Streams);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(0)]
    public async Task Stream_setup_with_an_shk_other_than_32_bytes_is_400(int length)
    {
        var (plane, host) = await StartAsync();
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectPairedAsync(plane);
        await sender.SetUpSessionAsync();

        var body = SessionRequests.StreamSetupBody(sender.Realtime(), sender.Connection.SessionId);
        ((PlistDictionary)body.GetArray("streams")![0]!)["shk"] = new byte[length];
        var response = await sender.StreamSetupAsync(body);

        Assert.Equal(400, response.StatusCode);
        Assert.Empty(host.Streams);
        Assert.Empty(host.EventsOf<ReceiverEvent.SenderStreamStarted>());
    }

    [Theory]
    [InlineData("spf", 1024L)]
    [InlineData("sr", 48000L)]
    [InlineData("type", 100L)]
    public async Task Stream_setup_with_a_wrong_realtime_format_or_type_is_400(string key, long value)
    {
        var (plane, host) = await StartAsync();
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectPairedAsync(plane);
        await sender.SetUpSessionAsync();

        var body = SessionRequests.StreamSetupBody(sender.Realtime(), sender.Connection.SessionId);
        ((PlistDictionary)body.GetArray("streams")![0]!)[key] = value;
        var response = await sender.StreamSetupAsync(body);

        Assert.Equal(400, response.StatusCode);
        Assert.Empty(host.Streams);
    }

    [Fact]
    public async Task Buffered_stream_setup_accepts_the_r3_formats_and_answers_the_buffer_size()
    {
        var (plane, host) = await StartAsync(host: new TestHost { AacAvailable = false });
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectPairedAsync(plane);
        await sender.SetUpSessionAsync();

        // ALAC-only variant (R3): AAC-LC is refused, ALAC accepted; an unknown format is refused.
        var aac = await sender.StreamSetupAsync(new StreamSetupRequest(StreamType.Buffered, sender.Pairing!.DefaultStreamKey, 0, CompressionType: 8, AudioFormat: 0x400000));
        var unknown = await sender.StreamSetupAsync(new StreamSetupRequest(StreamType.Buffered, sender.Pairing.DefaultStreamKey, 0, AudioFormat: 0x2000000));
        var alac = await sender.StreamSetupAsync(new StreamSetupRequest(StreamType.Buffered, sender.Pairing.DefaultStreamKey, 0));

        Assert.Equal(400, aac.StatusCode);
        Assert.Equal(400, unknown.StatusCode);
        Assert.Equal(200, alac.StatusCode);
        var stream = (PlistDictionary)alac.PlistBody().GetArray("streams")![0]!;
        Assert.Equal(103L, stream.GetInt64("type"));
        Assert.Equal(8388608L, stream.GetInt64("audioBufferSize"));
        var fake = Assert.Single(host.Streams);
        Assert.Equal(fake.DataPort, (int)stream.GetInt64("dataPort")!.Value);
        Assert.True(stream.GetInt64("controlPort") > 0);
        Assert.Equal("ALAC/44100/16/2", (await host.WaitForEventAsync<ReceiverEvent.SenderStreamStarted>()).Format);
    }

    [Fact]
    public async Task Buffered_aac_stream_is_accepted_with_a_decoder()
    {
        var (plane, host) = await StartAsync();
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectPairedAsync(plane);
        await sender.SetUpSessionAsync();

        var aac = await sender.StreamSetupAsync(new StreamSetupRequest(StreamType.Buffered, sender.Pairing!.DefaultStreamKey, 0, CompressionType: 8, AudioFormat: 0x400000));

        Assert.Equal(200, aac.StatusCode);
        var started = await host.WaitForEventAsync<ReceiverEvent.SenderStreamStarted>();
        Assert.Equal(StreamType.Buffered, started.StreamType);
        Assert.Equal("AAC-LC/44100/2", started.Format);
    }

    [Fact]
    public async Task A_failing_stream_bind_is_answered_500_and_claims_nothing()
    {
        var host = new TestHost { FailStream = _ => new InvalidOperationException("no sockets today") };
        var (plane, _) = await StartAsync(host: host);
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectPairedAsync(plane);
        await sender.SetUpSessionAsync();

        var response = await sender.StreamSetupAsync(sender.Realtime());

        Assert.Equal(500, response.StatusCode);
        Assert.False(plane.HasActiveStream);
        Assert.Empty(host.EventsOf<ReceiverEvent.SenderStreamStarted>());
    }

    [Fact]
    public async Task Second_connections_stream_setup_ends_the_first_session()
    {
        var (plane, host) = await StartAsync();
        await using var _plane = plane;
        await using var first = await TestSender.ConnectPairedAsync(plane);
        await first.SetUpSessionAsync();
        await first.SetUpRealtimeStreamAsync();
        var firstStream = Assert.Single(host.Streams);

        await using var second = await TestSender.ConnectPairedAsync(plane);
        await second.SetUpSessionAsync();

        // 4.3: both sessions exist until the second sender's stream SETUP.
        Assert.Equal(2, plane.Sessions.Count(s => s.IsSessionConnection));
        Assert.Empty(host.EventsOf<ReceiverEvent.SenderGone>());

        await second.SetUpRealtimeStreamAsync();

        // R8: the first session was ended (with the reason) before the second claimed the output.
        var gone = Assert.Single(host.EventsOf<ReceiverEvent.SenderGone>());
        Assert.Contains("another sender", gone.Reason, StringComparison.Ordinal);
        Assert.True(firstStream.Disposed);
        var order = host.Events.ToList();
        Assert.True(order.IndexOf(gone) < order.FindLastIndex(e => e is ReceiverEvent.SenderStreamStarted));
        await first.Connection.Closed.WaitAsync(Wait);
        Assert.True(Assert.Single(plane.Sessions).HasStream);
        Assert.Equal(200, (await second.SendAsync(SessionRequests.Feedback())).StatusCode);
    }

    [Fact]
    public async Task Closing_the_connection_ends_the_session()
    {
        var (plane, host) = await StartAsync();
        await using var _plane = plane;
        var sender = await TestSender.ConnectPairedAsync(plane);
        await sender.SetUpSessionAsync();
        await sender.SetUpRealtimeStreamAsync();

        await sender.DisposeAsync();

        var gone = await host.WaitForEventAsync<ReceiverEvent.SenderGone>();
        Assert.StartsWith("connection closed", gone.Reason, StringComparison.Ordinal);
        Assert.True(Assert.Single(host.Streams).Disposed);
        Assert.True(Assert.Single(host.Timings).Disposed);
        Assert.Contains("release", host.Recorder.Calls);
        await WaitUntilAsync(() => plane.Sessions.Count == 0, "the session to be forgotten");
        Assert.False(plane.HasActiveStream);
    }

    [Fact]
    public async Task Silent_sender_after_a_stream_setup_is_gone_after_the_timeout()
    {
        var (plane, host) = await StartAsync(Options(timeout: TimeSpan.FromMilliseconds(300)));
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectPairedAsync(plane);
        await sender.SetUpSessionAsync();
        await sender.SetUpRealtimeStreamAsync();

        var gone = await host.WaitForEventAsync<ReceiverEvent.SenderGone>();

        Assert.Contains("R16", gone.Reason, StringComparison.Ordinal);
        await sender.Connection.Closed.WaitAsync(Wait);
        Assert.True(Assert.Single(host.Streams).Disposed);
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task Feedback_keeps_a_session_alive_past_the_timeout()
    {
        var (plane, host) = await StartAsync(Options(timeout: TimeSpan.FromMilliseconds(600)));
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectPairedAsync(plane);
        await sender.SetUpSessionAsync();
        await sender.SetUpRealtimeStreamAsync();

        for (var i = 0; i < 12; i++)
        {
            await Task.Delay(150);
            Assert.Equal(200, (await sender.SendAsync(SessionRequests.Feedback())).StatusCode);
        }

        Assert.Empty(host.EventsOf<ReceiverEvent.SenderGone>());
        Assert.True(plane.HasActiveStream);
    }

    [Fact]
    public async Task Session_without_a_stream_is_not_timed_out()
    {
        var (plane, host) = await StartAsync(Options(timeout: TimeSpan.FromMilliseconds(200)));
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectPairedAsync(plane);
        await sender.SetUpSessionAsync();

        await Task.Delay(700);

        Assert.Empty(host.EventsOf<ReceiverEvent.SenderGone>());
        Assert.True(plane.Sessions.Single().IsSessionConnection);
    }

    [Fact]
    public async Task Body_less_teardown_ends_the_session()
    {
        var (plane, host) = await StartAsync();
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectPairedAsync(plane);
        await sender.SetUpSessionAsync();

        var response = await sender.SendAsync(RtspRequest.Create("TEARDOWN", sender.Uri));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("close", response.Header(RtspHeaders.Connection));
        Assert.NotNull(await host.WaitForEventAsync<ReceiverEvent.SenderGone>());
        await sender.Connection.Closed.WaitAsync(Wait);
    }

    [Fact]
    public async Task Teardown_with_an_empty_streams_array_ends_the_stream_and_keeps_the_session()
    {
        var (plane, host) = await StartAsync();
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectPairedAsync(plane);
        await sender.SetUpSessionAsync();
        await sender.SetUpRealtimeStreamAsync();

        var response = await sender.SendAsync(RtspRequest.WithPlist("TEARDOWN", sender.Uri, new PlistDictionary { ["streams"] = new List<object?>() }));

        Assert.Equal(200, response.StatusCode);
        Assert.Null(response.Header(RtspHeaders.Connection));
        Assert.False(plane.HasActiveStream);
        Assert.True(Assert.Single(host.Streams).Disposed);
        Assert.True(plane.Sessions.Single().IsSessionConnection);
        Assert.Empty(host.EventsOf<ReceiverEvent.SenderGone>());
    }

    [Fact]
    public async Task End_session_evicts_the_sender_and_later_requests_get_451()
    {
        var (plane, host) = await StartAsync();
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectPairedAsync(plane);
        await sender.SetUpSessionAsync();
        await sender.SetUpRealtimeStreamAsync();
        var session = plane.Sessions.Single();

        await plane.EndSessionAsync("Spotify took the output", CancellationToken.None);

        var gone = Assert.Single(host.EventsOf<ReceiverEvent.SenderGone>());
        Assert.Equal("Spotify took the output", gone.Reason);
        Assert.Contains("release", host.Recorder.Calls);
        Assert.True(session.IsEnded);
        await sender.Connection.Closed.WaitAsync(Wait);

        // 4.9: a handler that finds the session ended answers 451.
        var late = await session.HandleAsync(session.Connection, new RtspIncomingRequest("OPTIONS", "*", "RTSP/1.0", new Dictionary<string, string>(), ReadOnlyMemory<byte>.Empty), CancellationToken.None);
        Assert.Equal(451, late.StatusCode);

        // A no-op without a session.
        await plane.EndSessionAsync("again", CancellationToken.None);
        Assert.Single(host.EventsOf<ReceiverEvent.SenderGone>());
    }

    [Fact]
    public async Task Stop_ends_every_session_and_closes_the_listener()
    {
        var (plane, host) = await StartAsync();
        await using var sender = await TestSender.ConnectPairedAsync(plane);
        await sender.SetUpSessionAsync();
        var port = plane.Port;

        await plane.StopAsync("receiver stopped", CancellationToken.None);

        Assert.Equal("receiver stopped", Assert.Single(host.EventsOf<ReceiverEvent.SenderGone>()).Reason);
        await sender.Connection.Closed.WaitAsync(Wait);
        await Assert.ThrowsAnyAsync<Exception>(() => RtspConnection.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), new RtspConnectionOptions { ConnectTimeout = TimeSpan.FromSeconds(2) }, NullLogger.Instance, CancellationToken.None));
        await plane.DisposeAsync();
    }
}
