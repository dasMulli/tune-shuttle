// Port of core/src/connection/mod.rs (+ handshake.rs, proxytunnel.rs, socket.rs live next to this file).
// TODO(port: core-connection): everything below except the exception type.
using System.Net.Sockets;
using Librespot.Protocol.Keyexchange;

namespace Librespot.Core.Connection;

/// <summary>Upstream <c>connection::AuthenticationError</c>.</summary>
public sealed class ApAuthenticationException : LibrespotException
{
    public enum Reason
    {
        /// <summary>The AP rejected the login; <see cref="ErrorCode"/> says why.</summary>
        LoginFailed,
        /// <summary>Unexpected packet during authentication.</summary>
        Packet,
        /// <summary>Transport returned no data.</summary>
        Transport,
    }

    public Reason Cause { get; }

    /// <summary>Set for <see cref="Reason.LoginFailed"/>.</summary>
    public ErrorCode? ErrorCode { get; }

    private ApAuthenticationException(Reason cause, ErrorKind kind, string detail, ErrorCode? errorCode = null)
        : base(kind, detail)
    {
        Cause = cause;
        ErrorCode = errorCode;
    }

    public static ApAuthenticationException LoginFailed(ErrorCode code) =>
        new(Reason.LoginFailed, ErrorKind.PermissionDenied, $"Login failed with reason: {LoginErrorMessage(code)}", code);

    public static ApAuthenticationException Packet(byte cmd) =>
        new(Reason.Packet, ErrorKind.Unimplemented, $"invalid packet {cmd}");

    public static ApAuthenticationException Transport() =>
        new(Reason.Transport, ErrorKind.Unavailable, "transport returned no data");

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
/// The encrypted AP connection (upstream <c>Transport = Framed&lt;TcpStream, ApCodec&gt;</c>): a TCP stream plus an
/// <see cref="ApCodec"/>, exposing a packet send side and a packet receive side.
/// </summary>
public sealed class Transport : IAsyncDisposable
{
    internal Transport(TcpClient client, ApCodec codec)
    {
        Client = client;
        Codec = codec;
    }

    internal TcpClient Client { get; }
    internal ApCodec Codec { get; }

    /// <summary>Encrypts and sends one packet (upstream <c>SinkExt::send</c>).</summary>
    public ValueTask SendAsync(PacketType cmd, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: core-connection)");

    /// <summary>Receives and decrypts the next packet, or null when the peer closed the connection.</summary>
    public ValueTask<ApPacket?> ReceiveAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: core-connection)");

    public ValueTask DisposeAsync()
    {
        Client.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Free functions of upstream <c>connection</c>: connect (with optional proxy tunnel) and authenticate.</summary>
public static class ApConnection
{
    /// <summary>Upstream <c>connect_with_retry(host, port, proxy, max_retries)</c>: TCP connect + handshake.</summary>
    public static Task<Transport> ConnectWithRetryAsync(string host, ushort port, Uri? proxy, byte maxRetries, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: core-connection)");

    /// <summary>Upstream <c>connect(host, port, proxy)</c>.</summary>
    public static Task<Transport> ConnectAsync(string host, ushort port, Uri? proxy, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: core-connection)");

    /// <summary>
    /// Upstream <c>authenticate(transport, credentials, device_id)</c>: sends ClientResponseEncrypted, awaits
    /// APWelcome/AuthFailure and returns the reusable (stored-credentials) <see cref="Credentials"/>.
    /// </summary>
    public static Task<Credentials> AuthenticateAsync(Transport transport, Credentials credentials, string deviceId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: core-connection)");
}
