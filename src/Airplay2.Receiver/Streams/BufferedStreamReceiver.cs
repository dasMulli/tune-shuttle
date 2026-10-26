using System.Net;
using Airplay2.Receiver.Codecs;
using Airplay2.Receiver.Playout;
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

    /// <summary>Label used in socket names and log messages.</summary>
    public string Name { get; init; } = "receiver";
}

/// <summary>
/// The receive path of a buffered (type 103) stream (SPEC-receiver.md 4.6, 5.2): a TCP data listener bound at the stream
/// SETUP (one connection accepted), 2-byte length-prefixed <see cref="BufferedBlock"/>s, decoder selection per block by
/// SSRC (AAC through <see cref="IAacDecoder"/> with a 7-byte ADTS header prepended, ALAC through <c>AlacDecoder</c>; an
/// unknown SSRC uses the stream SETUP's <c>audioFormat</c> and is logged once, RQ3), decrypted with <c>shk</c>, late blocks
/// (R6) dropped before decrypting, the first block after a start, flush or discontinuity decoded and muted, and the frames
/// handed to the <see cref="PlayoutBuffer"/>. The reader stops reading the socket when <c>AudioBufferSize</c> is full
/// (TCP flow control). The UDP control socket of the answer carries no traffic and is closed with the stream.
/// </summary>
/// <remarks>
/// Wave 0 scaffold: the surface compiles; the behaviour is the wave 2 <c>buffered-input</c> package (SPEC-receiver.md 13).
/// </remarks>
public sealed class BufferedStreamReceiver : IAsyncDisposable
{
    private readonly ILogger _logger;

    /// <summary>Creates the receiver; nothing is bound until <see cref="StartAsync"/>.</summary>
    /// <param name="buffer">Where decoded frames go (SPEC-receiver.md 5.3).</param>
    /// <param name="aacDecoder">The AAC decoder of this stream, or null in the ALAC-only variant (R3, R4).</param>
    /// <param name="options">Options; the defaults when null.</param>
    /// <param name="logger">Logger.</param>
    public BufferedStreamReceiver(PlayoutBuffer buffer, IAacDecoder? aacDecoder, BufferedStreamReceiverOptions? options = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Buffer = buffer;
        AacDecoder = aacDecoder;
        Options = options ?? new BufferedStreamReceiverOptions();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The playout buffer.</summary>
    public PlayoutBuffer Buffer { get; }

    /// <summary>The AAC decoder, or null.</summary>
    public IAacDecoder? AacDecoder { get; }

    /// <summary>The options.</summary>
    public BufferedStreamReceiverOptions Options { get; }

    /// <summary>The bound TCP data port after <see cref="StartAsync"/> (the <c>dataPort</c> of the answer, SPEC-receiver.md 4.6).</summary>
    public int DataPort => 0;

    /// <summary>The bound UDP control port after <see cref="StartAsync"/> (the <c>controlPort</c> of the answer; no traffic, SPEC-receiver.md 4.6).</summary>
    public int ControlPort => 0;

    /// <summary>
    /// Binds the data listener and the idle control socket for one stream (SPEC-receiver.md 4.6) and accepts the sender's
    /// data connection in the background.
    /// </summary>
    /// <param name="sharedKey">The stream's 32-byte <c>shk</c>.</param>
    /// <param name="audioFormat">The stream SETUP's <c>audioFormat</c>: the decoder for blocks with an unknown SSRC (RQ3).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public Task StartAsync(byte[] sharedKey, ulong audioFormat, CancellationToken cancellationToken) => throw new NotImplementedException("SPEC-receiver.md 5.2, wave 2");

    /// <summary>Closes the sockets (TEARDOWN of the stream, SPEC-receiver.md 4.9).</summary>
    public ValueTask DisposeAsync()
    {
        _logger.LogDebug("Buffered stream receiver {Name} disposed", Options.Name);
        return ValueTask.CompletedTask;
    }
}
