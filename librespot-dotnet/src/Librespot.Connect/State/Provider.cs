// Port of connect/src/state/provider.rs.
using ProvidedTrack = Spotify.Connectstate.ProvidedTrack;

namespace Librespot.Connect;

/// <summary>The provider of a track: where it was added from (upstream <c>Provider</c>).</summary>
internal enum Provider
{
    /// <summary>Provider used by spotify: <c>context</c>.</summary>
    Context,

    /// <summary>Provider used by spotify: <c>queue</c>.</summary>
    Queue,

    /// <summary>Provider used by spotify: <c>autoplay</c>.</summary>
    Autoplay,

    /// <summary>
    /// Custom provider <c>unavailable</c>: it seems like spotify just knows that the track isn't available, currently
    /// we don't have an option to do the same, so we stay with the old solution for now.
    /// </summary>
    Unavailable,
}

/// <summary>Provider helpers (upstream <c>Display for Provider</c> and trait <c>IsProvider</c>).</summary>
internal static class ProviderExtensions
{
    // providers used by spotify
    private const string ProviderContext = "context";
    private const string ProviderQueue = "queue";
    private const string ProviderAutoplay = "autoplay";

    // custom providers, used to identify certain states that we can't handle preemptively, yet
    private const string ProviderUnavailable = "unavailable";

    /// <summary>Upstream <c>Display</c>.</summary>
    public static string ToDisplayString(this Provider provider) => provider switch
    {
        Provider.Context => ProviderContext,
        Provider.Queue => ProviderQueue,
        Provider.Autoplay => ProviderAutoplay,
        Provider.Unavailable => ProviderUnavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>The track was added by autoplay.</summary>
    public static bool IsAutoplay(this ProvidedTrack track) => track.Provider == ProviderAutoplay;

    /// <summary>The track comes from the context.</summary>
    public static bool IsContext(this ProvidedTrack track) => track.Provider == ProviderContext;

    /// <summary>The track was queued.</summary>
    public static bool IsQueue(this ProvidedTrack track) => track.Provider == ProviderQueue;

    /// <summary>The track is marked as unavailable.</summary>
    public static bool IsUnavailable(this ProvidedTrack track) => track.Provider == ProviderUnavailable;

    /// <summary>Sets the provider string of the track.</summary>
    public static void SetProvider(this ProvidedTrack track, Provider provider) => track.Provider = provider.ToDisplayString();
}
