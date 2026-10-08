// Port of core/src/connection/mod.rs.
using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Google.Protobuf;
using Librespot.Core.Logging;
using Librespot.Protocol.Authentication;
using Librespot.Protocol.Keyexchange;
using Microsoft.Extensions.Logging;

namespace Librespot.Core.Connection;

/// <summary>Upstream <c>connection::AuthenticationError</c>.</summary>
public sealed class ApAuthenticationException : LibrespotException
{
    /// <summary>The upstream variants.</summary>
    public enum Reason
    {
        /// <summary>The AP rejected the login; <see cref="ErrorCode"/> says why.</summary>
        LoginFailed,
        /// <summary>Unexpected packet during authentication.</summary>
        Packet,
        /// <summary>Transport returned no data.</summary>
        Transport,
    }

    /// <summary>Which variant this is.</summary>
    public Reason Cause { get; }

    /// <summary>Set for <see cref="Reason.LoginFailed"/>.</summary>
    public ErrorCode? ErrorCode { get; }

    private ApAuthenticationException(Reason cause, ErrorKind kind, string detail, ErrorCode? errorCode = null)
        : base(kind, detail)
    {
        Cause = cause;
        ErrorCode = errorCode;
    }

    /// <summary>Upstream <c>AuthenticationError::LoginFailed(code)</c> (kind PermissionDenied).</summary>
    public static ApAuthenticationException LoginFailed(ErrorCode code) =>
        new(Reason.LoginFailed, ErrorKind.PermissionDenied, $"Login failed with reason: {LoginErrorMessage(code)}", code);

    /// <summary>Upstream <c>AuthenticationError::Packet(cmd)</c> (kind Unimplemented).</summary>
    public static ApAuthenticationException Packet(byte cmd) =>
        new(Reason.Packet, ErrorKind.Unimplemented, $"invalid packet {cmd}");

    /// <summary>Upstream <c>AuthenticationError::Transport</c> (kind Unavailable).</summary>
    public static ApAuthenticationException Transport() =>
        new(Reason.Transport, ErrorKind.Unavailable, "transport returned no data");

    /// <summary>Upstream <c>login_error_message(code)</c>.</summary>
    public static string LoginErrorMessage(ErrorCode code) => code switch
    {
        Librespot.Protocol.Keyexchange.ErrorCode.ProtocolError => "Protocol error",
        Librespot.Protocol.Keyexchange.ErrorCode.TryAnotherAp => "Try another access point",
        Librespot.Protocol.Keyexchange.ErrorCode.BadConnectionId => "Bad connection ID",
        Librespot.Protocol.Keyexchange.ErrorCode.TravelRestriction => "Travel restriction",
        Librespot.Protocol.Keyexchange.ErrorCode.PremiumAccountRequired => "Premium account required",
        Librespot.Protocol.Keyexchange.ErrorCode.BadCredentials => "Bad credentials",
        Librespot.Protocol.Keyexchange.ErrorCode.CouldNotValidateCredentials => "Could not validate credentials",
        Librespot.Protocol.Keyexchange.ErrorCode.AccountExists => "Account exists",
        Librespot.Protocol.Keyexchange.ErrorCode.ExtraVerificationRequired => "Extra verification required",
        Librespot.Protocol.Keyexchange.ErrorCode.InvalidAppKey => "Invalid app key",
        Librespot.Protocol.Keyexchange.ErrorCode.ApplicationBanned => "Application banned",
        _ => code.ToString(),
    };
}

/// <summary>
/// The encrypted AP connection (upstream <c>Transport = Framed&lt;TcpStream, ApCodec&gt;</c>): a duplex stream plus an
/// <see cref="ApCodec"/>, exposing a packet send side and a packet receive side.
/// </summary>
/// <remarks>
/// <see cref="SendAsync(PacketType, ReadOnlyMemory{byte}, CancellationToken)"/> may be called concurrently (calls are
/// serialized); <see cref="ReceiveAsync"/> must be called by one reader at a time. Sending and receiving may overlap.
/// <para>
/// Deviation from upstream: a Rust <c>Framed</c> can only be dropped once nobody polls it, but here
/// <see cref="DisposeAsync"/> may run while a send or receive is in flight (the session's shutdown). Disposing closes
/// the connection first, so a pending <see cref="ReceiveAsync"/> faults (with an <see cref="IOException"/> or
/// <see cref="ObjectDisposedException"/>) and a pending send fails the same way; the receive buffers are released only
/// once no receive is running. Owners should still cancel (or let fault) their receive loop through disposal and
/// not start new calls afterwards: those throw <see cref="ObjectDisposedException"/>.
/// </para>
/// </remarks>
public sealed class Transport : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly IDisposable? _owner;
    private readonly PipeReader _reader;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ArrayBufferWriter<byte> _sendBuffer = new();
    private int _disposed;
    private int _receiving;
    private int _readerCompleted;

    internal Transport(TcpClient client, ApCodec codec)
        : this(client.GetStream(), codec, client)
    {
    }

    /// <summary>Wraps any duplex <paramref name="stream"/> (tests use loopback streams).</summary>
    /// <param name="stream">The connection, positioned right after the handshake.</param>
    /// <param name="codec">The keyed codec.</param>
    /// <param name="owner">Disposed together with the transport (the <see cref="TcpClient"/>), if any.</param>
    internal Transport(Stream stream, ApCodec codec, IDisposable? owner = null)
    {
        _stream = stream;
        _owner = owner;
        Codec = codec;
        _reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
    }

    internal ApCodec Codec { get; }

    /// <summary>Encrypts and sends one packet (upstream <c>SinkExt::send</c>: encode + flush).</summary>
    public ValueTask SendAsync(PacketType cmd, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
        SendAsync((byte)cmd, payload, cancellationToken);

    /// <summary>Encrypts and sends one packet with a raw command byte.</summary>
    public async ValueTask SendAsync(byte cmd, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            // Encoding advances the send nonce, so it must happen in send order, inside the lock.
            _sendBuffer.ResetWrittenCount();
            Codec.Encode(cmd, payload.Span, _sendBuffer);
            await _stream.WriteAsync(_sendBuffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Receives and decrypts the next packet, or null when the peer closed the connection (upstream
    /// <c>StreamExt::next</c> returning <c>None</c>).
    /// </summary>
    /// <exception cref="InvalidDataException">A packet failed the MAC check.</exception>
    /// <exception cref="IOException">The connection failed, or closed in the middle of a packet.</exception>
    /// <exception cref="ObjectDisposedException">The transport was disposed.</exception>
    /// <exception cref="InvalidOperationException">Another receive is already in progress.</exception>
    public async ValueTask<ApPacket?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _receiving, 1, 0) != 0)
        {
            throw new InvalidOperationException("Another receive is already in progress.");
        }

        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return await ReceiveCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Full fence (pairs with the one in DisposeAsync): if a dispose raced with this receive, at least one of the
            // two sees the other and completes the reader, and only once no read is in flight.
            Interlocked.Exchange(ref _receiving, 0);
            if (Volatile.Read(ref _disposed) != 0)
            {
                CompleteReader();
            }
        }
    }

    private async ValueTask<ApPacket?> ReceiveCoreAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (result.IsCanceled)
            {
                // Only CancelPendingRead produces this; treat it like a cancelled token instead of reading again.
                _reader.AdvanceTo(buffer.Start, buffer.End);
                throw new OperationCanceledException();
            }

            bool decoded;
            ApPacket packet;
            try
            {
                decoded = Codec.TryDecode(ref buffer, out packet);
            }
            catch
            {
                _reader.AdvanceTo(buffer.Start, buffer.End);
                throw;
            }

            if (decoded)
            {
                _reader.AdvanceTo(buffer.Start);
                return packet;
            }

            if (result.IsCompleted)
            {
                var remaining = buffer.Length;
                _reader.AdvanceTo(buffer.End);

                // tokio_util's default `decode_eof`: a clean end only when no bytes are left over.
                if (remaining == 0)
                {
                    return null;
                }

                throw new IOException("bytes remaining on stream");
            }

            // TryDecode may have consumed the header; everything else has been examined and needs more data.
            _reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>Closes the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Close the connection first so a pending read or write faults and unwinds. The pipe reader's pooled buffers
        // are released only when no receive is in flight; otherwise the receiver releases them on its way out (an
        // in-flight stream read may still be writing into them). The send lock is deliberately not disposed: a sender
        // waiting on it would get ObjectDisposedException from the lock instead of from the closed stream, and it
        // holds no OS handle.
        await _stream.DisposeAsync().ConfigureAwait(false);
        _owner?.Dispose();

        if (Volatile.Read(ref _receiving) == 0)
        {
            CompleteReader();
        }
    }

    private void CompleteReader()
    {
        if (Interlocked.Exchange(ref _readerCompleted, 1) == 0)
        {
            _reader.Complete();
        }
    }
}

/// <summary>Free functions of upstream <c>connection</c>: connect (with optional proxy tunnel) and authenticate.</summary>
public static class ApConnection
{
    /// <summary>Upstream's <c>TIMEOUT</c> for the AP handshake.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static readonly ILogger Log = LibrespotLog.CreateLogger("Librespot.Core.Connection");

    /// <summary>The process architecture in Rust's <c>std::env::consts::ARCH</c> vocabulary.</summary>
    internal static string RustArch { get; } = RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X86 => "x86",
        Architecture.X64 => "x86_64",
        Architecture.Arm or Architecture.Armv6 => "arm",
        Architecture.Arm64 => "aarch64",
        Architecture.Ppc64le => "powerpc64",
        Architecture.S390x => "s390x",
        Architecture.LoongArch64 => "loongarch64",
        Architecture.RiscV64 => "riscv64",
        Architecture.Wasm => "wasm32",
        var other => other.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Upstream <c>connect(host, port, proxy)</c>: TCP connect (through the proxy, if any) and handshake.
    /// </summary>
    /// <remarks>
    /// Like upstream, the 5 second <see cref="Timeout"/> covers the handshake only: upstream evaluates
    /// <c>socket::connect(..).await?</c> while building the future it passes to <c>tokio::time::timeout</c>.
    /// </remarks>
    /// <exception cref="TimeoutException">The handshake took longer than <see cref="Timeout"/>.</exception>
    public static async Task<Transport> ConnectAsync(string host, ushort port, Uri? proxy, CancellationToken cancellationToken = default)
    {
        var socket = await ApSocket.ConnectAsync(host, port, proxy, cancellationToken).ConfigureAwait(false);
        Log.LogDebug("Connection to AP established.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Timeout);
        try
        {
            return await Handshake.PerformAsync(socket, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new TimeoutException("deadline has elapsed");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Upstream <c>connect_with_retry(host, port, proxy, max_retries)</c>: <see cref="ConnectAsync"/>, retried up to
    /// <paramref name="maxRetries"/> more times. Cancellation is not retried.
    /// </summary>
    public static async Task<Transport> ConnectWithRetryAsync(string host, ushort port, Uri? proxy, byte maxRetries, CancellationToken cancellationToken = default)
    {
        var numRetries = 0;
        while (true)
        {
            try
            {
                return await ConnectAsync(host, port, proxy, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (!(e is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                Log.LogDebug("Connection to \"{Host}:{Port}\" failed: {Error}", host, port, e.Message);
                if (numRetries < maxRetries)
                {
                    numRetries++;
                    Log.LogDebug("Retry access point...");
                    continue;
                }

                throw;
            }
        }
    }

    /// <summary>Upstream's <c>std::env::consts::ARCH</c> to <c>CpuFamily</c> mapping in <c>authenticate</c>.</summary>
    internal static CpuFamily CpuFamilyFor(string arch) => arch switch
    {
        "blackfin" => CpuFamily.CpuBlackfin,
        "arm" or "aarch64" => CpuFamily.CpuArm,
        "ia64" => CpuFamily.CpuIa64,
        "mips" => CpuFamily.CpuMips,
        "ppc" => CpuFamily.CpuPpc,
        "ppc64" => CpuFamily.CpuPpc64,
        "sh" => CpuFamily.CpuSh,
        "x86" => CpuFamily.CpuX86,
        "x86_64" => CpuFamily.CpuX8664,
        _ => CpuFamily.CpuUnknown,
    };

    /// <summary>Upstream's <c>config::OS</c> to <c>Os</c> mapping in <c>authenticate</c>.</summary>
    internal static Os OsFor(string os) => os switch
    {
        "android" => Os.Android,
        "freebsd" or "netbsd" or "openbsd" => Os.Freebsd,
        "ios" => Os.Iphone,
        "linux" => Os.Linux,
        "macos" => Os.Osx,
        "windows" => Os.Windows,
        _ => Os.Unknown,
    };

    /// <summary>Builds the upstream <c>ClientResponseEncrypted</c> login packet.</summary>
    internal static ClientResponseEncrypted BuildLoginPacket(Credentials credentials, string deviceId)
    {
        var packet = new ClientResponseEncrypted
        {
            LoginCredentials = new LoginCredentials
            {
                Typ = credentials.AuthType,
                AuthData = ByteString.CopyFrom(credentials.AuthData),
            },
            SystemInfo = new SystemInfo
            {
                CpuFamily = CpuFamilyFor(RustArch),
                Os = OsFor(Platform.Os),
                SystemInformationString = $"librespot-{LibrespotVersion.ShaShort}-{LibrespotVersion.BuildId}",
                DeviceId = deviceId,
            },
            VersionString = $"librespot {LibrespotVersion.SemVer}",
        };

        if (credentials.Username is { } username)
        {
            packet.LoginCredentials.Username = username;
        }

        return packet;
    }

    /// <summary>
    /// Upstream <c>authenticate(transport, credentials, device_id)</c>: sends ClientResponseEncrypted, awaits
    /// APWelcome/AuthFailure and returns the reusable (stored-credentials) <see cref="Credentials"/>.
    /// </summary>
    /// <exception cref="ApAuthenticationException">The AP refused the login, sent an unexpected packet, or closed the connection.</exception>
    /// <exception cref="LibrespotException">Transport or protobuf failure.</exception>
    public static async Task<Credentials> AuthenticateAsync(Transport transport, Credentials credentials, string deviceId, CancellationToken cancellationToken = default)
    {
        var packet = BuildLoginPacket(credentials, deviceId);
        var data = packet.ToByteArray();

        Log.LogDebug("Authenticating with AP using {AuthType}", credentials.AuthType);

        ApPacket? received;
        try
        {
            await transport.SendAsync(PacketType.Login, data, cancellationToken).ConfigureAwait(false);
            received = await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not LibrespotException && !(e is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            throw LibrespotException.From(e);
        }

        if (received is not { } response)
        {
            throw ApAuthenticationException.Transport();
        }

        try
        {
            switch (PacketTypeExtensions.FromByte(response.Cmd))
            {
                case PacketType.ApWelcome:
                {
                    var welcomeData = ProtobufUtil.ParseRequired(APWelcome.Parser, response.Payload.Span);

                    return new Credentials
                    {
                        Username = welcomeData.CanonicalUsername,
                        AuthType = welcomeData.ReusableAuthCredentialsType,
                        AuthData = welcomeData.ReusableAuthCredentials.ToByteArray(),
                    };
                }

                case PacketType.AuthFailure:
                {
                    var errorData = ProtobufUtil.ParseRequired(APLoginFailed.Parser, response.Payload.Span);
                    throw ApAuthenticationException.LoginFailed(errorData.ErrorCode);
                }

                default:
                    Log.LogTrace(
                        "Did not expect {Cmd} AES key packet with data {Data}",
                        response.Cmd,
                        Convert.ToHexString(response.Payload.Span));
                    throw ApAuthenticationException.Packet(response.Cmd);
            }
        }
        catch (Exception e) when (e is InvalidProtocolBufferException or InvalidDataException)
        {
            throw LibrespotException.From(e);
        }
    }
}

/// <summary>Parsing helpers matching rust-protobuf semantics.</summary>
internal static class ProtobufUtil
{
    /// <summary>
    /// Parses <paramref name="data"/> and, like rust-protobuf's <c>parse_from_bytes</c>, rejects a message whose proto2
    /// <c>required</c> fields are missing (Google.Protobuf does not check this on parse).
    /// </summary>
    /// <exception cref="InvalidDataException">A required field is missing.</exception>
    public static T ParseRequired<T>(MessageParser<T> parser, ReadOnlySpan<byte> data)
        where T : IMessage<T>
    {
        var message = parser.ParseFrom(data);
        if (!message.IsInitialized())
        {
            throw new InvalidDataException($"Message `{message.Descriptor.Name}` is missing required fields");
        }

        return message;
    }
}
