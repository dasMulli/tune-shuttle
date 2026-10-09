using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Airplay2.Sender.Crypto;
using Microsoft.Extensions.Logging;

namespace Airplay2.Sender.Rtsp;

/// <summary>
/// One TCP connection to a receiver's AirPlay port carrying pairing POSTs, GET /info, RTSP methods and keep-alives
/// (SPEC-control.md 2). After pairing the same socket switches to the HAP frame encryption (2.5). Requests are
/// serialised one at a time; responses are matched by CSeq with FIFO fallback.
/// </summary>
public interface IRtspConnection : IAsyncDisposable
{
    /// <summary>The receiver endpoint.</summary>
    IPEndPoint RemoteEndPoint { get; }

    /// <summary>Our address on this connection: used in the session URI and as the timing/control address we announce.</summary>
    IPAddress LocalAddress { get; }

    /// <summary>The identity headers used on every request.</summary>
    RtspClientIdentity Identity { get; }

    /// <summary>Random 32-bit session id of this connection; the session URI is <c>rtsp://&lt;LocalAddress&gt;/&lt;SessionId&gt;</c>.</summary>
    uint SessionId { get; }

    /// <summary>The session URI for RTSP methods.</summary>
    string SessionUri { get; }

    /// <summary>True once <see cref="EnableEncryption"/> was called.</summary>
    bool IsEncrypted { get; }

    /// <summary>Completes when the receiver closes the socket or a read fails; the result describes why.</summary>
    Task<string> Closed { get; }

    /// <summary>Sends one request and waits for its response. Non-2xx responses are returned, not thrown.</summary>
    Task<RtspResponse> SendAsync(RtspRequest request, CancellationToken cancellationToken);

    /// <summary>Sends and throws <see cref="RtspStatusException"/> on a non-2xx status.</summary>
    Task<RtspResponse> SendExpectOkAsync(RtspRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Switches the socket to HAP framing. Must be called after the plaintext pairing response has been fully read and
    /// before the next request (the receiver activates encryption on its next read).
    /// </summary>
    void EnableEncryption(HapChannelKeys keys);
}

/// <summary>Connection options.</summary>
public sealed record RtspConnectionOptions
{
    /// <summary>TCP connect timeout. The SpeakerA needed up to 25 s on Wi-Fi once (SPEC-audio.md appendix A).</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// Per-request timeout, from writing the request to its response (pyatv: 4 s for RTSP exchanges, 10 s for pairing
    /// POSTs [pyatv:pyatv/support/rtsp.py:316; http.py:446]; we use 10 s for everything, SPEC.md D6).
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Identity headers; random when null.</summary>
    public RtspClientIdentity? Identity { get; init; }

    /// <summary>Optional <c>X-Apple-Client-Name</c> value, written on every request that does not carry its own (owntone does; shairport-sync only logs it).</summary>
    public string? ClientName { get; init; }
}

/// <summary>
/// Socket implementation of <see cref="IRtspConnection"/> (SPEC-control.md 2.1-2.5, SPEC.md D6): a raw TCP socket
/// (no HTTP client library can switch an open socket to HAP framing [pyatv:pyatv/support/http.py:326-357]), one
/// request in flight at a time, responses matched by <c>CSeq</c> with a fallback to the oldest outstanding request
/// [pyatv:pyatv/support/rtsp.py:291-330; http.py:385-404], a per-request timeout, and a background read loop that
/// decrypts HAP frames of up to 65 535 bytes once <see cref="EnableEncryption"/> was called and completes
/// <see cref="Closed"/> when the receiver closes the socket or a read/decrypt fails.
/// </summary>
/// <remarks>
/// <para>
/// Timeouts: a request whose response does not arrive within <see cref="RtspConnectionOptions.RequestTimeout"/> throws
/// <see cref="TimeoutException"/>; its <c>CSeq</c> is remembered so a late response is discarded instead of being
/// handed to the next request by the FIFO fallback. The connection stays open. A write that fails or is interrupted
/// closes the connection (the receiver's framing position, and under encryption the nonce counter, would be lost).
/// </para>
/// <para>
/// A decryption failure is fatal: the socket is closed and <see cref="Closed"/> reports it; the caller re-runs
/// pair-verify on a new connection (SPEC-control.md 2.5, [sps:pair_homekit.c:3080-3086]).
/// </para>
/// <para>
/// Plaintext bytes that arrive while no request is outstanding are buffered but not parsed: after the plaintext pairing
/// response they may already be ciphertext (SPEC-control.md 2.5), which only the caller knows. They are parsed as
/// plaintext when the next request goes out unencrypted, or re-read as ciphertext by <see cref="EnableEncryption"/>.
/// </para>
/// <para>
/// Logging: every message is logged at <c>Debug</c> after decryption (head verbatim, body as text, plist or a hex
/// preview); at <c>Trace</c> binary bodies are logged in full.
/// </para>
/// </remarks>
public sealed class RtspConnection : IRtspConnection
{
    private const int ReadChunkSize = 16 * 1024;
    private const int MaxAbandoned = 32;
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(1);

    private readonly NetworkStream _stream;
    private readonly Socket _socket;
    private readonly RtspConnectionOptions _options;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly RtspStreamReader _reader = new();
    private readonly List<PendingRequest> _outstanding = [];
    private readonly Queue<int> _abandoned = new();
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource<string> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _readLoop = Task.CompletedTask;
    private HapFrameCipher? _sendCipher;
    private int _nextCSeq;
    private string? _closeReason;
    private int _disposed;

    private RtspConnection(Socket socket, IPEndPoint remote, RtspConnectionOptions options, ILogger logger)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: true);
        _options = options;
        _logger = logger;
        RemoteEndPoint = remote;
        var local = ((IPEndPoint)socket.LocalEndPoint!).Address;
        LocalAddress = local.IsIPv4MappedToIPv6 ? local.MapToIPv4() : local;
        Identity = options.Identity ?? RtspClientIdentity.CreateRandom();
        Span<byte> id = stackalloc byte[4];
        RandomNumberGenerator.Fill(id);
        SessionId = BinaryPrimitives.ReadUInt32LittleEndian(id);
    }

    /// <inheritdoc />
    public IPEndPoint RemoteEndPoint { get; }

    /// <inheritdoc />
    public IPAddress LocalAddress { get; }

    /// <inheritdoc />
    public RtspClientIdentity Identity { get; }

    /// <inheritdoc />
    public uint SessionId { get; }

    /// <inheritdoc />
    /// <remarks>IPv6 addresses are bracketed and written without a scope id (<c>rtsp://[v6]/id</c> [owntone:airplay.c:1534-1537]).</remarks>
    public string SessionUri => LocalAddress.AddressFamily == AddressFamily.InterNetworkV6
        ? $"rtsp://[{new IPAddress(LocalAddress.GetAddressBytes())}]/{SessionId}"
        : $"rtsp://{LocalAddress}/{SessionId}";

    /// <inheritdoc />
    public bool IsEncrypted { get; private set; }

    /// <inheritdoc />
    public Task<string> Closed => _closed.Task;

    /// <summary>Number of HAP frames received and decrypted (probe P21 logs frame sizes).</summary>
    internal long ReceivedFrames
    {
        get
        {
            lock (_lock)
            {
                return _reader.FramesDecrypted;
            }
        }
    }

    /// <summary>Plaintext length of the largest HAP frame received so far.</summary>
    internal int LargestReceivedFrame
    {
        get
        {
            lock (_lock)
            {
                return _reader.LargestFramePlaintext;
            }
        }
    }

    /// <summary>
    /// Connects to <paramref name="remote"/>, choosing the local address the OS routes through. A sleeping receiver may
    /// take up to ~20 s to wake [pyatv:pyatv/support/http.py:36-39]; failed attempts (refused, unreachable, the OS's own
    /// SYN timeout) are retried once per second until <see cref="RtspConnectionOptions.ConnectTimeout"/> (25 s) has
    /// passed, then <see cref="TimeoutException"/> is thrown.
    /// </summary>
    public static async Task<RtspConnection> ConnectAsync(IPEndPoint remote, RtspConnectionOptions options, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.ConnectTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.RequestTimeout, TimeSpan.Zero);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.ConnectTimeout);
        var started = Stopwatch.GetTimestamp();
        var attempt = 0;
        while (true)
        {
            attempt++;
            var socket = new Socket(remote.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(remote, deadline.Token).ConfigureAwait(false);
                ConfigureKeepAlive(socket, logger);
                var connection = new RtspConnection(socket, remote, options, logger);
                logger.LogDebug(
                    "RTSP connected to {Receiver} from {Local} after {Attempts} attempt(s), session URI {SessionUri}",
                    remote,
                    socket.LocalEndPoint,
                    attempt,
                    connection.SessionUri);
                connection.Start();
                return connection;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                throw new TimeoutException($"Connecting to {remote} timed out after {options.ConnectTimeout.TotalSeconds:0.#} s ({attempt} attempt(s)).");
            }
            catch (SocketException ex) when (IsRetriable(ex.SocketErrorCode))
            {
                socket.Dispose();
                var remaining = options.ConnectTimeout - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"Connecting to {remote} failed for {options.ConnectTimeout.TotalSeconds:0.#} s ({attempt} attempt(s)): {ex.SocketErrorCode}.", ex);
                }

                logger.LogDebug("RTSP connect to {Receiver} failed ({Error}), retrying", remote, ex.SocketErrorCode);
                try
                {
                    await Task.Delay(remaining < ConnectRetryDelay ? remaining : ConnectRetryDelay, deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"Connecting to {remote} failed for {options.ConnectTimeout.TotalSeconds:0.#} s ({attempt} attempt(s)): {ex.SocketErrorCode}.", ex);
                }
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    /// <inheritdoc />
    /// <exception cref="TimeoutException">No response within <see cref="RtspConnectionOptions.RequestTimeout"/>.</exception>
    /// <exception cref="IOException">The connection is closed or closed while waiting.</exception>
    public async Task<RtspResponse> SendAsync(RtspRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<RtspResponse> SendExpectOkAsync(RtspRequest request, CancellationToken cancellationToken)
    {
        var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.IsSuccess ? response : throw new RtspStatusException(request, response);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Implements the "response to the pairing request is still plaintext" rule (SPEC-control.md 2.5
    /// [sps:rtsp.c:1811-1817]): the pairing response has been read in plaintext by the time this is called; from here on
    /// the read loop decrypts and every request is encrypted. Bytes that arrived after that response (none from a
    /// conforming receiver) were held unparsed by the read loop and are re-read as ciphertext.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A request is in flight, or encryption is already on.</exception>
    public void EnableEncryption(HapChannelKeys keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_requestGate.Wait(0))
        {
            throw new InvalidOperationException("EnableEncryption must be called between requests, not while one is in flight.");
        }

        try
        {
            ThrowIfClosed();
            lock (_lock)
            {
                if (_sendCipher is not null)
                {
                    throw new InvalidOperationException("Encryption is already enabled on this connection.");
                }

                if (_reader.BufferedPlaintext > 0)
                {
                    _logger.LogWarning(
                        "RTSP {Receiver}: {Bytes} unparsed bytes followed the pairing response; treating them as ciphertext",
                        RemoteEndPoint,
                        _reader.BufferedPlaintext);
                }

                // Separate cipher instances per direction: the read loop decrypts while a writer may be encrypting.
                _reader.EnableDecryption(new HapFrameCipher(keys));
                _sendCipher = new HapFrameCipher(keys);
                IsEncrypted = true;
                DispatchLocked();
            }
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException)
        {
            Close($"decryption failed: {ex.Message}");
            throw;
        }
        finally
        {
            _requestGate.Release();
        }

        _logger.LogDebug("RTSP {Receiver}: switched to HAP encryption", RemoteEndPoint);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Close("disposed");
        await _readLoop.ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private static bool IsRetriable(SocketError error) => error is
        SocketError.TimedOut or SocketError.ConnectionRefused or SocketError.HostUnreachable or
        SocketError.NetworkUnreachable or SocketError.HostDown or SocketError.TryAgain or SocketError.NetworkDown;

    private static void ConfigureKeepAlive(Socket socket, ILogger logger)
    {
        // TCP keep-alive notices a receiver that vanished while the connection is idle (shairport-sync does the same on
        // its side of the control socket [sps:rtsp.c:4556-4590]); feedback every 2 s (SPEC-control.md 4.10) is the
        // primary liveness check, this one covers the time before the session is set up.
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 4);
        }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException)
        {
            logger.LogDebug(ex, "TCP keep-alive options not supported on this platform");
        }
    }

    private void Start() => _readLoop = ReadLoopAsync();

    private async Task<RtspResponse> ExchangeAsync(RtspRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfClosed();
        var cseq = _nextCSeq++;
        var wire = RtspMessageCodec.EncodeRequest(request, cseq, Identity, _options.ClientName);
        var pending = new PendingRequest(cseq);
        InvalidDataException? protocolError = null;
        lock (_lock)
        {
            ThrowIfClosed();
            try
            {
                // Plaintext held while nothing was outstanding (see DispatchLocked) is plaintext after all, since this
                // request goes out unencrypted: parse it now, before this request can claim a stale response (nothing
                // is held once the connection is encrypted).
                DispatchLocked(dispatchHeld: true);
                _outstanding.Add(pending);
            }
            catch (InvalidDataException ex)
            {
                protocolError = ex;
            }
        }

        if (protocolError is not null)
        {
            var reason = $"protocol error: {protocolError.Message}";
            _logger.LogWarning("RTSP {Receiver}: {Reason}", RemoteEndPoint, reason);
            Close(reason);
            throw new IOException($"{request.Method} {request.Uri} to {RemoteEndPoint}: {reason}", protocolError);
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "RTSP -> {Receiver}{Encrypted}\n{Message}",
                RemoteEndPoint,
                _sendCipher is null ? string.Empty : " (encrypted)",
                RtspWireLog.DescribeEncoded(wire, _logger.IsEnabled(LogLevel.Trace)));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        var payload = _sendCipher is { } cipher ? cipher.Encrypt(wire) : wire;
        try
        {
            await _stream.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            RemovePending(pending);
            var userCancelled = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
            var reason = ex switch
            {
                OperationCanceledException when userCancelled => "write cancelled",
                OperationCanceledException => "write timed out",
                _ => $"write failed: {ex.Message}",
            };

            // A partially written message (and, encrypted, a consumed nonce) cannot be recovered on this socket.
            var priorReason = Volatile.Read(ref _closeReason);
            Close(reason);
            if (userCancelled)
            {
                throw;
            }

            if (ex is OperationCanceledException)
            {
                throw new TimeoutException($"{request.Method} {request.Uri}: {reason} after {_options.RequestTimeout.TotalSeconds:0.#} s.", ex);
            }

            throw new IOException($"{request.Method} {request.Uri} to {RemoteEndPoint}: {priorReason ?? reason}", ex);
        }

        try
        {
            return await pending.Completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!Abandon(pending))
            {
                // The response or the connection's close won the race with the timeout: report what happened. The
                // completion is already set (response) or about to be (Close faults it right after taking it out).
                return await pending.Completion.Task.ConfigureAwait(false);
            }

            _logger.LogWarning(
                "RTSP {Receiver}: no response to {Method} {Uri} (CSeq {CSeq}) within {Timeout} s",
                RemoteEndPoint,
                request.Method,
                request.Uri,
                cseq,
                _options.RequestTimeout.TotalSeconds);
            throw new TimeoutException($"No response to {request.Method} {request.Uri} (CSeq {cseq}) from {RemoteEndPoint} within {_options.RequestTimeout.TotalSeconds:0.#} s.");
        }
        catch (OperationCanceledException)
        {
            // A response that raced the caller's cancellation is dropped; a fault set by Close is observed there.
            Abandon(pending);
            throw;
        }
    }

    private async Task ReadLoopAsync()
    {
        await Task.Yield();
        var chunk = new byte[ReadChunkSize];
        var reason = "receiver closed the connection";
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
                    DispatchLocked();
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
            _logger.LogWarning("RTSP {Receiver}: {Reason}", RemoteEndPoint, reason);
        }
        catch (InvalidDataException ex)
        {
            reason = $"protocol error: {ex.Message}";
            _logger.LogWarning("RTSP {Receiver}: {Reason}", RemoteEndPoint, reason);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            reason = $"read failed: {ex.Message}";
        }
        catch (Exception ex)
        {
            reason = $"read loop failed: {ex.Message}";
            _logger.LogError(ex, "RTSP {Receiver}: read loop failed", RemoteEndPoint);
        }

        Close(reason);
    }

    /// <summary>
    /// Parses and dispatches the buffered plaintext. In plaintext mode with no request outstanding the bytes are held
    /// (unless <paramref name="dispatchHeld"/>): the last response may have been the pairing response, after which the
    /// receiver sends ciphertext (SPEC-control.md 2.5 [sps:rtsp.c:1811-1817]), and stripping "empty lines" or parsing a
    /// message from those bytes would corrupt them before <see cref="EnableEncryption"/> re-reads them (a frame whose
    /// length byte is 0x0D or 0x0A looks like a keep-alive CRLF).
    /// </summary>
    private void DispatchLocked(bool dispatchHeld = false)
    {
        while (true)
        {
            if (!dispatchHeld && !_reader.IsEncrypted && _outstanding.Count == 0)
            {
                return;
            }

            _reader.SkipEmptyLines();
            switch (_reader.PeekKind())
            {
                case RtspMessageKind.None:
                    return;
                case RtspMessageKind.Request:
                    if (!_reader.TryReadRequest(out var incoming))
                    {
                        return;
                    }

                    _logger.LogWarning(
                        "RTSP {Receiver}: ignoring unexpected request {Method} {Uri} on the control connection",
                        RemoteEndPoint,
                        incoming!.Method,
                        incoming.Uri);
                    continue;
                default:
                    if (!_reader.TryReadResponse(out var response))
                    {
                        return;
                    }

                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug(
                            "RTSP <- {Receiver}{Encrypted}\n{Message}",
                            RemoteEndPoint,
                            _reader.IsEncrypted
                                ? $" (encrypted; frames {_reader.FramesDecrypted}, last {_reader.LastFramePlaintext} B, largest {_reader.LargestFramePlaintext} B)"
                                : string.Empty,
                            RtspWireLog.Describe(response!, _logger.IsEnabled(LogLevel.Trace)));
                    }

                    CompleteLocked(response!);
                    continue;
            }
        }
    }

    private void CompleteLocked(RtspResponse response)
    {
        PendingRequest? match = null;
        var cseq = response.CSeq;
        if (cseq is { } c)
        {
            match = _outstanding.Find(p => p.CSeq == c);
        }

        if (match is null)
        {
            if (cseq is { } late && _abandoned.Contains(late))
            {
                _logger.LogDebug("RTSP {Receiver}: discarding late response to abandoned CSeq {CSeq}", RemoteEndPoint, late);
                return;
            }

            if (_outstanding.Count == 0)
            {
                _logger.LogWarning(
                    "RTSP {Receiver}: discarding response {Status} (CSeq {CSeq}) without an outstanding request",
                    RemoteEndPoint,
                    response.StatusCode,
                    cseq?.ToString(CultureInfo.InvariantCulture) ?? "none");
                return;
            }

            match = _outstanding[0];
            if (cseq is not null)
            {
                _logger.LogWarning(
                    "RTSP {Receiver}: response CSeq {Got} matches no request; assigning it to the oldest outstanding CSeq {Expected}",
                    RemoteEndPoint,
                    cseq,
                    match.CSeq);
            }
        }

        _outstanding.Remove(match);
        match.Completion.TrySetResult(response);
    }

    private void RemovePending(PendingRequest pending)
    {
        lock (_lock)
        {
            _outstanding.Remove(pending);
        }
    }

    /// <summary>Gives up on a request; false when it was completed or failed in the meantime.</summary>
    private bool Abandon(PendingRequest pending)
    {
        lock (_lock)
        {
            if (!_outstanding.Remove(pending))
            {
                return false;
            }

            _abandoned.Enqueue(pending.CSeq);
            while (_abandoned.Count > MaxAbandoned)
            {
                _abandoned.Dequeue();
            }

            return true;
        }
    }

    private void ThrowIfClosed()
    {
        if (Volatile.Read(ref _closeReason) is { } reason)
        {
            throw new IOException($"Connection to {RemoteEndPoint} is closed: {reason}");
        }
    }

    private void Close(string reason)
    {
        PendingRequest[] failed;
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

        _logger.Log(
            reason == "disposed" ? LogLevel.Debug : LogLevel.Information,
            "RTSP connection to {Receiver} closed: {Reason}",
            RemoteEndPoint,
            reason);
        foreach (var pending in failed)
        {
            pending.Completion.TrySetException(new IOException($"Connection to {RemoteEndPoint} closed: {reason}"));

            // Mark the fault observed: a caller that timed out or was cancelled in the same instant no longer awaits it
            // (UnobservedTaskException otherwise); a caller that still awaits gets the exception as usual.
            _ = pending.Completion.Task.Exception;
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
            // the peer may already be gone
        }

        _stream.Dispose();
        _closed.TrySetResult(reason);
    }

    private sealed class PendingRequest(int cseq)
    {
        public int CSeq { get; } = cseq;

        public TaskCompletionSource<RtspResponse> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
