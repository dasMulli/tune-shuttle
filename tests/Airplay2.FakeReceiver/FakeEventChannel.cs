using System.Net;
using System.Net.Sockets;
using Airplay2.Sender.Crypto;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtsp;
using Microsoft.Extensions.Logging;

namespace Airplay2.FakeReceiver;

/// <summary>
/// The receiver side of the event channel of one session (SPEC-control.md 4.3): a TCP listener opened at the session
/// SETUP (before the answer, as shairport does [sps:rtsp.c:2626-2638]); the connection the sender makes to it is
/// HAP-encrypted from its first byte with the Events keys in the receiver's view, and carries RTSP-shaped
/// <c>POST /command</c> requests from the receiver to the sender. An unanswered request closes the channel after
/// <see cref="FakeReceiverOptions.EventAnswerTimeout"/> [protocols.md:1786-1795]. A reconnect replaces the previous
/// connection.
/// </summary>
internal sealed class FakeEventChannel : IAsyncDisposable
{
    /// <summary>Plist size below which <see cref="FakeReceiverOptions.LargeEventFrames"/> pads the <c>updateInfo</c> body, so the one frame is surely above 1024 bytes.</summary>
    private const int LargeFrameMinimumBody = 1400;

    private readonly FakeReceiver _owner;
    private readonly HapChannelKeys? _keys;
    private readonly int _sessionId;
    private Socket? _listener;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private RtspServerConnection? _connection;
    private bool _updateInfoAfterRecord;
    private Task _acceptLoop = Task.CompletedTask;
    private int _disposed;

    /// <param name="owner">The receiver.</param>
    /// <param name="keys">The Events keys in the receiver's view (<c>HapAccessoryPairing.EventKeysFor</c>), or null for a plaintext channel (unpaired connection with <see cref="FakeReceiverOptions.RequirePairing"/> off).</param>
    /// <param name="sessionId">The control connection number, for logs.</param>
    public FakeEventChannel(FakeReceiver owner, HapChannelKeys? keys, int sessionId)
    {
        _owner = owner;
        _keys = keys;
        _sessionId = sessionId;
    }

    /// <summary>The listening port announced as <c>eventPort</c>.</summary>
    public int Port { get; private set; }

    /// <summary>True while a sender connection is open.</summary>
    public bool IsConnected
    {
        get
        {
            lock (_lock)
            {
                return _connection is { Closed.IsCompleted: false };
            }
        }
    }

    /// <summary>Binds and starts accepting.</summary>
    public void Start()
    {
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(4);
            Port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        }
        catch
        {
            listener.Dispose();
            throw;
        }

        _listener = listener;
        _acceptLoop = AcceptLoopAsync(listener, _stop.Token);
        _owner.Track(_acceptLoop, $"event accept loop (session #{_sessionId})");
    }

    /// <summary>
    /// RECORD arrived and <see cref="FakeReceiverOptions.UpdateInfoOnConnect"/> is off: send <c>updateInfo</c> now, or as
    /// soon as the sender connects.
    /// </summary>
    public void UpdateInfoAfterRecord()
    {
        bool connected;
        lock (_lock)
        {
            // Checked and flagged in one critical section, so a connect racing RECORD either sees the flag or is seen here.
            connected = _connection is { Closed.IsCompleted: false };
            _updateInfoAfterRecord = !connected;
        }

        if (connected)
        {
            _owner.Track(SendUpdateInfoQuietlyAsync(), "updateInfo after RECORD");
        }
    }

    /// <summary>
    /// Sends <c>{type: updateInfo, value: &lt;the /info dict plus txtAirPlay&gt;}</c> [sps:ap2_event_message_handler.c:109-139].
    /// <c>initialVolume</c> is included when the receiver reports one (or <paramref name="forceVolume"/>).
    /// </summary>
    public Task<RtspResponse> SendUpdateInfoAsync(bool forceVolume, CancellationToken cancellationToken)
    {
        var value = _owner.BuildInfo(includeTxt: true, includeVolume: forceVolume || _owner.Options.InitialVolume is not null);
        var body = new PlistDictionary { ["type"] = "updateInfo", ["value"] = value };
        return SendCommandAsync(body, _owner.Options.LargeEventFrames, cancellationToken);
    }

    /// <summary>
    /// Sends <c>POST /command</c> with a bplist body (Content-Length, Content-Type, no CSeq unless
    /// <see cref="FakeReceiverOptions.EventRequestsCarryCSeq"/> [sps:ap2_event_message_handler.c:88-95]) and waits for the
    /// answer. With <paramref name="large"/> the message goes out as one HAP frame (SPEC-control.md 2.5).
    /// </summary>
    /// <exception cref="InvalidOperationException">No sender is connected.</exception>
    /// <exception cref="TimeoutException">No answer within the event timeout; the channel has been closed.</exception>
    public async Task<RtspResponse> SendCommandAsync(PlistDictionary body, bool large, CancellationToken cancellationToken)
    {
        RtspServerConnection connection;
        lock (_lock)
        {
            connection = _connection is { Closed.IsCompleted: false } open ? open : throw new InvalidOperationException("No event channel is connected.");
        }

        var type = body.GetString("type") ?? "(none)";
        var encoded = BinaryPlist.Encode(body);
        if (large && encoded.Length < LargeFrameMinimumBody)
        {
            var padded = new PlistDictionary(body) { ["fakeReceiverPadding"] = new byte[LargeFrameMinimumBody - encoded.Length] };
            encoded = BinaryPlist.Encode(padded);
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (large)
            {
                connection.MaxSendFramePlaintext = HapFrameCipher.MaxReceiveFramePlaintext;
            }

            try
            {
                var response = await connection.SendRequestAsync("POST", "/command", RtspContentTypes.BinaryPlist, encoded, null, cancellationToken).ConfigureAwait(false);
                _owner.Write(new FakeReceiverEvent.EventCommandAnswered(type, response.StatusCode, large));
                return response;
            }
            catch (TimeoutException)
            {
                _owner.Logger.LogWarning(
                    "Fake receiver {Name}: event command {Type} not answered within {Timeout}; closing the event channel",
                    _owner.Options.Name,
                    type,
                    _owner.Options.EventAnswerTimeout);
                _owner.Write(new FakeReceiverEvent.EventCommandAnswered(type, 0, large));
                await connection.CloseAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                if (large && !connection.Closed.IsCompleted)
                {
                    connection.MaxSendFramePlaintext = HapFrameCipher.MaxSendFramePlaintext;
                }
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        _listener?.Dispose();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _owner.Logger.LogDebug(ex, "Fake receiver {Name}: event accept loop ended with an error", _owner.Options.Name);
        }

        RtspServerConnection? connection;
        lock (_lock)
        {
            connection = _connection;
        }

        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(Socket listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptAsync(stop).ConfigureAwait(false);
            }
            catch (Exception ex) when ((ex is OperationCanceledException or ObjectDisposedException or SocketException) && stop.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
            {
                break;
            }

            var options = new RtspServerConnectionOptions
            {
                ServerHeader = _owner.Options.ServerHeader,
                Name = $"{_owner.Options.Name} events #{_sessionId}",
                RequestTimeout = _owner.Options.EventAnswerTimeout,
                SendCSeqOnRequests = _owner.Options.EventRequestsCarryCSeq,
            };
            var connection = RtspServerConnection.Start(socket, HandleAsync, options, _owner.Logger);
            if (_keys is not null)
            {
                // Encrypted from the first byte (SPEC-control.md 4.3); nothing has been sent or received yet.
                connection.EnableEncryption(_keys);
            }

            RtspServerConnection? previous;
            bool sendUpdateInfo;
            lock (_lock)
            {
                previous = _connection;
                _connection = connection;
                sendUpdateInfo = _owner.Options.UpdateInfoOnConnect || _updateInfoAfterRecord;
                _updateInfoAfterRecord = false;
            }

            if (previous is not null)
            {
                _owner.Logger.LogDebug("Fake receiver {Name}: event channel reconnected; closing the previous connection", _owner.Options.Name);
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            _owner.SetLatestEventChannel(this);
            _owner.Write(new FakeReceiverEvent.EventChannelConnected(connection.RemoteEndPoint));
            _owner.Track(MonitorAsync(connection), $"event channel monitor (session #{_sessionId})");
            if (sendUpdateInfo)
            {
                // shairport sends it right after accepting [sps:ap2_event_receiver.c:219].
                _owner.Track(SendUpdateInfoQuietlyAsync(), "updateInfo on connect");
            }
        }
    }

    private async Task MonitorAsync(RtspServerConnection connection)
    {
        var reason = await connection.Closed.ConfigureAwait(false);
        _owner.Write(new FakeReceiverEvent.EventChannelClosed(reason));
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private async Task SendUpdateInfoQuietlyAsync()
    {
        try
        {
            await SendUpdateInfoAsync(forceVolume: false, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or InvalidOperationException or ObjectDisposedException)
        {
            _owner.Logger.LogDebug("Fake receiver {Name}: updateInfo not delivered: {Error}", _owner.Options.Name, ex.Message);
        }
    }

    /// <summary>The sender sends no requests on the event channel; anything that arrives is logged and answered 501.</summary>
    private ValueTask<RtspServerResponse> HandleAsync(RtspServerConnection connection, RtspIncomingRequest request, CancellationToken cancellationToken)
    {
        _owner.Logger.LogWarning("Fake receiver {Name}: unexpected request {Method} {Uri} on the event channel", _owner.Options.Name, request.Method, request.Uri);
        return ValueTask.FromResult(RtspServerResponse.Status(RtspStatus.NotImplemented));
    }
}
