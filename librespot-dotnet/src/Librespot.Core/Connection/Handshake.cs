// Port of core/src/connection/handshake.rs.
// TODO(port: core-connection): ClientHello / APResponseMessage exchange, RSA-SHA1 verification of the
// server's DH public key against the embedded Spotify public key, HMAC-SHA1 key derivation into the
// Shannon send/recv keys, ClientResponsePlaintext. Port the #[cfg(test)] tests in that file too.
using System.Net.Sockets;

namespace Librespot.Core.Connection;

public static class Handshake
{
    /// <summary>
    /// Performs the key exchange on a freshly connected socket and returns the transport whose
    /// <see cref="ApCodec"/> is keyed with the derived send/recv keys.
    /// </summary>
    public static Task<Transport> PerformAsync(TcpClient client, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: core-connection)");
}
