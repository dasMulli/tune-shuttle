using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Airplay2.Sender.Crypto;
using Airplay2.Sender.Rtsp;
using Microsoft.Extensions.Logging;

namespace Airplay2.Sender.Session;

/// <summary>
/// A minimal, test-only control connection for probe P21 (SPEC-control.md 7.15): it pairs like any connection (it is an
/// <see cref="IRtspConnection"/>, so <see cref="Pairing.HapPairingClient"/> runs over it) and can then send one request
/// as a <b>single</b> HAP frame larger than the 1024-byte cap. The library's send path (<see cref="RtspConnection"/>,
/// <see cref="HapFrameCipher"/>) never does that: the cap there is a constant (SPEC.md D9). This class exists only so the
/// probe can ask the receiver whether it accepts such a frame; it is internal and used by nothing else.
/// </summary>
/// <remarks>Strictly one request at a time, no pipelining, no background reader: the probe drives it step by step.</remarks>
internal sealed class OversizedFrameProbeConnection : IRtspConnection
{
    private const string PaddingHeader = "X-Probe-Padding";
    private const int ReadChunkSize = 16 * 1024;

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly ILogger _logger;
    private readonly TimeSpan _requestTimeout;
    private readonly RtspStreamReader _reader = new();
    private readonly TaskCompletionSource<string> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly byte[] _chunk = new byte[ReadChunkSize];
    private HapFrameCipher? _sendCipher;
    private HapChannelKeys? _keys;
    private bool _nonceDiverged;
    private int _nextCSeq;

    private OversizedFrameProbeConnection(Socket socket, IPEndPoint remote, TimeSpan requestTimeout, ILogger logger)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: true);
        _logger = logger;
        _requestTimeout = requestTimeout;
        RemoteEndPoint = remote;
        var local = ((IPEndPoint)socket.LocalEndPoint!).Address;
        LocalAddress = local.IsIPv4MappedToIPv6 ? local.MapToIPv4() : local;
        Identity = RtspClientIdentity.CreateRandom();
        SessionId = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
    }

    public IPEndPoint RemoteEndPoint { get; }

    public IPAddress LocalAddress { get; }

    public RtspClientIdentity Identity { get; }

    public uint SessionId { get; }

    public string SessionUri => $"rtsp://{LocalAddress}/{SessionId}";

    public bool IsEncrypted => _sendCipher is not null;

    public Task<string> Closed => _closed.Task;

    /// <summary>Plaintext length of the largest frame the receiver sent.</summary>
    public int LargestReceivedFrame => _reader.LargestFramePlaintext;

    public static async Task<OversizedFrameProbeConnection> ConnectAsync(IPEndPoint remote, TimeSpan connectTimeout, TimeSpan requestTimeout, ILogger logger, CancellationToken cancellationToken)
    {
        var socket = new Socket(remote.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(connectTimeout);
        try
        {
            await socket.ConnectAsync(remote, timeout.Token).ConfigureAwait(false);
            return new OversizedFrameProbeConnection(socket, remote, requestTimeout, logger);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new TimeoutException($"Connecting to {remote} timed out.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async Task<RtspResponse> SendAsync(RtspRequest request, CancellationToken cancellationToken)
    {
        if (_nonceDiverged)
        {
            // The oversized frame consumed a nonce outside the cipher, which cannot skip ahead: the connection is
            // single-use after it (the probe closes it).
            throw new InvalidOperationException("The write nonce moved outside the cipher; open a new connection.");
        }

        var wire = RtspMessageCodec.EncodeRequest(request, _nextCSeq++, Identity);
        var payload = _sendCipher is { } cipher ? cipher.Encrypt(wire) : wire;
        await WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new IOException($"{request.Method} {request.Uri}: the receiver closed the connection ({await Closed.ConfigureAwait(false)}).");
    }

    public async Task<RtspResponse> SendExpectOkAsync(RtspRequest request, CancellationToken cancellationToken)
    {
        var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.IsSuccess ? response : throw new RtspStatusException(request, response);
    }

    public void EnableEncryption(HapChannelKeys keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (_sendCipher is not null)
        {
            throw new InvalidOperationException("Encryption is already enabled.");
        }

        _keys = keys;
        _reader.EnableDecryption(new HapFrameCipher(keys));
        _sendCipher = new HapFrameCipher(keys);
    }

    /// <summary>
    /// Sends <paramref name="request"/> padded (an <c>X-Probe-Padding</c> header) to exactly <paramref name="frameSize"/>
    /// bytes of plaintext as one HAP frame (2-byte length, ChaCha20-Poly1305 with the next write nonce, AAD = length,
    /// SPEC-control.md 2.5), then waits for the answer. Returns the answer, or null when the receiver closed the connection
    /// or did not answer within the request timeout (the reason is in the second item).
    /// </summary>
    public async Task<(RtspResponse? Response, string? Failure)> SendAsSingleFrameAsync(RtspRequest request, int frameSize, CancellationToken cancellationToken)
    {
        if (_sendCipher is null || _keys is null)
        {
            throw new InvalidOperationException("The oversized frame is sent inside the encrypted channel.");
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(frameSize, HapFrameCipher.MaxReceiveFramePlaintext);
        var plaintext = Pad(request, frameSize);

        // The library cipher has consumed nonces up to WriteCounter; continue the same counter for this one frame.
        var counter = _sendCipher.WriteCounter;
        var frame = new byte[2 + plaintext.Length + ChaCha20Poly1305Aead.TagSize];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)plaintext.Length);
        var nonce = new byte[ChaCha20Poly1305Aead.NonceSize];
        ChaCha20Poly1305Aead.NonceFromCounter(counter, nonce);
        ChaCha20Poly1305Aead.Encrypt(_keys.WriteKey, nonce, plaintext, frame.AsSpan(0, 2), frame.AsSpan(2, plaintext.Length), frame.AsSpan(2 + plaintext.Length, ChaCha20Poly1305Aead.TagSize));
        _nonceDiverged = true;
        _logger.LogInformation("P21: sending {Method} {Uri} as one {Bytes}-byte HAP frame (nonce {Counter})", request.Method, request.Uri, plaintext.Length, counter);

        try
        {
            await WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            var response = await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
            return response is null ? (null, await Closed.ConfigureAwait(false)) : (response, null);
        }
        catch (TimeoutException ex)
        {
            return (null, ex.Message);
        }
        catch (IOException ex)
        {
            return (null, ex.Message);
        }
    }

    public ValueTask DisposeAsync()
    {
        Close("disposed");
        return ValueTask.CompletedTask;
    }

    private byte[] Pad(RtspRequest request, int frameSize)
    {
        var cseq = _nextCSeq++;
        var bare = RtspMessageCodec.EncodeRequest(request, cseq, Identity);
        var overhead = PaddingHeader.Length + 4; // "Name: " + value + "\r\n" = name + 2 + value + 2
        var padding = frameSize - bare.Length - overhead;
        if (padding < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(frameSize), frameSize, $"The request needs at least {bare.Length + overhead + 1} bytes.");
        }

        var headers = new Dictionary<string, string>(request.Headers) { [PaddingHeader] = new string('x', padding) };
        var padded = RtspMessageCodec.EncodeRequest(request with { Headers = headers }, cseq, Identity);
        if (padded.Length != frameSize)
        {
            throw new InvalidOperationException($"Padding produced {padded.Length} bytes instead of {frameSize}.");
        }

        return padded;
    }

    private async Task WriteAsync(byte[] payload, CancellationToken cancellationToken)
    {
        if (_closed.Task.IsCompleted)
        {
            throw new IOException($"The connection to {RemoteEndPoint} is closed.");
        }

        await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RtspResponse?> ReadResponseAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        while (true)
        {
            _reader.SkipEmptyLines();
            if (_reader.PeekKind() == RtspMessageKind.Response && _reader.TryReadResponse(out var response))
            {
                return response;
            }

            int read;
            try
            {
                read = await _stream.ReadAsync(_chunk, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"No answer from {RemoteEndPoint} within {_requestTimeout.TotalSeconds:0.#} s.");
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                Close($"read failed: {ex.Message}");
                return null;
            }

            if (read == 0)
            {
                Close("receiver closed the connection");
                return null;
            }

            try
            {
                _reader.Append(_chunk.AsSpan(0, read));
            }
            catch (CryptographicException ex)
            {
                Close($"decryption failed: {ex.Message}");
                return null;
            }
        }
    }

    private void Close(string reason)
    {
        if (!_closed.TrySetResult(reason))
        {
            return;
        }

        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // already gone
        }

        _stream.Dispose();
    }
}
