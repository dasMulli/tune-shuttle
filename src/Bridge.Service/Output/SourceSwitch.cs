using Airplay2.Receiver;

namespace Bridge.Service.Output;

/// <summary>Who owns the output (SPEC-receiver.md 8.2 <c>Owner</c>; 9.1 <c>StatusDto.ActiveSource</c>, <c>PlaybackDto.Source</c>).</summary>
public enum AudioSource
{
    /// <summary>Unowned: the default at start, and after a sender left (R8).</summary>
    None,

    /// <summary>Spotify Connect through librespot and the <see cref="AirplaySink"/>.</summary>
    Spotify,

    /// <summary>The AirPlay receiver (<c>AirplaySource</c>).</summary>
    Airplay,
}

/// <summary>
/// The "newest source wins" switch in front of the output (SPEC-receiver.md R8, 8.2): every call a source makes on the one
/// <c>IGroupPlayer</c> is gated by ownership. <see cref="MayWrite"/> is the per-write gate both sinks check; the player
/// commands go through <see cref="OutputManager"/>, which drops them for a non-owner and remembers a dropped <c>Start</c>.
/// <see cref="ClaimAsync"/> runs the eviction to completion (Spotify paused through Spirc and the sink's pause awaited,
/// or <c>AirplaySource.EndSessionAsync</c> awaited) before it grants and publishes <c>statusChanged</c>; the playback view
/// belongs to the owner.
/// </summary>
/// <remarks>
/// Wave 0 scaffold: the type and its registration exist so the wave 3 packages compile against it; the behaviour is the
/// wave 3 <c>source-switch</c> package (SPEC-receiver.md 13).
/// </remarks>
public sealed class SourceSwitch
{
    /// <summary>The current owner (SPEC-receiver.md 8.2); <see cref="AudioSource.None"/> at start.</summary>
    public AudioSource Owner { get; private set; } = AudioSource.None;

    /// <summary>The sender while <see cref="Owner"/> is <see cref="AudioSource.Airplay"/>, else null.</summary>
    public SenderInfo? Sender { get; private set; }

    /// <summary>The API name of a source (SPEC-receiver.md 9.1): <c>spotify</c>, <c>airplay</c>, or null for <see cref="AudioSource.None"/>.</summary>
    public static string? ToApiName(AudioSource source) => source switch
    {
        AudioSource.Spotify => OutputManager.SourceSpotify,
        AudioSource.Airplay => OutputManager.SourceAirplay,
        _ => null,
    };

    /// <summary>The per-write gate (SPEC-receiver.md 8.2): true while <paramref name="source"/> owns the output; frames of a non-owner are counted and dropped.</summary>
    public bool MayWrite(AudioSource source) => throw new NotImplementedException("SPEC-receiver.md 8.2, wave 3");

    /// <summary>
    /// Claims the output for <paramref name="source"/> (SPEC-receiver.md 8.2): evicts the current owner to completion (at
    /// most 2 s for Spotify's pause), then sets the owner, replays a remembered <c>Start</c> and publishes <c>statusChanged</c>.
    /// </summary>
    public Task ClaimAsync(AudioSource source, SenderInfo? sender, CancellationToken cancellationToken) => throw new NotImplementedException("SPEC-receiver.md 8.2, wave 3");

    /// <summary>Sets <see cref="AudioSource.None"/> when <paramref name="source"/> owns the output (SPEC-receiver.md 8.2: a sender left, Spotify stopped).</summary>
    public void Release(AudioSource source) => throw new NotImplementedException("SPEC-receiver.md 8.2, wave 3");
}
