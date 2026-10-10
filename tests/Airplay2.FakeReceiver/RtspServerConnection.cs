using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Airplay2.Sender.Crypto;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtsp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airplay2.FakeReceiver;

/// <summary>Handles one request received by an <see cref="RtspServerConnection"/>.</summary>
/// <param name="connection">The connection the request arrived on (to record state or switch keys).</param>
/// <param name="request">The parsed, decrypted request.</param>
/// <param name="cancellationToken">Cancelled when the connection closes.</param>
/// <returns>
/// The response to send. Must not await <see cref="RtspServerConnection.SendRequestAsync"/> on the same connection (the
/// read loop is busy running the handler). To close the connection from a handler, set
/// <see cref="RtspServerResponse.CloseAfter"/>; <see cref="RtspServerConnection.CloseAsync"/> called from a handler only
/// starts the close and returns without waiting for the read loop.
/// </returns>
public delegate ValueTask<RtspServerResponse> RtspRequestHandler(RtspServerConnection connection, RtspIncomingRequest request, CancellationToken cancellationToken);

/// <summary>
/// A response written by <see cref="RtspServerConnection"/>: status line with the request's protocol token echoed (the
/// srcvers-366 speaker echoes it, SPEC-control.md 1.8), <c>Content-Length</c> (always), <c>Content-Type</c> when set,
/// <c>Server</c>, <c>CSeq</c> echoed when the request had one [sps:rtsp.c:4210-4217], then <see cref="Headers"/>.
/// </summary>
/// <param name="StatusCode">The status code.</param>
/// <param name="ReasonPhrase">Reason phrase; the standard one for the code when null.</param>
public sealed record RtspServerResponse(int StatusCode, string? ReasonPhrase = null)
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    /// <summary>Extra headers (e.g. <c>Audio-Latency</c>, <c>Connection: close</c>).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = NoHeaders;

    /// <summary>Body content type, or null.</summary>
    public string? ContentType { get; init; }

    /// <summary>Body bytes.</summary>
    public ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>
    /// Keys (server view: <see cref="HapChannelKeys.WriteKey"/> encrypts what the server sends) to switch to after this
    /// response has been written in plaintext: "prepare encryption, but don't activate yet, the response to this
    /// request is still plaintext" [sps:rtsp.c:1811-1817].
    /// </summary>
    public HapChannelKeys? EnableEncryptionAfter { get; init; }

    /// <summary>Close the connection after writing this response.</summary>
    public bool CloseAfter { get; init; }

    /// <summary>Test switch: write this CSeq instead of echoing the request's.</summary>
    public int? CSeqOverride { get; init; }

    /// <summary>Test switch: do not write a CSeq header.</summary>
    public bool OmitCSeq { get; init; }

    /// <summary>
    /// Test switch: write nothing (the handler answered itself through <see cref="RtspServerConnection.WriteRawAsync"/>,
    /// or the request is meant to go unanswered). <see cref="CloseAfter"/> still applies.
    /// </summary>
    public bool Suppress { get; init; }

    /// <summary>A response that writes nothing (see <see cref="Suppress"/>).</summary>
    public static RtspServerResponse None() => new(RtspStatus.Ok) { Suppress = true };

    /// <summary>200 OK without body.</summary>
    public static RtspServerResponse Ok() => new(RtspStatus.Ok);

    /// <summary>A status without body.</summary>
    public static RtspServerResponse Status(int statusCode) => new(statusCode);

    /// <summary>200 OK with a binary plist body.</summary>
    public static RtspServerResponse Plist(object body) =>
        new(RtspStatus.Ok) { ContentType = RtspContentTypes.BinaryPlist, Body = BinaryPlist.Encode(body) };

    /// <summary>200 OK with an arbitrary body.</summary>
    public static RtspServerResponse Bytes(string contentType, ReadOnlyMemory<byte> body) =>
        new(RtspStatus.Ok) { ContentType = contentType, Body = body };

    /// <summary>The standard reason phrase for a status code (RTSP/HTTP plus the AirPlay codes of SPEC-control.md 2.4).</summary>
    public static string DefaultReason(int statusCode) => statusCode switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        451 => "Unavailable",
        453 => "Not Enough Bandwidth",
        470 => "Connection Authorization Required",
        500 => "Internal Server Error",
        501 => "Not Implemented",
        _ => "Status",
    };
}

/// <summary>Options of an <see cref="RtspServerConnection"/>.</summary>
public sealed record RtspServerConnectionOptions
{
    /// <summary>The <c>Server</c> header written on every response (the srcvers-366 speaker's value by default).</summary>
    public string ServerHeader { get; init; } = "AirTunes/366.0";

    /// <summary>
    /// Largest plaintext per HAP frame this side sends, 1..65 535. Default 1024 like every reference receiver; larger
    /// values emulate an Apple-SDK receiver sending big frames, which the sender must accept (SPEC-control.md 2.5, D9).
    /// </summary>
    public int MaxSendFramePlaintext { get; init; } = HapFrameCipher.MaxSendFramePlaintext;

    /// <summary>How long <see cref="RtspServerConnection.SendRequestAsync"/> waits for the peer's answer (the receiver-side event timeout is ~30 s, SPEC-control.md 4.3).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Write <c>CSeq</c> on requests this side sends (Apple TV: <c>CSeq: 0</c>; shairport-sync: none, SPEC-control.md 4.3).</summary>
    public bool SendCSeqOnRequests { get; init; }

    /// <summary>Label used in log messages.</summary>
    public string Name { get; init; } = "fake";
}

/// <summary>
/// The receiver side of one RTSP-over-TCP connection, for the fake receiver's control port and event channel and for
/// tests: reads requests (plaintext, then HAP-encrypted after <see cref="RtspServerResponse.EnableEncryptionAfter"/> or
/// <see cref="EnableEncryption"/>), runs the handler for each in order and writes its response; can also send requests
/// to the peer and read their answers (the event channel's <c>POST /command</c>, SPEC-control.md 4.3). Receive frames
/// up to 65 535 bytes are accepted; sent frames are split at <see cref="RtspServerConnectionOptions.MaxSendFramePlaintext"/>.
/// </summary>
public sealed class RtspServerConnection : IAsyncDisposable
{
    private const int ReadChunkSize = 16 * 1024;
    private const int LengthSize = 2;
    private const int MaxAbandoned = 32;
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    /// <summary>The connection whose handler is running in the current async flow (CloseAsync must not await its own read loop).</summary>
    private static readonly AsyncLocal<RtspServerConnection?> HandlerOf = new();

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly RtspRequestHandler _handler;
    private readonly RtspServerConnectionOptions _options;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly RtspStreamReader _reader = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly List<OutstandingRequest> _outstanding = [];
    private readonly List<int> _abandoned = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource<string> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly byte[] _nonce = new byte[ChaCha20Poly1305Aead.NonceSize];
    private Task _readLoop = Task.CompletedTask;
    private byte[]? _sendKey;
    private ulong _sendCounter;
    private long _sentFrames;
    private int _nextCSeq;
    private string? _closeReason;
    private int _disposed;

    private RtspServerConnection(Socket socket, RtspRequestHandler handler, RtspServerConnectionOptions options, ILogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxSendFramePlaintext, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaxSendFramePlaintext, HapFrameCipher.MaxReceiveFramePlaintext);
        _socket = socket;
        _socket.NoDelay = true;
        _stream = new NetworkStream(socket, ownsSocket: true);
        _handler = handler;
        _options = options;
        _logger = logger;
        RemoteEndPoint = (IPEndPoint)socket.RemoteEndPoint!;
        LocalEndPoint = (IPEndPoint)socket.LocalEndPoint!;
        MaxSendFramePlaintext = options.MaxSendFramePlaintext;
    }

    /// <summary>The peer (the sender).</summary>
    public IPEndPoint RemoteEndPoint { get; }

    /// <summary>Our end.</summary>
    public IPEndPoint LocalEndPoint { get; }

    /// <summary>True once encryption is active in both directions.</summary>
    public bool IsEncrypted { get; private set; }

    /// <summary>Largest plaintext per sent HAP frame; may be changed between writes (e.g. to send one large frame).</summary>
    public int MaxSendFramePlaintext
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, HapFrameCipher.MaxReceiveFramePlaintext);
            field = value;
        }
    }

    /// <summary>Completes when the connection closes; the result says why.</summary>
    public Task<string> Closed => _closed.Task;

    /// <summary>HAP frames received and decrypted.</summary>
    public long ReceivedFrames
    {
        get
        {
            lock (_lock)
            {
                return _reader.FramesDecrypted;
            }
        }
    }

    /// <summary>Plaintext length of the largest HAP frame received.</summary>
    public int LargestReceivedFrame
    {
        get
        {
            lock (_lock)
            {
                return _reader.LargestFramePlaintext;
            }
        }
    }

    /// <summary>HAP frames sent.</summary>
    public long SentFrames => Interlocked.Read(ref _sentFrames);

    /// <summary>Starts serving an accepted socket.</summary>
    /// <param name="socket">The connected socket.</param>
    /// <param name="handler">Serves incoming requests.</param>
    /// <param name="options">Connection options.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="keys">
    /// When set, HAP encryption is on before the read loop starts (server's view, as for <see cref="EnableEncryption"/>).
    /// Use it for a channel that is encrypted from its first byte and whose peer may send at once: switching with
    /// <see cref="EnableEncryption"/> after <c>Start</c> races the read loop, which may already have taken the first
    /// ciphertext as plaintext and then waits for more bytes instead of serving the request.
    /// </param>
    public static RtspServerConnection Start(Socket socket, RtspRequestHandler handler, RtspServerConnectionOptions? options = null, ILogger? logger = null, HapChannelKeys? keys = null)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(handler);
        var connection = new RtspServerConnection(socket, handler, options ?? new RtspServerConnectionOptions(), logger ?? NullLogger.Instance);
        if (keys is not null)
        {
            connection.EnableEncryptionCore(keys);
        }

        connection._readLoop = connection.ReadLoopAsync();
        return connection;
    }

    /// <summary>Accepts one connection on a listening socket and starts serving it.</summary>
    public static async Task<RtspServerConnection> AcceptAsync(Socket listener, RtspRequestHandler handler, RtspServerConnectionOptions? options, ILogger? logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(listener);
        var socket = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
        return Start(socket, handler, options, logger);
    }

    /// <summary>
    /// Switches both directions to HAP framing now (the event channel is encrypted from its first byte). Keys are the
    /// server's view: <see cref="HapChannelKeys.WriteKey"/> encrypts what this side sends; pass
    /// <c>HapChannelKeys.ForControl(ss).Swapped()</c> / <c>ForEvents(ss).Swapped()</c>.
    /// </summary>
    public void EnableEncryption(HapChannelKeys keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (!_writeGate.Wait(0))
        {
            throw new InvalidOperationException("EnableEncryption must not run concurrently with a write.");
        }

        try
        {
            EnableEncryptionCore(keys);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Sends a request to the peer (as a receiver does on the event channel) and waits for its answer, written by the
    /// codec (<see cref="RtspMessageCodec"/>): request line, optional <c>CSeq</c>, <c>Content-Type</c>/<c>Content-Length</c>
    /// per the <see cref="RtspRequest"/> rule, then <paramref name="headers"/>. Answers are matched by <c>CSeq</c> when
    /// both sides write one, otherwise to the oldest outstanding request. A request that times out
    /// (<see cref="TimeoutException"/>) or is cancelled is taken out of the queue, so an unanswered request does not
    /// swallow the answer to the next one; its late answer is recognised and dropped by <c>CSeq</c>
    /// (<see cref="RtspServerConnectionOptions.SendCSeqOnRequests"/>), without CSeqs it would go to the next request.
    /// </summary>
    public async Task<RtspResponse> SendRequestAsync(
        string method,
        string uri,
        string? contentType,
        ReadOnlyMemory<byte> body,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        int? cseq = _options.SendCSeqOnRequests ? Interlocked.Increment(ref _nextCSeq) - 1 : null;
        var message = RtspMessageCodec.EncodeBareRequest(method, uri, cseq, contentType, headers ?? NoHeaders, body.Span);
        var pending = new OutstandingRequest(cseq);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                ThrowIfClosed();
                _outstanding.Add(pending);
            }

            LogMessage("->", message);
            await WriteMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Abandon(pending);
            throw;
        }
        finally
        {
            _writeGate.Release();
        }

        try
        {
            return await pending.Completion.Task.WaitAsync(_options.RequestTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (!Abandon(pending))
            {
                // the answer (or the close) raced the timeout
                return await pending.Completion.Task.ConfigureAwait(false);
            }

            throw;
        }
        catch (OperationCanceledException)
        {
            Abandon(pending);
            throw;
        }
    }

    /// <summary>
    /// Test hook: writes <paramref name="plaintext"/> as given, bypassing the codec. Encrypted: one HAP frame per entry of
    /// <paramref name="chunkSizes"/> (frames not aligned with messages, or one frame larger than 1024 bytes); plaintext:
    /// one socket write per entry with a short pause in between so the peer sees split reads. Null = the normal split.
    /// </summary>
    public async Task WriteRawAsync(ReadOnlyMemory<byte> plaintext, IReadOnlyList<int>? chunkSizes, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (chunkSizes is null)
            {
                await WriteMessageAsync(plaintext, cancellationToken).ConfigureAwait(false);
                return;
            }

            var offset = 0;
            foreach (var size in chunkSizes)
            {
                var chunk = plaintext.Slice(offset, size);
                offset += size;
                if (_sendKey is not null)
                {
                    await _stream.WriteAsync(EncryptFrames(chunk.Span, Math.Max(1, size)), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _stream.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
                    await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                }
            }

            if (offset != plaintext.Length)
            {
                await WriteMessageAsync(plaintext[offset..], cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Test hook: writes bytes to the socket without any framing or encryption (e.g. a corrupt frame).</summary>
    public async Task WriteWireAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Closes the connection (as a receiver does when another sender takes over) and waits for the read loop to end.
    /// Called from a request handler (which runs on the read loop) it only starts the close and returns at once, since
    /// awaiting the loop there would deadlock; <see cref="RtspServerResponse.CloseAfter"/> is the usual way for a handler.
    /// </summary>
    public async Task CloseAsync()
    {
        Close("closed by the server");
        if (HandlerOf.Value != this)
        {
            await _readLoop.ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>Like <see cref="CloseAsync"/>, does not wait for the read loop when called from a request handler.</remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Close("disposed");
        if (HandlerOf.Value == this)
        {
            return; // the read loop still runs this handler; _shutdown is left to the GC (no timer is attached)
        }

        await _readLoop.ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private void EnableEncryptionCore(HapChannelKeys keys)
    {
        lock (_lock)
        {
            if (_sendKey is not null)
            {
                throw new InvalidOperationException("Encryption is already enabled.");
            }

            _reader.EnableDecryption(new HapFrameCipher(keys));
            _sendKey = keys.WriteKey;
            IsEncrypted = true;
        }

        _logger.LogDebug("RTSP server ({Name}) {Peer}: switched to HAP encryption", _options.Name, RemoteEndPoint);
    }

    private async Task ReadLoopAsync()
    {
        await Task.Yield();
        var chunk = new byte[ReadChunkSize];
        var reason = "peer closed the connection";
        try
        {
            while (true)
            {
                var read = await _stream.ReadAsync(chunk, _shutdown.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                lock (_lock)
                {
                    _reader.Append(chunk.AsSpan(0, read));
                }

                while (TakeRequest() is { } request)
                {
                    await HandleAsync(request).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when ((ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException) && Volatile.Read(ref _closeReason) is not null)
        {
            reason = Volatile.Read(ref _closeReason)!;
        }
        catch (CryptographicException ex)
        {
            reason = $"decryption failed: {ex.Message}";
        }
        catch (InvalidDataException ex)
        {
            reason = $"protocol error: {ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            reason = $"read failed: {ex.Message}";
        }
        catch (Exception ex)
        {
            reason = $"read loop failed: {ex.Message}";
            _logger.LogError(ex, "RTSP server ({Name}): read loop failed", _options.Name);
        }

        Close(reason);
    }

    /// <summary>Completes answers to our own requests and returns the next complete incoming request, if any.</summary>
    private RtspIncomingRequest? TakeRequest()
    {
        lock (_lock)
        {
            while (true)
            {
                _reader.SkipEmptyLines();
                switch (_reader.PeekKind())
                {
                    case RtspMessageKind.None:
                        return null;
                    case RtspMessageKind.Response:
                        if (!_reader.TryReadResponse(out var response))
                        {
                            return null;
                        }

                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.LogDebug("RTSP server ({Name}) <- {Peer}\n{Message}", _options.Name, RemoteEndPoint, RtspWireLog.Describe(response!, full: false));
                        }

                        CompleteLocked(response!);
                        continue;
                    default:
                        if (!_reader.TryReadRequest(out var request))
                        {
                            return null;
                        }

                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.LogDebug("RTSP server ({Name}) <- {Peer}{Encrypted}\n{Message}", _options.Name, RemoteEndPoint, _reader.IsEncrypted ? " (encrypted)" : string.Empty, RtspWireLog.Describe(request!, full: false));
                        }

                        return request;
                }
            }
        }
    }

    /// <summary>Hands a response to its request: by CSeq when it carries one, else to the oldest; late answers to abandoned CSeqs are dropped.</summary>
    private void CompleteLocked(RtspResponse response)
    {
        var cseq = response.CSeq;
        if (cseq is { } late && _abandoned.Remove(late))
        {
            _logger.LogDebug("RTSP server ({Name}): dropping late response {Status} to abandoned CSeq {CSeq}", _options.Name, response.StatusCode, late);
            return;
        }

        var match = cseq is null ? null : _outstanding.Find(p => p.CSeq == cseq);
        if (match is null && _outstanding.Count > 0)
        {
            match = _outstanding[0];
        }

        if (match is null)
        {
            _logger.LogWarning("RTSP server ({Name}): response {Status} without a request", _options.Name, response.StatusCode);
            return;
        }

        _outstanding.Remove(match);
        match.Completion.TrySetResult(response);
    }

    /// <summary>Takes a request that timed out or was cancelled out of the queue; false when it was answered or failed meanwhile.</summary>
    private bool Abandon(OutstandingRequest pending)
    {
        lock (_lock)
        {
            if (!_outstanding.Remove(pending))
            {
                return false;
            }

            if (pending.CSeq is { } cseq)
            {
                _abandoned.Add(cseq);
                if (_abandoned.Count > MaxAbandoned)
                {
                    _abandoned.RemoveAt(0);
                }
            }

            return true;
        }
    }

    private async Task HandleAsync(RtspIncomingRequest request)
    {
        RtspServerResponse response;
        try
        {
            HandlerOf.Value = this; // flows into the handler; restored for the read loop when this method returns
            response = await _handler(this, request, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RTSP server ({Name}): handler failed for {Method} {Uri}", _options.Name, request.Method, request.Uri);
            response = RtspServerResponse.Status(RtspStatus.InternalError);
        }

        if (response.Suppress)
        {
            if (response.CloseAfter)
            {
                Close("closed by the handler");
            }

            return;
        }

        var headers = new List<KeyValuePair<string, string>>();
        if (response.ContentType is not null)
        {
            headers.Add(new(RtspHeaders.ContentType, response.ContentType));
        }

        headers.Add(new(RtspHeaders.Server, _options.ServerHeader));
        var cseq = response.CSeqOverride?.ToString(CultureInfo.InvariantCulture) ?? request.Header(RtspHeaders.CSeq);
        if (cseq is not null && !response.OmitCSeq)
        {
            headers.Add(new(RtspHeaders.CSeq, cseq));
        }

        headers.AddRange(response.Headers);
        var ordered = new OrderedDictionary<string, string>(headers.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in headers)
        {
            ordered[k] = v;
        }

        var bytes = RtspMessageCodec.EncodeResponse(
            request.Protocol,
            response.StatusCode,
            response.ReasonPhrase ?? RtspServerResponse.DefaultReason(response.StatusCode),
            ordered,
            response.Body);
        await _writeGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        try
        {
            LogMessage("->", bytes);
            await WriteMessageAsync(bytes, _shutdown.Token).ConfigureAwait(false);
            if (response.EnableEncryptionAfter is { } keys)
            {
                EnableEncryptionCore(keys);
            }
        }
        finally
        {
            _writeGate.Release();
        }

        if (response.CloseAfter)
        {
            Close("closed after response");
        }
    }

    private async Task WriteMessageAsync(ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken)
    {
        var wire = _sendKey is null ? plaintext : EncryptFrames(plaintext.Span, MaxSendFramePlaintext);
        await _stream.WriteAsync(wire, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>HAP frames (SPEC-control.md 2.5) with a configurable size; the sender's own cipher caps at 1024 by design.</summary>
    private byte[] EncryptFrames(ReadOnlySpan<byte> plaintext, int maxFrame)
    {
        var frames = Math.Max(1, (plaintext.Length + maxFrame - 1) / maxFrame);
        var output = new byte[plaintext.Length + (frames * (LengthSize + ChaCha20Poly1305Aead.TagSize))];
        var written = 0;
        var offset = 0;
        do
        {
            var len = Math.Min(maxFrame, plaintext.Length - offset);
            var header = output.AsSpan(written, LengthSize);
            BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)len);
            ChaCha20Poly1305Aead.NonceFromCounter(_sendCounter++, _nonce);
            ChaCha20Poly1305Aead.Encrypt(
                _sendKey,
                _nonce,
                plaintext.Slice(offset, len),
                header,
                output.AsSpan(written + LengthSize, len),
                output.AsSpan(written + LengthSize + len, ChaCha20Poly1305Aead.TagSize));
            written += LengthSize + len + ChaCha20Poly1305Aead.TagSize;
            offset += len;
            Interlocked.Increment(ref _sentFrames);
        }
        while (offset < plaintext.Length);

        return output;
    }

    private void LogMessage(string direction, ReadOnlySpan<byte> message)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("RTSP server ({Name}) {Direction} {Peer}\n{Message}", _options.Name, direction, RemoteEndPoint, RtspWireLog.DescribeEncoded(message, full: false));
        }
    }

    private void ThrowIfClosed()
    {
        if (Volatile.Read(ref _closeReason) is { } reason)
        {
            throw new IOException($"Server connection to {RemoteEndPoint} is closed: {reason}");
        }
    }

    private void Close(string reason)
    {
        OutstandingRequest[] failed;
        lock (_lock)
        {
            if (_closeReason is not null)
            {
                return;
            }

            Volatile.Write(ref _closeReason, reason);
            failed = [.. _outstanding];
            _outstanding.Clear();
        }

        _logger.LogDebug("RTSP server ({Name}) connection from {Peer} closed: {Reason}", _options.Name, RemoteEndPoint, reason);
        foreach (var pending in failed)
        {
            pending.Completion.TrySetException(new IOException($"Server connection closed: {reason}"));
            _ = pending.Completion.Task.Exception; // observed: the caller may have timed out in the same instant
        }

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
            // peer already gone
        }

        _stream.Dispose();
        _closed.TrySetResult(reason);
    }

    private sealed class OutstandingRequest(int? cseq)
    {
        public int? CSeq { get; } = cseq;

        public TaskCompletionSource<RtspResponse> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
