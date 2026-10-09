using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Airplay2.Sender.Crypto;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtsp;
using Microsoft.Extensions.Logging;

namespace Airplay2.Sender.Session;

/// <summary>Options of the event channel (SPEC-control.md 4.3).</summary>
public sealed record EventChannelOptions
{
    /// <summary>
    /// Connect attempts on a refused connection: airplay2-receiver may not listen yet when it answers the SETUP, so pyatv
    /// retries 5 times, 1 s apart [pyatv:pyatv/protocols/raop/protocols/airplayv2.py:80-103].
    /// </summary>
    public int ConnectAttempts { get; init; } = 5;

    /// <summary>Delay between connect attempts.</summary>
    public TimeSpan ConnectRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Timeout of one connect attempt.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Delay before each answer. Zero (default) answers immediately, as the receiver closes the channel after ~30 s
    /// without an answer [pyatv:docs/documentation/protocols.md:1786-1795] and shairport-sync reads the answer
    /// synchronously [sps:ap2_event_message_handler.c:45-67]. Probe P13 sets 5/20/40 s to see when the SpeakerA gives up.
    /// </summary>
    public TimeSpan AnswerDelay { get; init; } = TimeSpan.Zero;

    /// <summary>Capacity of <see cref="IEventChannel.Messages"/>; the oldest message is dropped when the reader falls behind.</summary>
    public int MessageCapacity { get; init; } = 64;
}

/// <summary>
/// The sender's end of the event channel (SPEC-control.md 4.3): a TCP connection from us to the receiver's
/// <c>eventPort</c>, HAP-framed with the Events keys in the swapped roles (we encrypt with
/// <c>Events-Read-Encryption-Key</c>, decrypt with <c>Events-Write-Encryption-Key</c>,
/// <see cref="HapChannelKeys.ForEvents"/>), carrying RTSP-shaped requests from the receiver. Every request is answered
/// with <c>&lt;protocol&gt; 200 OK</c>, <c>Content-Length: 0</c>, <c>Audio-Latency: 0</c>, <c>Server</c> and <c>CSeq</c>
/// echoed when present (pyatv's answer [pyatv:pyatv/protocols/airplay/channels.py:60-97]) before it is parsed and
/// published on <see cref="Messages"/>. Frames of up to 65 535 bytes of plaintext are accepted (SPEC-control.md 2.5, D9);
/// a message may span frames and frames need not align with messages.
/// </summary>
/// <remarks>
/// One owner per socket: the read loop reads, answers and parses; <see cref="DisposeAsync"/> closes the socket and waits
/// for the loop. A decryption failure or a malformed message closes the channel. <see cref="Closed"/> completes with the
/// reason in every case. When the close was not initiated by <see cref="DisposeAsync"/> (the receiver closed the
/// connection, a read or decryption failure, a protocol error), a final <see cref="ReceiverEventMessage.Closed"/> is
/// published before <see cref="Messages"/> completes; a sender-initiated close (TEARDOWN, a replaced channel, dispose)
/// only completes <see cref="Messages"/>, so a consumer never reads its own teardown as a receiver-side loss.
/// </remarks>
public sealed class EventChannel : IEventChannel
{
    private const int ReadChunkSize = 16 * 1024;

    /// <summary>The <see cref="Closed"/> reason of a close initiated by <see cref="DisposeAsync"/>.</summary>
    public const string ClosedBySenderReason = "closed by the sender";

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly EventChannelOptions _options;
    private readonly ILogger _logger;
    private readonly RtspStreamReader _reader = new();
    private readonly HapFrameCipher? _sendCipher;
    private readonly Channel<ReceiverEventMessage> _messages;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource<string> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly long _connectedAt = Stopwatch.GetTimestamp();
    private Task _readLoop = Task.CompletedTask;
    private string? _closeReason;
    private long _requestsAnswered;
    private long _framesReceived;
    private int _largestFrame;
    private long _firstRequestTicks = -1;
    private int _disposed;

    private EventChannel(Socket socket, IPEndPoint remote, HapChannelKeys? keys, EventChannelOptions options, ILogger logger)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: true);
        _options = options;
        _logger = logger;
        RemoteEndPoint = remote;
        if (keys is not null)
        {
            // Separate cipher instances per direction (the counters are independent, SPEC-control.md 2.5).
            _reader.EnableDecryption(new HapFrameCipher(keys));
            _sendCipher = new HapFrameCipher(keys);
        }

        _messages = Channel.CreateBounded<ReceiverEventMessage>(new BoundedChannelOptions(Math.Max(1, options.MessageCapacity))
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = true,
        });
    }

    /// <inheritdoc />
    public ChannelReader<ReceiverEventMessage> Messages => _messages.Reader;

    /// <summary>The receiver's event endpoint.</summary>
    public IPEndPoint RemoteEndPoint { get; }

    /// <summary>True when the channel is HAP-encrypted (always, once paired).</summary>
    public bool IsEncrypted => _sendCipher is not null;

    /// <summary>Completes when the channel closes; the result is the reason.</summary>
    public Task<string> Closed => _closed.Task;

    /// <summary>Requests answered so far.</summary>
    public long RequestsAnswered => Interlocked.Read(ref _requestsAnswered);

    /// <summary>HAP frames received (probe P21 logs frame sizes).</summary>
    public long FramesReceived => Interlocked.Read(ref _framesReceived);

    /// <summary>Plaintext length of the largest HAP frame received (above 1024 means the receiver sends large frames, SPEC-control.md 7.15).</summary>
    public int LargestFrameReceived => Volatile.Read(ref _largestFrame);

    /// <summary>Time from connecting to the first request from the receiver, or null before one arrived (probe P13).</summary>
    public TimeSpan? FirstRequestAfter
    {
        get
        {
            var ticks = Interlocked.Read(ref _firstRequestTicks);
            return ticks < 0 ? null : Stopwatch.GetElapsedTime(_connectedAt, ticks);
        }
    }

    /// <summary>
    /// Connects to the receiver's event port, retrying a refused connection (<see cref="EventChannelOptions.ConnectAttempts"/>
    /// attempts, <see cref="EventChannelOptions.ConnectRetryDelay"/> apart), and starts the read loop.
    /// </summary>
    /// <param name="remote">The receiver's address and <c>eventPort</c>.</param>
    /// <param name="keys">The Events keys in the sender's view (<see cref="HapChannelKeys.ForEvents"/>); null for a plaintext channel (an unpaired test connection).</param>
    /// <param name="options">Options.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    /// <exception cref="TimeoutException">An attempt timed out.</exception>
    /// <exception cref="SocketException">The last attempt failed.</exception>
    public static async Task<EventChannel> ConnectAsync(IPEndPoint remote, HapChannelKeys? keys, EventChannelOptions options, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ConnectAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.ConnectTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(options.AnswerDelay.Ticks);

        for (var attempt = 1; ; attempt++)
        {
            var socket = new Socket(remote.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.ConnectTimeout);
            try
            {
                await socket.ConnectAsync(remote, timeout.Token).ConfigureAwait(false);
                var channel = new EventChannel(socket, remote, keys, options, logger);
                logger.LogDebug("Event channel connected to {Receiver} after {Attempts} attempt(s){Encrypted}", remote, attempt, keys is null ? " (plaintext)" : string.Empty);
                channel._readLoop = channel.ReadLoopAsync();
                return channel;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                throw new TimeoutException($"Connecting the event channel to {remote} timed out after {options.ConnectTimeout.TotalSeconds:0.#} s.");
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused && attempt < options.ConnectAttempts)
            {
                socket.Dispose();
                logger.LogDebug("Event channel connect to {Receiver} refused (attempt {Attempt} of {Attempts}), retrying", remote, attempt, options.ConnectAttempts);
                await Task.Delay(options.ConnectRetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Close(ClosedBySenderReason);
        await _readLoop.ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        await Task.Yield();
        var chunk = new byte[ReadChunkSize];
        var reason = "receiver closed the event channel";
        try
        {
            while (true)
            {
                var read = await _stream.ReadAsync(chunk, _shutdown.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var before = _reader.FramesDecrypted;
                _reader.Append(chunk.AsSpan(0, read));
                if (_reader.FramesDecrypted != before)
                {
                    Interlocked.Exchange(ref _framesReceived, _reader.FramesDecrypted);
                    Volatile.Write(ref _largestFrame, _reader.LargestFramePlaintext);
                    if (_reader.LastFramePlaintext > HapFrameCipher.MaxSendFramePlaintext)
                    {
                        _logger.LogDebug("Event channel {Receiver}: received a {Bytes}-byte frame (above the 1024-byte send cap)", RemoteEndPoint, _reader.LastFramePlaintext);
                    }
                }

                await DispatchAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when ((ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException) && Volatile.Read(ref _closeReason) is not null)
        {
            reason = Volatile.Read(ref _closeReason)!;
        }
        catch (CryptographicException ex)
        {
            reason = $"decryption failed: {ex.Message}";
            _logger.LogWarning("Event channel {Receiver}: {Reason}", RemoteEndPoint, reason);
        }
        catch (InvalidDataException ex)
        {
            reason = $"protocol error: {ex.Message}";
            _logger.LogWarning("Event channel {Receiver}: {Reason}", RemoteEndPoint, reason);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            reason = $"read failed: {ex.Message}";
        }
        catch (Exception ex)
        {
            reason = $"read loop failed: {ex.Message}";
            _logger.LogError(ex, "Event channel {Receiver}: read loop failed", RemoteEndPoint);
        }

        Close(reason);
        reason = Volatile.Read(ref _closeReason) ?? reason;
        if (!string.Equals(reason, ClosedBySenderReason, StringComparison.Ordinal))
        {
            // Only a close the sender did not initiate is the receiver's view of the session ending (SPEC-control.md 6.5).
            _messages.Writer.TryWrite(new ReceiverEventMessage.Closed(reason));
        }

        _messages.Writer.TryComplete();
        _closed.TrySetResult(reason);
    }

    private async Task DispatchAsync()
    {
        while (true)
        {
            _reader.SkipEmptyLines();
            switch (_reader.PeekKind())
            {
                case RtspMessageKind.None:
                    return;
                case RtspMessageKind.Response:
                    if (!_reader.TryReadResponse(out var response))
                    {
                        return;
                    }

                    _logger.LogWarning("Event channel {Receiver}: ignoring unexpected response {Status}", RemoteEndPoint, response!.StatusCode);
                    continue;
                default:
                    if (!_reader.TryReadRequest(out var request))
                    {
                        return;
                    }

                    Interlocked.CompareExchange(ref _firstRequestTicks, Stopwatch.GetTimestamp(), -1);
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug(
                            "Event <- {Receiver} (frames {Frames}, largest {Largest} B)\n{Message}",
                            RemoteEndPoint,
                            _reader.FramesDecrypted,
                            _reader.LargestFramePlaintext,
                            RtspWireLog.Describe(request!, _logger.IsEnabled(LogLevel.Trace)));
                    }

                    await AnswerAsync(request!).ConfigureAwait(false);
                    Publish(Parse(request!));
                    continue;
            }
        }
    }

    private async Task AnswerAsync(RtspIncomingRequest request)
    {
        if (_options.AnswerDelay > TimeSpan.Zero)
        {
            _logger.LogInformation("Event channel {Receiver}: delaying the answer to {Method} {Uri} by {Delay} s (probe)", RemoteEndPoint, request.Method, request.Uri, _options.AnswerDelay.TotalSeconds);
            await Task.Delay(_options.AnswerDelay, _shutdown.Token).ConfigureAwait(false);
        }

        // pyatv's answer [pyatv:pyatv/protocols/airplay/channels.py:60-97]: echo the protocol, Content-Length 0,
        // Audio-Latency 0, Server and CSeq echoed when present.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [RtspHeaders.AudioLatency] = "0" };
        if (request.Header(RtspHeaders.Server) is { } server)
        {
            headers[RtspHeaders.Server] = server;
        }

        if (request.Header(RtspHeaders.CSeq) is { } cseq)
        {
            headers[RtspHeaders.CSeq] = cseq;
        }

        var protocol = request.Protocol.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase) || request.Protocol.StartsWith("RTSP/", StringComparison.OrdinalIgnoreCase)
            ? request.Protocol
            : RtspRequest.Protocol;
        var wire = RtspMessageCodec.EncodeResponse(protocol, RtspStatus.Ok, "OK", headers, ReadOnlyMemory<byte>.Empty);
        var payload = _sendCipher is { } cipher ? cipher.Encrypt(wire) : wire;
        await _stream.WriteAsync(payload, _shutdown.Token).ConfigureAwait(false);
        Interlocked.Increment(ref _requestsAnswered);
    }

    private ReceiverEventMessage Parse(RtspIncomingRequest request)
    {
        PlistDictionary? body = null;
        if (!request.Body.IsEmpty)
        {
            try
            {
                body = request.PlistBody();
            }
            catch (FormatException ex)
            {
                _logger.LogInformation("Event channel {Receiver}: {Method} {Uri} body is not a bplist ({Error})", RemoteEndPoint, request.Method, request.Uri, ex.Message);
            }
        }

        var type = body?.GetString("type");
        switch (type)
        {
            case "updateInfo" when body!.GetDictionary("value") is { } value:
                _logger.LogDebug(
                    "Event channel {Receiver}: updateInfo{Volume}",
                    RemoteEndPoint,
                    value.GetDouble("initialVolume") is { } v ? string.Create(CultureInfo.InvariantCulture, $" (initialVolume {v})") : string.Empty);
                return new ReceiverEventMessage.UpdateInfo(value);
            case "sendMediaRemoteCommand" when body!.GetString("value") is { } command:
                _logger.LogInformation("Event channel {Receiver}: media remote command {Command}", RemoteEndPoint, command);
                return new ReceiverEventMessage.MediaRemoteCommand(command);
            default:
                // Open question C-7.4 / P4: log anything else verbatim so a device-side volume message can be identified.
                _logger.LogInformation(
                    "Event channel {Receiver}: unknown message {Method} {Uri} type {Type}: {Body}",
                    RemoteEndPoint,
                    request.Method,
                    request.Uri,
                    type ?? "(none)",
                    RtspWireLog.DescribeBody(request.ContentType, request.Body.Span, full: false));
                return new ReceiverEventMessage.Unknown(type, body, request);
        }
    }

    private void Publish(ReceiverEventMessage message)
    {
        if (!_messages.Writer.TryWrite(message))
        {
            _logger.LogDebug("Event channel {Receiver}: message dropped, the channel is completed", RemoteEndPoint);
        }
    }

    private void Close(string reason)
    {
        if (Interlocked.CompareExchange(ref _closeReason, reason, null) is not null)
        {
            return;
        }

        _logger.Log(string.Equals(reason, ClosedBySenderReason, StringComparison.Ordinal) ? LogLevel.Debug : LogLevel.Information, "Event channel to {Receiver} closed: {Reason}", RemoteEndPoint, reason);
        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already disposed
        }

        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // the peer may already be gone
        }

        _stream.Dispose();
    }
}
