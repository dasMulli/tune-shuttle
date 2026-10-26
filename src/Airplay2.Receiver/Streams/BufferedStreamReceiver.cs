using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Airplay2.Receiver.Codecs;
using Airplay2.Receiver.Playout;
using Airplay2.Receiver.Session;
using Airplay2.Sender.Alac;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airplay2.Receiver.Streams;

/// <summary>Options of a <see cref="BufferedStreamReceiver"/> (SPEC-receiver.md 4.6, 5.2).</summary>
public sealed record BufferedStreamReceiverOptions
{
    /// <summary>Local address of the data listener and the idle control socket (loopback in tests; the product receiver binds its LAN address).</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;

    /// <summary>The <c>audioBufferSize</c> answered in the stream SETUP and the most undecoded bytes kept (SPEC-receiver.md 4.6, 5.2: 8 MiB).</summary>
    public int AudioBufferSize { get; init; } = 8388608;

    /// <summary>
    /// How much decoded audio the <see cref="PlayoutBuffer"/> may hold before the next block is decoded (SPEC-receiver.md 5.2:
    /// blocks wait undecoded, as <c>[sps] ap2_buffered_audio_processor.c:502-506</c> decodes only near the play time). While
    /// the buffer holds this much, blocks stay in the undecoded queue and, once that is full, the socket is not read.
    /// </summary>
    public TimeSpan DecodeAhead { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How often the decoder looks again while the playout buffer is full (SPEC-receiver.md 5.2; <c>[sps]</c> sleeps two packet durations).</summary>
    public TimeSpan DecodePollInterval { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>Label used in socket names and log messages.</summary>
    public string Name { get; init; } = "receiver";
}

/// <summary>Counters of one <see cref="BufferedStreamReceiver"/> for Diagnostics and tests (SPEC-receiver.md 5.2, 5.3 statistics).</summary>
/// <param name="Connections">Data connections accepted so far.</param>
/// <param name="BlocksReceived">Blocks read from the data connection.</param>
/// <param name="BytesReceived">Bytes read from the data connection, length prefixes included.</param>
/// <param name="BlocksDecoded">Blocks decoded and handed to the playout buffer (muted ones included).</param>
/// <param name="MutedBlocks">Blocks decoded and muted as the first after a start, flush or discontinuity.</param>
/// <param name="LateBlocks">Blocks dropped without decrypting because their release time was already more than <c>LateThreshold</c> past (R6).</param>
/// <param name="FlushedBlocks">Blocks dropped by a <c>FLUSH</c>, an immediate <c>FLUSHBUFFERED</c> or a splice (SPEC-receiver.md 4.7).</param>
/// <param name="BadBlocks">Blocks that failed authentication or decoding, or had no usable decoder.</param>
/// <param name="UnknownSsrcBlocks">Blocks whose SSRC is neither AAC nor ALAC; decoded per the stream SETUP's <c>audioFormat</c> (RQ3).</param>
/// <param name="QueuedBytes">Undecoded bytes waiting now (at most <see cref="BufferedStreamReceiverOptions.AudioBufferSize"/>).</param>
/// <param name="ReadPauses">How often reading the socket was paused because the undecoded queue was full (TCP flow control).</param>
/// <param name="Decoder">The decoder of the last decoded block (<c>ALAC</c> or <c>AAC-LC (libavcodec)</c>), or null before the first.</param>
public sealed record BufferedStreamStatistics(
    long Connections,
    long BlocksReceived,
    long BytesReceived,
    long BlocksDecoded,
    long MutedBlocks,
    long LateBlocks,
    long FlushedBlocks,
    long BadBlocks,
    long UnknownSsrcBlocks,
    long QueuedBytes,
    long ReadPauses,
    string? Decoder);

/// <summary>
/// What the buffered reader needs from the playout side (SPEC-receiver.md 5.2, 5.3): the R6 lateness question, whether
/// more decoded audio is wanted, and the queue itself. <see cref="PlayoutBuffer"/> is the product implementation; tests
/// substitute a recorder.
/// </summary>
internal interface IBufferedPlayoutTarget
{
    /// <summary>True when the frame at <paramref name="rtpTimestamp"/> is already past <c>release + LateThreshold</c> (R6).</summary>
    bool IsLate(uint rtpTimestamp);

    /// <summary>True while the buffer wants more decoded audio (it holds less than <see cref="BufferedStreamReceiverOptions.DecodeAhead"/>).</summary>
    bool WantsMore(TimeSpan decodeAhead);

    /// <summary>The decoder now in use, for the statistics (<c>ALAC</c> or <c>AAC-LC (libavcodec)</c>, SPEC-receiver.md 5.3).</summary>
    void ReportDecoder(string decoder);

    /// <summary>Queues decoded S16 stereo frames (copied).</summary>
    void Enqueue(uint rtpTimestamp, uint sequence, ReadOnlyMemory<short> frames);
}

/// <summary>
/// The receive path of a buffered (type 103) stream (SPEC-receiver.md 4.6, 5.2): a TCP data listener bound at the stream
/// SETUP (one connection at a time), 2-byte length-prefixed <see cref="BufferedBlock"/>s, decoder selection per block by
/// SSRC (AAC through <see cref="IAacDecoder"/> with a 7-byte ADTS header prepended, ALAC through <c>AlacDecoder</c>; an
/// unknown SSRC uses the stream SETUP's <c>audioFormat</c> and is logged once, RQ3), decrypted with <c>shk</c>, late blocks
/// (R6) dropped before decrypting, the first block after a start, flush or discontinuity decoded and muted, and the frames
/// handed to the <see cref="PlayoutBuffer"/>. The reader stops reading the socket when <c>AudioBufferSize</c> is full
/// (TCP flow control). The UDP control socket of the answer carries no traffic and is closed with the stream.
/// </summary>
/// <remarks>
/// Two loops per stream: the reader (accept, frame, queue; waits for room in the undecoded queue) and the decoder (waits
/// until the playout buffer wants more, then drops a late block or decrypts, decodes and enqueues it). Blocks wait
/// undecoded until they are needed, as <c>[sps] ap2_buffered_audio_processor.c:502-506, 683-694</c> does, so the 8 MiB
/// window is the run-ahead the sender may push. The decoders are used on the decoder loop only. The AAC decoder passed
/// in is owned by the receiver and disposed with it.
/// </remarks>
public sealed class BufferedStreamReceiver : IAsyncDisposable
{
    private const string AlacName = "ALAC";
    private const string AacName = "AAC-LC (libavcodec)";

    private readonly ILogger _logger;
    private readonly IBufferedPlayoutTarget _target;
    private readonly Lock _gate = new();
    private readonly Queue<QueuedBlock> _queue = new();
    private readonly HashSet<uint> _loggedSsrcs = [];
    private readonly AlacDecoder _alac = new(AlacMagicCookie.Default44100Stereo16);
    private readonly CancellationTokenSource _stop = new();
    private Socket? _listener;
    private Socket? _control;
    private Socket? _connection;
    private Task? _readLoop;
    private Task? _decodeLoop;
    private byte[]? _key;
    private ulong _audioFormat;
    private long _queuedBytes;
    private TaskCompletionSource _dataSignal = NewSignal();
    private TaskCompletionSource _spaceSignal = NewSignal();

    // Drop rules of FLUSHBUFFERED for blocks still to arrive (SPEC-receiver.md 4.7), guarded by _gate.
    private uint? _flushUntil;
    private (uint From, uint Until)? _splice;
    private bool _spliceActive;
    private bool _haveArrived;
    private uint _lastArrived;
    private bool _discontinuity = true;
    private bool _noAacLogged;
    private int _started;
    private int _disposed;

    // Decoder-loop state.
    private bool _haveLast;
    private uint _lastSequence;
    private uint _expectedTimestamp;
    private Format _lastFormat;

    private long _connections;
    private long _blocksReceived;
    private long _bytesReceived;
    private long _blocksDecoded;
    private long _mutedBlocks;
    private long _lateBlocks;
    private long _flushedBlocks;
    private long _badBlocks;
    private long _unknownSsrcBlocks;
    private long _readPauses;
    private string? _decoderName;

    /// <summary>Creates the receiver; nothing is bound until <see cref="StartAsync"/>.</summary>
    /// <param name="buffer">Where decoded frames go (SPEC-receiver.md 5.3).</param>
    /// <param name="aacDecoder">The AAC decoder of this stream, or null in the ALAC-only variant (R3, R4); owned and disposed by the receiver.</param>
    /// <param name="options">Options; the defaults when null.</param>
    /// <param name="logger">Logger.</param>
    public BufferedStreamReceiver(PlayoutBuffer buffer, IAacDecoder? aacDecoder, BufferedStreamReceiverOptions? options = null, ILogger? logger = null)
        : this(new PlayoutBufferTarget(buffer ?? throw new ArgumentNullException(nameof(buffer))), aacDecoder, options, logger)
    {
        Buffer = buffer;
    }

    /// <summary>Creates the receiver over another playout target (tests).</summary>
    internal BufferedStreamReceiver(IBufferedPlayoutTarget target, IAacDecoder? aacDecoder, BufferedStreamReceiverOptions? options, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
        AacDecoder = aacDecoder;
        Options = options ?? new BufferedStreamReceiverOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(Options.AudioBufferSize, ushort.MaxValue, nameof(options));
        _logger = logger ?? NullLogger.Instance;
        Buffer = null!;
    }

    /// <summary>The playout buffer (null only for the test constructor).</summary>
    public PlayoutBuffer Buffer { get; }

    /// <summary>The AAC decoder, or null.</summary>
    public IAacDecoder? AacDecoder { get; }

    /// <summary>The options.</summary>
    public BufferedStreamReceiverOptions Options { get; }

    /// <summary>The bound TCP data port after <see cref="StartAsync"/> (the <c>dataPort</c> of the answer, SPEC-receiver.md 4.6).</summary>
    public int DataPort => (_listener?.LocalEndPoint as IPEndPoint)?.Port ?? 0;

    /// <summary>The bound UDP control port after <see cref="StartAsync"/> (the <c>controlPort</c> of the answer; no traffic, SPEC-receiver.md 4.6).</summary>
    public int ControlPort => (_control?.LocalEndPoint as IPEndPoint)?.Port ?? 0;

    /// <summary>A snapshot of the counters (SPEC-receiver.md 5.2, 5.3 statistics).</summary>
    public BufferedStreamStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                return new BufferedStreamStatistics(
                    Interlocked.Read(ref _connections),
                    Interlocked.Read(ref _blocksReceived),
                    Interlocked.Read(ref _bytesReceived),
                    Interlocked.Read(ref _blocksDecoded),
                    Interlocked.Read(ref _mutedBlocks),
                    Interlocked.Read(ref _lateBlocks),
                    Interlocked.Read(ref _flushedBlocks),
                    Interlocked.Read(ref _badBlocks),
                    Interlocked.Read(ref _unknownSsrcBlocks),
                    _queuedBytes,
                    Interlocked.Read(ref _readPauses),
                    Volatile.Read(ref _decoderName));
            }
        }
    }

    /// <summary>
    /// Binds the data listener and the idle control socket for one stream (SPEC-receiver.md 4.6) and accepts the sender's
    /// data connection in the background.
    /// </summary>
    /// <param name="sharedKey">The stream's 32-byte <c>shk</c>.</param>
    /// <param name="audioFormat">The stream SETUP's <c>audioFormat</c>: the decoder for blocks with an unknown SSRC (RQ3).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="ArgumentException"><paramref name="sharedKey"/> is not 32 bytes.</exception>
    /// <exception cref="InvalidOperationException">The receiver was started before.</exception>
    public Task StartAsync(byte[] sharedKey, ulong audioFormat, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sharedKey);
        if (sharedKey.Length != 32)
        {
            throw new ArgumentException("The stream key (shk) is 32 bytes.", nameof(sharedKey));
        }

        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The buffered stream receiver is already started.");
        }

        _key = (byte[])sharedKey.Clone();
        _audioFormat = audioFormat;
        var family = Options.BindAddress.AddressFamily;
        _listener = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
        _control = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            _listener.Bind(new IPEndPoint(Options.BindAddress, 0));
            _listener.Listen(1);
            _control.Bind(new IPEndPoint(Options.BindAddress, 0));
        }
        catch
        {
            _listener.Dispose();
            _control.Dispose();
            throw;
        }

        _logger.LogDebug("Buffered stream receiver {Name}: data port {DataPort} (TCP), control port {ControlPort} (UDP, idle), audioFormat 0x{AudioFormat:X}", Options.Name, DataPort, ControlPort, audioFormat);
        _readLoop = RunReadLoopAsync(_stop.Token);
        _decodeLoop = RunDecodeLoopAsync(_stop.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// <c>FLUSH</c> (<paramref name="untilSequence"/> null: every undecoded block is dropped) or an immediate
    /// <c>FLUSHBUFFERED</c> (blocks with a sequence before <paramref name="untilSequence"/>, 23-bit modular compare, are
    /// dropped now and when they still arrive) on the undecoded stage (SPEC-receiver.md 4.5, 4.7); the next decoded block
    /// is muted and the AAC decoder reset (5.2). The <see cref="PlayoutBuffer"/> applies the same flush to decoded frames.
    /// </summary>
    public void Flush(uint? untilSequence)
    {
        lock (_gate)
        {
            if (untilSequence is { } until)
            {
                until &= BufferedBlock.SequenceMask;
                _flushUntil = until;
                Purge(b => BufferedBlock.IsBefore(b.Block.Sequence, until));
            }
            else
            {
                _flushUntil = null;
                _splice = null;
                Purge(_ => true);
            }

            _discontinuity = true;
        }

        _logger.LogDebug("Buffered stream receiver {Name}: flush until {Until}", Options.Name, untilSequence?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "all");
    }

    /// <summary>
    /// A deferred <c>FLUSHBUFFERED</c> (SPEC-receiver.md 4.7) on the undecoded stage: once the block <paramref name="fromSequence"/>
    /// has arrived, blocks from it up to but excluding <paramref name="untilSequence"/> are dropped (queued ones and late
    /// arrivals) without flushing or pausing anything; the block after the gap is muted (a discontinuity, 5.2). The
    /// <see cref="PlayoutBuffer"/> applies the same splice to decoded frames.
    /// </summary>
    public void Splice(uint fromSequence, uint untilSequence)
    {
        fromSequence &= BufferedBlock.SequenceMask;
        untilSequence &= BufferedBlock.SequenceMask;
        lock (_gate)
        {
            _splice = (fromSequence, untilSequence);
            _spliceActive = _haveArrived && !BufferedBlock.IsBefore(_lastArrived, fromSequence);
            if (_spliceActive)
            {
                Purge(b => InSplice(b.Block.Sequence, fromSequence, untilSequence));
            }
        }

        _logger.LogDebug("Buffered stream receiver {Name}: splice from {From} until {Until}", Options.Name, fromSequence, untilSequence);
    }

    /// <summary>Closes the sockets and stops both loops (TEARDOWN of the stream, SPEC-receiver.md 4.9); disposes the AAC decoder.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        _listener?.Dispose();
        _control?.Dispose();
        Volatile.Read(ref _connection)?.Dispose();
        foreach (var loop in new[] { _readLoop, _decodeLoop })
        {
            if (loop is not null)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        lock (_gate)
        {
            _queue.Clear();
            _queuedBytes = 0;
        }

        AacDecoder?.Dispose();
        _stop.Dispose();
        _logger.LogDebug("Buffered stream receiver {Name} disposed", Options.Name);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static bool InSplice(uint sequence, uint from, uint until) =>
        !BufferedBlock.IsBefore(sequence, from) && BufferedBlock.IsBefore(sequence, until);

    /// <summary>Accepts one data connection at a time and reads its blocks into the undecoded queue.</summary>
    private async Task RunReadLoopAsync(CancellationToken stop)
    {
        await Task.Yield();
        while (!stop.IsCancellationRequested)
        {
            Socket connection;
            try
            {
                connection = await _listener!.AcceptAsync(stop).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException || stop.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException e)
            {
                // A transient accept failure (a reset in the backlog) must not leave the stream deaf for the session.
                _logger.LogWarning(e, "Buffered stream receiver {Name}: accepting the data connection failed ({Error}); still listening", Options.Name, e.SocketErrorCode);
                continue;
            }

            Interlocked.Increment(ref _connections);
            Volatile.Write(ref _connection, connection);
            _logger.LogInformation("Buffered stream receiver {Name}: data connection from {Remote}", Options.Name, connection.RemoteEndPoint);
            lock (_gate)
            {
                _discontinuity = true;
            }

            try
            {
                await ReadConnectionAsync(connection, stop).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException && stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or EndOfStreamException)
            {
                _logger.LogInformation("Buffered stream receiver {Name}: data connection closed ({Reason})", Options.Name, e.Message);
            }
            finally
            {
                Volatile.Write(ref _connection, null);
                connection.Dispose();
            }
        }
    }

    private async Task ReadConnectionAsync(Socket connection, CancellationToken stop)
    {
        await using var stream = new NetworkStream(connection, ownsSocket: false);
        var prefix = new byte[BufferedBlock.LengthPrefixSize];
        while (true)
        {
            var read = await stream.ReadAtLeastAsync(prefix, prefix.Length, throwOnEndOfStream: false, stop).ConfigureAwait(false);
            if (read < prefix.Length)
            {
                _logger.LogInformation("Buffered stream receiver {Name}: the sender closed the data connection", Options.Name);
                return;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(prefix);
            if (length < BufferedBlock.LengthPrefixSize + BufferedBlock.HeaderSize)
            {
                // The framing is lost; nothing after this length can be trusted (SPEC-audio.md 4.2).
                _logger.LogWarning("Buffered stream receiver {Name}: block length {Length} is shorter than a header; closing the data connection", Options.Name, length);
                return;
            }

            var body = new byte[length - BufferedBlock.LengthPrefixSize];
            await stream.ReadExactlyAsync(body, stop).ConfigureAwait(false);
            Interlocked.Increment(ref _blocksReceived);
            Interlocked.Add(ref _bytesReceived, length);
            BufferedBlock.TryRead(body, out var block);
            await QueueAsync(new QueuedBlock(block, length), stop).ConfigureAwait(false);
        }
    }

    /// <summary>Applies the arrival drop rules, then waits for room in the undecoded queue (TCP flow control, SPEC-receiver.md 5.2).</summary>
    private async ValueTask QueueAsync(QueuedBlock queued, CancellationToken stop)
    {
        var paused = false;
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (!paused)
                {
                    _haveArrived = true;
                    _lastArrived = queued.Block.Sequence;
                }

                if (DropOnArrival(queued.Block.Sequence))
                {
                    Interlocked.Increment(ref _flushedBlocks);
                    _discontinuity = true;
                    return;
                }

                if (_queue.Count == 0 || _queuedBytes + queued.Bytes <= Options.AudioBufferSize)
                {
                    _queue.Enqueue(queued);
                    _queuedBytes += queued.Bytes;
                    _dataSignal.TrySetResult();
                    return;
                }

                if (!paused)
                {
                    paused = true;
                    Interlocked.Increment(ref _readPauses);
                    _logger.LogDebug("Buffered stream receiver {Name}: {Bytes} undecoded bytes queued; reading paused", Options.Name, _queuedBytes);
                }

                if (_spaceSignal.Task.IsCompleted)
                {
                    _spaceSignal = NewSignal();
                }

                wait = _spaceSignal.Task;
            }

            await wait.WaitAsync(stop).ConfigureAwait(false);
        }
    }

    /// <summary>The FLUSHBUFFERED rules for an arriving block (SPEC-receiver.md 4.7); called under <see cref="_gate"/>.</summary>
    private bool DropOnArrival(uint sequence)
    {
        if (_flushUntil is { } until)
        {
            if (BufferedBlock.IsBefore(sequence, until))
            {
                return true;
            }

            _flushUntil = null;
        }

        if (_splice is { } splice)
        {
            if (!_spliceActive && !BufferedBlock.IsBefore(sequence, splice.From))
            {
                _spliceActive = true;
            }

            if (_spliceActive)
            {
                if (InSplice(sequence, splice.From, splice.Until))
                {
                    return true;
                }

                _splice = null;
                _spliceActive = false;
            }
        }

        return false;
    }

    /// <summary>Removes queued blocks that match; called under <see cref="_gate"/>.</summary>
    private void Purge(Func<QueuedBlock, bool> drop)
    {
        var count = _queue.Count;
        var removed = 0;
        for (var i = 0; i < count; i++)
        {
            var item = _queue.Dequeue();
            if (drop(item))
            {
                _queuedBytes -= item.Bytes;
                removed++;
            }
            else
            {
                _queue.Enqueue(item);
            }
        }

        if (removed > 0)
        {
            Interlocked.Add(ref _flushedBlocks, removed);
            _spaceSignal.TrySetResult();
        }
    }

    /// <summary>Takes the next block when the playout buffer wants more audio, and decodes it.</summary>
    private async Task RunDecodeLoopAsync(CancellationToken stop)
    {
        await Task.Yield();
        var plaintext = new byte[ushort.MaxValue];
        var adts = new byte[ushort.MaxValue + AdtsHeader.Size];
        var pcm = new short[2 * 4096];
        while (!stop.IsCancellationRequested)
        {
            Task? waitData = null;
            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    if (_dataSignal.Task.IsCompleted)
                    {
                        _dataSignal = NewSignal();
                    }

                    waitData = _dataSignal.Task;
                }
            }

            if (waitData is not null)
            {
                await waitData.WaitAsync(stop).ConfigureAwait(false);
                continue;
            }

            if (!_target.WantsMore(Options.DecodeAhead))
            {
                await Task.Delay(Options.DecodePollInterval, stop).ConfigureAwait(false);
                continue;
            }

            QueuedBlock next;
            bool discontinuity;
            lock (_gate)
            {
                if (!_queue.TryDequeue(out next))
                {
                    continue;
                }

                _queuedBytes -= next.Bytes;
                _spaceSignal.TrySetResult();
                discontinuity = _discontinuity;
                _discontinuity = false;
            }

            try
            {
                Process(next.Block, discontinuity, plaintext, adts, pcm);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Interlocked.Increment(ref _badBlocks);
                _haveLast = false;
                _logger.LogWarning(e, "Buffered stream receiver {Name}: block {Sequence} could not be processed", Options.Name, next.Block.Sequence);
            }
        }
    }

    /// <summary>Format selection, late drop, decrypt, decode, first-block mute and enqueue of one block (SPEC-receiver.md 5.2).</summary>
    private void Process(BufferedBlock block, bool discontinuity, byte[] plaintext, byte[] adts, short[] pcm)
    {
        var format = SelectFormat(block.Ssrc);
        if (format == Format.None)
        {
            Interlocked.Increment(ref _badBlocks);
            _haveLast = false;
            return;
        }

        if (format == Format.Aac && AacDecoder is null)
        {
            if (!_noAacLogged)
            {
                _noAacLogged = true;
                _logger.LogWarning("Buffered stream receiver {Name}: AAC blocks arrive but no AAC decoder is loaded (R4); they are dropped", Options.Name);
            }

            Interlocked.Increment(ref _badBlocks);
            _haveLast = false;
            return;
        }

        if (_target.IsLate(block.Timestamp))
        {
            Interlocked.Increment(ref _lateBlocks);
            _haveLast = false;
            return;
        }

        if (!block.TryDecrypt(_key, plaintext, out var length))
        {
            Interlocked.Increment(ref _badBlocks);
            _haveLast = false;
            _logger.LogWarning("Buffered stream receiver {Name}: block {Sequence} failed authentication", Options.Name, block.Sequence);
            return;
        }

        var mute = discontinuity
            || !_haveLast
            || format != _lastFormat
            || block.Sequence != ((_lastSequence + 1) & BufferedBlock.SequenceMask)
            || block.Timestamp != _expectedTimestamp;
        int frames;
        if (format == Format.Aac)
        {
            if (mute)
            {
                AacDecoder!.Reset();
            }

            if (length > AdtsHeader.MaxPayloadLength)
            {
                Interlocked.Increment(ref _badBlocks);
                _haveLast = false;
                return;
            }

            AdtsHeader.Write(adts, length);
            plaintext.AsSpan(0, length).CopyTo(adts.AsSpan(AdtsHeader.Size));
            if (!AacDecoder!.TryDecode(adts.AsSpan(0, AdtsHeader.Size + length), pcm, out frames))
            {
                Interlocked.Increment(ref _badBlocks);
                _haveLast = false;
                _logger.LogDebug("Buffered stream receiver {Name}: AAC block {Sequence} could not be decoded", Options.Name, block.Sequence);
                return;
            }
        }
        else
        {
            frames = _alac.Decode(plaintext.AsSpan(0, length), pcm, 2);
            if (frames < 0)
            {
                Interlocked.Increment(ref _badBlocks);
                _haveLast = false;
                _logger.LogDebug("Buffered stream receiver {Name}: block {Sequence} is not a valid ALAC frame", Options.Name, block.Sequence);
                return;
            }
        }

        _haveLast = true;
        _lastFormat = format;
        _lastSequence = block.Sequence;
        _expectedTimestamp = unchecked(block.Timestamp + (uint)(format == Format.Aac ? BufferedBlock.AacFramesPerBlock : BufferedBlock.AlacFramesPerBlock));
        var name = format == Format.Aac ? AacName : AlacName;
        if (!string.Equals(Volatile.Read(ref _decoderName), name, StringComparison.Ordinal))
        {
            Volatile.Write(ref _decoderName, name);
            _target.ReportDecoder(name);
        }
        var samples = pcm.AsMemory(0, frames * 2);
        if (mute)
        {
            samples.Span.Clear();
            Interlocked.Increment(ref _mutedBlocks);
        }

        Interlocked.Increment(ref _blocksDecoded);
        if (frames > 0)
        {
            _target.Enqueue(block.Timestamp, block.Sequence, samples);
        }
    }

    /// <summary>SSRC <c>0x16000000</c> → AAC, <c>0x0000FACE</c> → ALAC, anything else → the SETUP's <c>audioFormat</c>, logged once per SSRC (RQ3).</summary>
    private Format SelectFormat(uint ssrc)
    {
        switch (ssrc)
        {
            case BufferedBlock.SsrcAac:
                return Format.Aac;
            case BufferedBlock.SsrcAlac:
                return Format.Alac;
        }

        Interlocked.Increment(ref _unknownSsrcBlocks);
        var format = _audioFormat switch
        {
            ReceiverStreamFormats.Alac => Format.Alac,
            ReceiverStreamFormats.AacLc => Format.Aac,
            _ => Format.None,
        };
        if (_loggedSsrcs.Add(ssrc))
        {
            _logger.LogInformation(
                "Buffered stream receiver {Name}: unknown SSRC 0x{Ssrc:X8}; decoding per the stream SETUP audioFormat 0x{AudioFormat:X} ({Decoder})",
                Options.Name,
                ssrc,
                _audioFormat,
                format == Format.None ? "none" : format == Format.Aac ? AacName : AlacName);
        }

        return format;
    }

    private enum Format
    {
        None,
        Alac,
        Aac,
    }

    private readonly record struct QueuedBlock(BufferedBlock Block, int Bytes);

    /// <summary>The product target: the stream's <see cref="PlayoutBuffer"/> (SPEC-receiver.md 5.3).</summary>
    private sealed class PlayoutBufferTarget(PlayoutBuffer buffer) : IBufferedPlayoutTarget
    {
        public bool IsLate(uint rtpTimestamp) => buffer.IsLate(rtpTimestamp);

        public bool WantsMore(TimeSpan decodeAhead) => buffer.BufferedFrames * TimeSpan.TicksPerSecond < decodeAhead.Ticks * PlayoutOptions.SampleRate;

        public void ReportDecoder(string decoder) => buffer.Decoder = decoder;

        public void Enqueue(uint rtpTimestamp, uint sequence, ReadOnlyMemory<short> frames) => buffer.Enqueue(rtpTimestamp, sequence, frames);
    }
}
