using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Airplay2.Sender.Alac;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Xunit;

namespace Airplay2.Sender.Tests.Streams;

/// <summary>One packet as the buffered reader saw it.</summary>
internal sealed record BufferedPacketSeen(int Connection, int Length, uint Sequence, bool Marker, uint Timestamp, uint Ssrc, byte[]? Frame, short[]? Pcm);

/// <summary>
/// The receiver side of the buffered stream's data connection (the fake
/// receiver's buffered reader): a loopback TCP listener that reads length-prefixed packets the way shairport-sync does
/// (<c>[sps] ap2_buffered_audio_processor.c:242-297</c>), decrypts them with the stream key (AAD bytes 4..11, nonce tail)
/// and decodes ALAC 44.1/16 frames. Reading can be paused to exercise TCP backpressure.
/// </summary>
internal sealed class BufferedTestReceiver : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<BufferedPacketSeen> _packets = Channel.CreateUnbounded<BufferedPacketSeen>();
    private readonly ConcurrentQueue<string> _closed = new();
    private readonly Task _accepting;
    private readonly SemaphoreSlim _resume = new(0);
    private volatile bool _paused;
    private int _connections;

    public BufferedTestReceiver(byte[]? key = null)
    {
        Key = key;
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public IPEndPoint Endpoint => new(IPAddress.Loopback, Port);

    /// <summary>The stream key; packets are only recorded raw while it is null.</summary>
    public byte[]? Key { get; set; }

    public int Connections => Volatile.Read(ref _connections);

    public IReadOnlyCollection<string> Closed => _closed;

    public ChannelReader<BufferedPacketSeen> Packets => _packets.Reader;

    /// <summary>Stops reading from the sockets (the sender's writes back up in TCP).</summary>
    public void Pause() => _paused = true;

    public void Resume()
    {
        _paused = false;
        _resume.Release();
    }

    /// <summary>Reads <paramref name="count"/> packets or fails after <paramref name="timeout"/>.</summary>
    public async Task<List<BufferedPacketSeen>> ReadAsync(int count, TimeSpan? timeout = null)
    {
        var list = new List<BufferedPacketSeen>(count);
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        try
        {
            while (list.Count < count)
            {
                list.Add(await _packets.Reader.ReadAsync(cts.Token));
            }
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"Only {list.Count} of {count} buffered packets arrived.");
        }

        return list;
    }

    /// <summary>Everything that arrives until <paramref name="quiet"/> passes without a packet.</summary>
    public async Task<List<BufferedPacketSeen>> DrainAsync(TimeSpan quiet)
    {
        var list = new List<BufferedPacketSeen>();
        while (true)
        {
            using var cts = new CancellationTokenSource(quiet);
            try
            {
                list.Add(await _packets.Reader.ReadAsync(cts.Token));
            }
            catch (OperationCanceledException)
            {
                return list;
            }
        }
    }

    public async Task WaitClosedAsync(TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (_closed.IsEmpty)
        {
            Assert.True(DateTime.UtcNow < until, "the data connection was not closed");
            await Task.Delay(10);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _accepting;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        var readers = new List<Task>();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var socket = await _listener.AcceptSocketAsync(_stop.Token);
                var id = Interlocked.Increment(ref _connections);
                readers.Add(ReadLoopAsync(socket, id));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        await Task.WhenAll(readers);
    }

    private async Task ReadLoopAsync(Socket socket, int id)
    {
        var decoder = new AlacDecoder(AlacMagicCookie.AirPlay);
        var buffer = new byte[256 * 1024];
        var used = 0;
        try
        {
            using (socket)
            {
                while (true)
                {
                    while (_paused && !_stop.IsCancellationRequested)
                    {
                        await _resume.WaitAsync(TimeSpan.FromMilliseconds(20));
                    }

                    var n = await socket.ReceiveAsync(buffer.AsMemory(used), SocketFlags.None, _stop.Token);
                    if (n == 0)
                    {
                        _closed.Enqueue($"connection {id}: EOF");
                        return;
                    }

                    used += n;
                    var consumed = 0;
                    while (BufferedAudioPacket.TrySplit(buffer.AsSpan(consumed, used - consumed), out var rtp, out var length))
                    {
                        _packets.Writer.TryWrite(Parse(id, length, rtp, decoder));
                        consumed += length;
                    }

                    buffer.AsSpan(consumed, used - consumed).CopyTo(buffer);
                    used -= consumed;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            _closed.Enqueue($"connection {id}: {ex.GetType().Name}");
        }
    }

    private BufferedPacketSeen Parse(int connection, int length, ReadOnlySpan<byte> rtp, AlacDecoder decoder)
    {
        var sequence = BufferedAudioPacket.ReadSequence(rtp);
        var marker = (rtp[1] & 0x80) != 0;
        var timestamp = RtpAudioPacketBuilder.ReadTimestamp(rtp);
        var ssrc = BufferedAudioPacket.ReadSsrc(rtp);
        byte[]? frame = null;
        short[]? pcm = null;
        if (Key is { } key)
        {
            var plain = new byte[rtp.Length];
            if (RtpAudioPacketBuilder.TryDecrypt(key, rtp, plain, out var n))
            {
                frame = plain[..n];
                if (ssrc == BufferedAudioPacket.SsrcAlac44100S16)
                {
                    var samples = new short[352 * 2];
                    var frames = decoder.Decode(frame, samples, 2);
                    pcm = frames > 0 ? samples[..(frames * 2)] : null;
                }
            }
        }

        return new BufferedPacketSeen(connection, length, sequence, marker, timestamp, ssrc, frame, pcm);
    }
}

/// <summary>A SETRATEANCHORTIME or FLUSHBUFFERED as a session saw it.</summary>
internal sealed record BufferedControlCall(string Method, int Rate, uint RtpTime, ulong Seconds, ulong Fraction, ulong TimelineId, uint UntilSeq, uint UntilTs, uint? FromSeq, uint? FromTs);

/// <summary>
/// A receiver session double for transport tests: records SETRATEANCHORTIME and FLUSHBUFFERED (optionally slow or failing)
/// and answers SETUPs like a PTP receiver whose buffered data port is a <see cref="BufferedTestReceiver"/>.
/// </summary>
internal sealed class RecordingSession(string id = "rx") : IReceiverSession
{
    private readonly Channel<BufferedControlCall> _calls = Channel.CreateUnbounded<BufferedControlCall>();

    public ConcurrentQueue<BufferedControlCall> Calls { get; } = new();

    public ConcurrentQueue<string> Log { get; } = new();

    public TimeSpan Delay { get; set; }

    public Exception? Failure { get; set; }

    public StreamSetupRequest? LastStreamSetup { get; private set; }

    public SessionSetupRequest? LastSessionSetup { get; private set; }

    public int DataPort { get; set; }

    public long? AudioBufferSize { get; set; } = 8L << 20;

    public DiscoveredReceiver Receiver { get; } = new(id, id, [IPAddress.Loopback], 7000, AirplayTxtRecord.Parse(["features=0x405FCA00,0x1C340", "flags=0x4"]), null, DateTimeOffset.UtcNow);

    public IRtspConnection Connection { get; } = new StubConnection();

    public PairingSession? Pairing => null;

    public async Task<BufferedControlCall> NextCallAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        try
        {
            return await _calls.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("no SETRATEANCHORTIME/FLUSHBUFFERED arrived");
            throw;
        }
    }

    public async Task<List<BufferedControlCall>> DrainCallsAsync(TimeSpan quiet)
    {
        var list = new List<BufferedControlCall>();
        while (true)
        {
            using var cts = new CancellationTokenSource(quiet);
            try
            {
                list.Add(await _calls.Reader.ReadAsync(cts.Token));
            }
            catch (OperationCanceledException)
            {
                return list;
            }
        }
    }

    public async Task SetRateAnchorTimeAsync(int rate, uint rtpTime, ulong networkTimeSecs, ulong networkTimeFrac, ulong timelineId, CancellationToken cancellationToken)
    {
        await RespondAsync(cancellationToken);
        Record(new BufferedControlCall("SETRATEANCHORTIME", rate, rtpTime, networkTimeSecs, networkTimeFrac, timelineId, 0, 0, null, null));
    }

    public async Task FlushBufferedAsync(uint flushUntilSeq, uint flushUntilTimestamp, uint? flushFromSeq, uint? flushFromTimestamp, CancellationToken cancellationToken)
    {
        await RespondAsync(cancellationToken);
        Record(new BufferedControlCall("FLUSHBUFFERED", 0, 0, 0, 0, 0, flushUntilSeq, flushUntilTimestamp, flushFromSeq, flushFromTimestamp));
    }

    public Task<SessionSetupResult> SetupSessionAsync(SessionSetupRequest request, CancellationToken cancellationToken)
    {
        LastSessionSetup = request;
        Log.Enqueue("SETUP session");
        return Task.FromResult(new SessionSetupResult(7001, 0, [IPAddress.Loopback], new PlistDictionary()));
    }

    public Task<StreamSetupResult> SetupStreamAsync(StreamSetupRequest request, CancellationToken cancellationToken)
    {
        LastStreamSetup = request;
        Log.Enqueue("SETUP stream");
        return Task.FromResult(new StreamSetupResult(request.Type, DataPort, 0, null, AudioBufferSize, new PlistDictionary()));
    }

    public Task TeardownAsync(CancellationToken cancellationToken)
    {
        Log.Enqueue("TEARDOWN");
        return Task.CompletedTask;
    }

    public Task FlushAsync(ushort nextSequence, uint nextTimestamp, CancellationToken cancellationToken)
    {
        Log.Enqueue("FLUSH");
        return Task.CompletedTask;
    }

    public Task SetPeersAsync(IReadOnlyList<IPAddress> peers, CancellationToken cancellationToken)
    {
        Log.Enqueue("SETPEERS");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Log.Enqueue("DISPOSE");
        return ValueTask.CompletedTask;
    }

    public Task<PlistDictionary> GetInfoAsync(bool withQualifier, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<PairingSession> PairAsync(IPairingStore store, Func<CancellationToken, Task<string>>? pinPrompt, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task AuthSetupAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IEventChannel> ConnectEventChannelAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<int> RecordAsync(CancellationToken cancellationToken) => Task.FromResult(0);

    public Task SetVolumeAsync(double volumeDb, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<double> GetVolumeAsync(CancellationToken cancellationToken) => Task.FromResult(-20.0);

    public Task SetMetadataAsync(TrackMetadata metadata, uint startFrame, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SetArtworkAsync(ReadOnlyMemory<byte> image, string contentType, uint startFrame, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SetProgressAsync(ProgressInfo progress, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<PlistDictionary?> FeedbackAsync(CancellationToken cancellationToken) => Task.FromResult<PlistDictionary?>(null);

    private async Task RespondAsync(CancellationToken cancellationToken)
    {
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        if (Failure is { } failure)
        {
            throw failure;
        }
    }

    private void Record(BufferedControlCall call)
    {
        Calls.Enqueue(call);
        _calls.Writer.TryWrite(call);
    }

    /// <summary>An <see cref="IRtspConnection"/> that only answers the address questions the wiring asks.</summary>
    private sealed class StubConnection : IRtspConnection
    {
        public IPEndPoint RemoteEndPoint { get; } = new(IPAddress.Loopback, 7000);

        public IPAddress LocalAddress => IPAddress.Loopback;

        public RtspClientIdentity Identity { get; } = RtspClientIdentity.CreateRandom();

        public uint SessionId => 1;

        public string SessionUri => "rtsp://127.0.0.1/1";

        public bool IsEncrypted => true;

        public Task<string> Closed { get; } = new TaskCompletionSource<string>().Task;

        public Task<RtspResponse> SendAsync(RtspRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RtspResponse> SendExpectOkAsync(RtspRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void EnableEncryption(Airplay2.Sender.Crypto.HapChannelKeys keys) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Assertions on anchors.</summary>
internal static class BufferedAssert
{
    /// <summary>The anchor's network time in PTP nanoseconds, as shairport-sync reconstructs it.</summary>
    public static ulong Nanos(BufferedControlCall call) => NetworkTime.ToNanoseconds(call.Seconds, call.Fraction);
}
