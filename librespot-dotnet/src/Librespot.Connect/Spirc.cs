// Port of connect/src/spirc.rs (public contract). TODO(port: connect-spirc): SpircTask (the select! loop over
// dealer messages, player events, commands and the context resolver), all command handlers, transfer.
using Librespot.Core;
using Librespot.Playback;
using Librespot.Playback.Mixer;

namespace Librespot.Connect;

/// <summary>Upstream <c>SpircError</c>.</summary>
public sealed class SpircException : LibrespotException
{
    public enum Reason
    {
        NoData,
        UnknownContentType,
        NotAllowedContext,
        FailedDealerSetup,
        InvalidUri,
        TransferFailed,
    }

    public Reason Cause { get; }

    public SpircException(Reason cause, ErrorKind kind, string detail, Exception? inner = null) : base(kind, detail, inner)
    {
        Cause = cause;
    }
}

/// <summary>Handle to the running Spotify Connect task; every method sends a command to it.</summary>
public sealed class Spirc
{
    private Spirc()
    {
    }

    /// <summary>
    /// Upstream <c>Spirc::new(config, session, credentials, player, mixer)</c>: connects the session if needed,
    /// starts the dealer, and returns the handle plus the task to await (upstream returns <c>(Spirc, impl Future)</c>).
    /// </summary>
    public static Task<(Spirc Spirc, Task SpircTask)> CreateAsync(ConnectConfig config, Session session, Credentials credentials, Player player, IMixer mixer, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: connect-spirc)");

    public void Shutdown() => throw new NotImplementedException("TODO(port: connect-spirc)");
    public void Play() => throw new NotImplementedException("TODO(port: connect-spirc)");
    public void PlayPause() => throw new NotImplementedException("TODO(port: connect-spirc)");
    public void Pause() => throw new NotImplementedException("TODO(port: connect-spirc)");
    public void Prev() => throw new NotImplementedException("TODO(port: connect-spirc)");
    public void Next() => throw new NotImplementedException("TODO(port: connect-spirc)");
    public void VolumeUp() => throw new NotImplementedException("TODO(port: connect-spirc)");
    public void VolumeDown() => throw new NotImplementedException("TODO(port: connect-spirc)");
    public void Shuffle(bool shuffle) => throw new NotImplementedException("TODO(port: connect-spirc)");
    public void Repeat(bool repeat) => throw new NotImplementedException("TODO(port: connect-spirc)");
    public void RepeatTrack(bool repeat) => throw new NotImplementedException("TODO(port: connect-spirc)");
    public void SetVolume(ushort volume) => throw new NotImplementedException("TODO(port: connect-spirc)");
    public void SetPositionMs(uint positionMs) => throw new NotImplementedException("TODO(port: connect-spirc)");
    public void Load(LoadRequest command) => throw new NotImplementedException("TODO(port: connect-spirc)");
    public void AddToQueue(SpotifyUri uri) => throw new NotImplementedException("TODO(port: connect-spirc)");
    public void ClearQueue() => throw new NotImplementedException("TODO(port: connect-spirc)");
    public void Disconnect(bool pause) => throw new NotImplementedException("TODO(port: connect-spirc)");
    public void Activate() => throw new NotImplementedException("TODO(port: connect-spirc)");
    public void Transfer(TransferRequest? transferRequest) => throw new NotImplementedException("TODO(port: connect-spirc)");
}
