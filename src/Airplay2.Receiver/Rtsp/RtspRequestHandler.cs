using Airplay2.Sender.Rtsp;

namespace Airplay2.Receiver.Rtsp;

/// <summary>
/// Handles one request received by an <see cref="RtspServerConnection"/>: the receiver's request dispatch
/// (SPEC-receiver.md 4.1-4.9) plugs in here.
/// </summary>
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
