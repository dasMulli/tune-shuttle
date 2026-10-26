namespace Airplay2.Receiver;

/// <summary>The receiver's state as the facade reports it (SPEC-receiver.md 8.3, 9.1 <c>AirplayReceiverDto.State</c>).</summary>
public enum ReceiverStatus
{
    /// <summary>Not started, or stopped (SPEC-receiver.md 3.3 <c>enabled → false</c>).</summary>
    Off,

    /// <summary>Starting: binding the listeners and registering the services.</summary>
    Starting,

    /// <summary>Advertised and listening; no sender has a session (SPEC-receiver.md 3.1).</summary>
    Advertising,

    /// <summary>A sender completed a session SETUP but has no stream (SPEC-receiver.md 4.3).</summary>
    Connected,

    /// <summary>A stream is set up and the receiver owns the output (SPEC-receiver.md 4.6, R8).</summary>
    Streaming,

    /// <summary>Starting failed (for example the RTSP port is taken); the reason is logged.</summary>
    Error,
}
