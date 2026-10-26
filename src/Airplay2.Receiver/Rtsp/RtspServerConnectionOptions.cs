using Airplay2.Sender.Crypto;

namespace Airplay2.Receiver.Rtsp;

/// <summary>Options of an <see cref="RtspServerConnection"/> (SPEC-receiver.md 4.1, 4.4).</summary>
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
    public string Name { get; init; } = "receiver";
}
