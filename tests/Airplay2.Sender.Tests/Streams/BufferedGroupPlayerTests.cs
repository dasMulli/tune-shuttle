using System.Net;
using Airplay2.FakeReceiver;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Airplay2.Sender.Tests.Playback;
using Airplay2.Sender.Tests.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using TestReceiver = Airplay2.FakeReceiver.FakeReceiver;

namespace Airplay2.Sender.Tests.Streams;

/// <summary>
/// The group player over <see cref="BufferedStreamTransport"/> as <see cref="BufferedStreamTransport.CreateGroupPlayer"/>
/// builds it (session factory wrapped by <see cref="BufferedStreamTransport.WrapSessionFactory"/>, the transport's L): the control plane is the in-process fake receiver (pairing,
/// PTP session SETUP, SETPEERS, SETRATEANCHORTIME and FLUSHBUFFERED are recorded there), the TCP data plane is the
/// buffered reader (the fake receiver does not emulate type 103, so the stream SETUP is answered by the test).
/// </summary>
public sealed class BufferedGroupPlayerTests
{
    private const int L = 66150; // not the GroupPlayerOptions default: CreateGroupPlayer must align the player to it
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Two_receivers_play_the_buffered_stream_anchor_on_the_first_packet_and_flush_with_flushbuffered()
    {
        var clock = new MasterClock();
        var ptp = new StubPtpMaster();
        await using var control = new ControlPort(bindAddress: IPAddress.Loopback);
        var transport = new BufferedStreamTransport(clock, ptp, control, new BufferedStreamOptions { LatencyFrames = L }, null);
        var fakes = new Dictionary<string, TestReceiver>();
        var readers = new Dictionary<string, BufferedTestReceiver>();
        try
        {
            foreach (var name in new[] { "A", "B" })
            {
                var fake = await SessionTestSupport.StartAsync(new FakeReceiverOptions { Name = name, SendTimingRequests = false });
                fakes[fake.DeviceId] = fake;
                readers[fake.DeviceId] = new BufferedTestReceiver();
            }

            async Task<IReceiverSession> Connect(DiscoveredReceiver receiver, CancellationToken cancellationToken) =>
                new BufferedDataRedirect(await SessionTestSupport.ConnectAndPairAsync(fakes[receiver.Id]), readers[receiver.Id]);

            await using var player = transport.CreateGroupPlayer(Connect, new GroupPlayerOptions(), NullLogger<GroupPlayer>.Instance);
            Assert.Equal(L, player.Options.LatencyFrames);
            var ids = fakes.Keys.ToArray();
            await player.AddReceiverAsync(SessionTestSupport.Describe(fakes[ids[0]]), 40, None);
            await player.AddReceiverAsync(SessionTestSupport.Describe(fakes[ids[1]]), 60, None);

            var input = TestPcm.Sweep(352 * 100);
            player.Write(input);
            await player.StartAsync(None);
            var a = await readers[ids[0]].ReadAsync(100);
            var b = await readers[ids[1]].ReadAsync(100);
            Assert.Equal(input, a.SelectMany(p => p.Pcm!).ToArray());
            Assert.Equal(input, b.SelectMany(p => p.Pcm!).ToArray());
            Assert.Equal(a.Select(p => (p.Sequence, p.Timestamp)), b.Select(p => (p.Sequence, p.Timestamp)));
            Assert.All(a.Zip(a.Skip(1)), pair => Assert.Equal(1, BufferedAudioPacket.Distance(pair.Second.Sequence, pair.First.Sequence)));
            Assert.All(a, p => Assert.Equal(BufferedAudioPacket.SsrcAlac44100S16, p.Ssrc));

            var requests = await SessionTestSupport.RequestsAsync(fakes[ids[0]]);
            Assert.Equal(["SETUP", "RECORD", "SETPEERS", "SETUP", "SET_PARAMETER", "SETRATEANCHORTIME"], requests.Select(SessionTestSupport.Line).SkipWhile(l => l != "SETUP").Where(l => l != "POST /feedback" && !l.StartsWith("GET_PARAMETER", StringComparison.Ordinal)).Take(6).ToArray());
            var session = Plist(requests.First(r => r.Method == "SETUP"));
            Assert.Equal("PTP", session.GetString("timingProtocol"));
            Assert.Equal(unchecked((long)ptp.ClockId), session.GetDictionary("timingPeerInfo")!.GetInt64("ClockID"));
            var stream = (PlistDictionary)Plist(requests.Where(r => r.Method == "SETUP").Skip(1).First()).GetArray("streams")![0]!;
            Assert.Equal((103L, 0x40000L, 44100L, 352L), (stream.GetInt64("type"), stream.GetInt64("audioFormat"), stream.GetInt64("sr"), stream.GetInt64("spf")));
            var anchor = Plist(requests.Single(r => r.Method == "SETRATEANCHORTIME"));
            Assert.Equal(1L, anchor.GetInt64("rate"));
            Assert.Equal((long)a[0].Timestamp, anchor.GetInt64("rtpTime"));
            Assert.Equal(unchecked((long)ptp.ClockId), anchor.GetInt64("networkTimeTimelineID"));
            Assert.Contains("add 127.0.0.1", ptp.Calls);

            // Pause: the group player's FLUSH becomes rate 0 + FLUSHBUFFERED until the next packet; no realtime FLUSH.
            await player.PauseAsync(None);
            var next = transport.NextWireSequence;
            var sentAtPause = transport.PacketsSent;
            requests = await SessionTestSupport.RequestsAsync(fakes[ids[0]]);
            Assert.DoesNotContain(requests, r => r.Method == "FLUSH");
            var rate0 = Plist(requests.Single(r => r.Method == "SETRATEANCHORTIME"));
            Assert.Equal(0L, rate0.GetInt64("rate"));
            var flush = Plist(requests.Single(r => r.Method == "FLUSHBUFFERED"));
            var tail = await readers[ids[0]].DrainAsync(TimeSpan.FromMilliseconds(300));
            var lastSent = tail.Count > 0 ? tail[^1].Sequence : a[^1].Sequence;
            Assert.True(
                (long)next == flush.GetInt64("flushUntilSeq") && BufferedAudioPacket.Distance(next, lastSent) >= 1,
                $"flushUntilSeq {flush.GetInt64("flushUntilSeq")}, next wire sequence {next}, last packet read {lastSent}, packets sent {sentAtPause} at the pause and {transport.PacketsSent} now");
            Assert.False(flush.ContainsKey("flushFromSeq"));
            Assert.Equal(GroupMemberState.Flushed, player.Members[ids[0]]);

            // Resume: a fresh anchor at the next timestamp, the stream continues with the next sequence number.
            var more = TestPcm.Sweep(352 * 20, seed: 7);
            player.Write(more);
            await player.StartAsync(None);
            var resumed = await readers[ids[0]].ReadAsync(20);
            Assert.Equal(next, resumed[0].Sequence);
            Assert.Equal(more, resumed.SelectMany(p => p.Pcm!).ToArray());
            requests = await SessionTestSupport.RequestsAsync(fakes[ids[0]]);
            var again = Plist(requests.First(r => r.Method == "SETRATEANCHORTIME"));
            Assert.Equal((1L, (long)resumed[0].Timestamp), (again.GetInt64("rate"), again.GetInt64("rtpTime")));
            var seconds = (ulong)again.GetInt64("networkTimeSecs")!.Value;
            var fraction = Convert.ToUInt64(again["networkTimeFrac"], System.Globalization.CultureInfo.InvariantCulture);
            var lead = (long)NetworkTime.ToNanoseconds(seconds, fraction) - (long)clock.PtpNowNanos;
            Assert.InRange(lead, 1_000_000_000L, L * 1_000_000_000L / 44100); // about L ahead of the packet's due time
        }
        finally
        {
            foreach (var fake in fakes.Values)
            {
                await fake.DisposeAsync();
            }

            foreach (var reader in readers.Values)
            {
                await reader.DisposeAsync();
            }
        }

        Assert.Contains("remove 127.0.0.1", ptp.Calls); // TEARDOWN released the grandmaster peer
        Assert.Equal(ptp.Calls.Count(c => c.StartsWith("add", StringComparison.Ordinal)), ptp.Calls.Count(c => c.StartsWith("remove", StringComparison.Ordinal)));
    }

    private static PlistDictionary Plist(FakeReceiverEvent.Request request)
    {
        var start = request.Body.AsSpan().IndexOf("bplist00"u8);
        return BinaryPlist.DecodeDictionary(request.Body.AsSpan(start));
    }

    /// <summary>
    /// Passes everything to the fake receiver's session except the buffered stream SETUP, which the fake answers 501: that
    /// one is answered here with the buffered reader's TCP port (and the reader learns the stream key).
    /// </summary>
    private sealed class BufferedDataRedirect(IReceiverSession inner, BufferedTestReceiver reader) : IReceiverSession
    {
        public DiscoveredReceiver Receiver => inner.Receiver;

        public IRtspConnection Connection => inner.Connection;

        public PairingSession? Pairing => inner.Pairing;

        public Task<StreamSetupResult> SetupStreamAsync(StreamSetupRequest request, CancellationToken cancellationToken)
        {
            Assert.Equal(StreamType.Buffered, request.Type);
            reader.Key = request.StreamKey;

            // Still record the request at the fake (as SET_PARAMETER would be): send the real SETUP and expect its 501.
            return SetupAsync(request, cancellationToken);
        }

        public Task<PlistDictionary> GetInfoAsync(bool withQualifier, CancellationToken cancellationToken) => inner.GetInfoAsync(withQualifier, cancellationToken);

        public Task<PairingSession> PairAsync(IPairingStore store, Func<CancellationToken, Task<string>>? pinPrompt, CancellationToken cancellationToken) => inner.PairAsync(store, pinPrompt, cancellationToken);

        public Task AuthSetupAsync(CancellationToken cancellationToken) => inner.AuthSetupAsync(cancellationToken);

        public Task<SessionSetupResult> SetupSessionAsync(SessionSetupRequest request, CancellationToken cancellationToken) => inner.SetupSessionAsync(request, cancellationToken);

        public Task<IEventChannel> ConnectEventChannelAsync(CancellationToken cancellationToken) => inner.ConnectEventChannelAsync(cancellationToken);

        public Task<int> RecordAsync(CancellationToken cancellationToken) => inner.RecordAsync(cancellationToken);

        public Task SetPeersAsync(IReadOnlyList<IPAddress> peers, CancellationToken cancellationToken) => inner.SetPeersAsync(peers, cancellationToken);

        public Task SetVolumeAsync(double volumeDb, CancellationToken cancellationToken) => inner.SetVolumeAsync(volumeDb, cancellationToken);

        public Task<double> GetVolumeAsync(CancellationToken cancellationToken) => inner.GetVolumeAsync(cancellationToken);

        public Task SetMetadataAsync(TrackMetadata metadata, uint startFrame, CancellationToken cancellationToken) => inner.SetMetadataAsync(metadata, startFrame, cancellationToken);

        public Task SetArtworkAsync(ReadOnlyMemory<byte> image, string contentType, uint startFrame, CancellationToken cancellationToken) => inner.SetArtworkAsync(image, contentType, startFrame, cancellationToken);

        public Task SetProgressAsync(ProgressInfo progress, CancellationToken cancellationToken) => inner.SetProgressAsync(progress, cancellationToken);

        public Task FlushAsync(ushort nextSequence, uint nextTimestamp, CancellationToken cancellationToken) => inner.FlushAsync(nextSequence, nextTimestamp, cancellationToken);

        public Task SetRateAnchorTimeAsync(int rate, uint rtpTime, ulong networkTimeSecs, ulong networkTimeFrac, ulong timelineId, CancellationToken cancellationToken) =>
            inner.SetRateAnchorTimeAsync(rate, rtpTime, networkTimeSecs, networkTimeFrac, timelineId, cancellationToken);

        public Task FlushBufferedAsync(uint flushUntilSeq, uint flushUntilTimestamp, uint? flushFromSeq, uint? flushFromTimestamp, CancellationToken cancellationToken) =>
            inner.FlushBufferedAsync(flushUntilSeq, flushUntilTimestamp, flushFromSeq, flushFromTimestamp, cancellationToken);

        public Task<PlistDictionary?> FeedbackAsync(CancellationToken cancellationToken) => inner.FeedbackAsync(cancellationToken);

        public Task TeardownAsync(CancellationToken cancellationToken) => inner.TeardownAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private async Task<StreamSetupResult> SetupAsync(StreamSetupRequest request, CancellationToken cancellationToken)
        {
            var status = await Assert.ThrowsAsync<RtspStatusException>(() => inner.SetupStreamAsync(request, cancellationToken));
            Assert.Equal(501, status.Response.StatusCode);
            return new StreamSetupResult(StreamType.Buffered, reader.Port, 0, null, 8L << 20, new PlistDictionary());
        }
    }
}
