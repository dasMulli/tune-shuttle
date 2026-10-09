// Port of connect/src/state/metadata.rs.
//
// Upstream defines a `Metadata` trait (implemented for ContextTrack, ProvidedTrack, Context and StateContext via
// `impl_metadata!`, which only returns `&self.metadata`). The generated protobuf classes live in another assembly,
// so they cannot implement an interface here; instead the trait methods are extension methods on the metadata map
// itself and callers write `track.Metadata.IsFromQueue()` where upstream writes `track.is_from_queue()`.
// Values are written with Rust's `Display` formatting (`true`/`false`, invariant integers).
using System.Globalization;

namespace Librespot.Connect;

/// <summary>Allows easy access of known metadata fields (upstream trait <c>Metadata</c>).</summary>
internal static class MetadataExtensions
{
    private const string ContextUri = "context_uri";
    private const string EntityUri = "entity_uri";
    private const string IsQueued = "is_queued";
    private const string IsAutoplay = "autoplay.is_autoplay";
    private const string Hidden = "hidden";
    private const string Iteration = "iteration";

    private const string CustomContextIndex = "context_index";
    private const string CustomShuffleSeed = "shuffle_seed";
    private const string CustomInitialTrack = "initial_track";

    /// <summary>True when the entry exists and equals <c>"true"</c>.</summary>
    public static bool GetBool(this IDictionary<string, string> metadata, string entry) =>
        metadata.TryGetValue(entry, out var value) && value == "true";

    /// <summary>The entry parsed as an unsigned integer (upstream <c>usize</c>), or null.</summary>
    public static int? GetUsize(this IDictionary<string, string> metadata, string entry) =>
        metadata.TryGetValue(entry, out var value)
        && int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
        && parsed >= 0
            ? parsed
            : null;

    /// <summary>The raw entry, or null.</summary>
    public static string? Get(this IDictionary<string, string> metadata, string entry) =>
        metadata.TryGetValue(entry, out var value) ? value : null;

    private static void Set(IDictionary<string, string> metadata, string entry, string value) => metadata[entry] = value;

    private static string Display(bool value) => value ? "true" : "false";

    /// <summary>Upstream <c>is_from_queue</c> (<c>is_queued</c>).</summary>
    public static bool IsFromQueue(this IDictionary<string, string> metadata) => metadata.GetBool(IsQueued);

    /// <summary>Upstream <c>set_from_queue</c>.</summary>
    public static void SetFromQueue(this IDictionary<string, string> metadata, bool isQueued) => Set(metadata, IsQueued, Display(isQueued));

    /// <summary>Upstream <c>remove_from_queue</c>.</summary>
    public static void RemoveFromQueue(this IDictionary<string, string> metadata) => metadata.Remove(IsQueued);

    /// <summary>Upstream <c>is_from_autoplay</c> (<c>autoplay.is_autoplay</c>).</summary>
    public static bool IsFromAutoplay(this IDictionary<string, string> metadata) => metadata.GetBool(IsAutoplay);

    /// <summary>Upstream <c>set_from_autoplay</c>.</summary>
    public static void SetFromAutoplay(this IDictionary<string, string> metadata, bool isAutoplay) => Set(metadata, IsAutoplay, Display(isAutoplay));

    /// <summary>Upstream <c>remove_from_autoplay</c>.</summary>
    public static void RemoveFromAutoplay(this IDictionary<string, string> metadata) => metadata.Remove(IsAutoplay);

    /// <summary>Upstream <c>is_hidden</c> (<c>hidden</c>).</summary>
    public static bool IsHidden(this IDictionary<string, string> metadata) => metadata.GetBool(Hidden);

    /// <summary>Upstream <c>set_hidden</c>.</summary>
    public static void SetHidden(this IDictionary<string, string> metadata, bool isHidden) => Set(metadata, Hidden, Display(isHidden));

    /// <summary>Upstream <c>remove_hidden</c>.</summary>
    public static void RemoveHidden(this IDictionary<string, string> metadata) => metadata.Remove(Hidden);

    /// <summary>Upstream <c>get_context_index</c> (custom entry <c>context_index</c>).</summary>
    public static int? GetContextIndex(this IDictionary<string, string> metadata) => metadata.GetUsize(CustomContextIndex);

    /// <summary>Upstream <c>set_context_index</c>.</summary>
    public static void SetContextIndex(this IDictionary<string, string> metadata, int contextIndex) =>
        Set(metadata, CustomContextIndex, contextIndex.ToString(CultureInfo.InvariantCulture));

    /// <summary>Upstream <c>remove_context_index</c>.</summary>
    public static void RemoveContextIndex(this IDictionary<string, string> metadata) => metadata.Remove(CustomContextIndex);

    /// <summary>Upstream <c>get_context_uri</c> (<c>context_uri</c>).</summary>
    public static string? GetContextUri(this IDictionary<string, string> metadata) => metadata.Get(ContextUri);

    /// <summary>Upstream <c>set_context_uri</c>.</summary>
    public static void SetContextUri(this IDictionary<string, string> metadata, string contextUri) => Set(metadata, ContextUri, contextUri);

    /// <summary>Upstream <c>remove_context_uri</c>.</summary>
    public static void RemoveContextUri(this IDictionary<string, string> metadata) => metadata.Remove(ContextUri);

    /// <summary>Upstream <c>get_entity_uri</c> (<c>entity_uri</c>).</summary>
    public static string? GetEntityUri(this IDictionary<string, string> metadata) => metadata.Get(EntityUri);

    /// <summary>Upstream <c>set_entity_uri</c>.</summary>
    public static void SetEntityUri(this IDictionary<string, string> metadata, string entityUri) => Set(metadata, EntityUri, entityUri);

    /// <summary>Upstream <c>remove_entity_uri</c>.</summary>
    public static void RemoveEntityUri(this IDictionary<string, string> metadata) => metadata.Remove(EntityUri);

    /// <summary>Upstream <c>get_iteration</c> (<c>iteration</c>).</summary>
    public static string? GetIteration(this IDictionary<string, string> metadata) => metadata.Get(Iteration);

    /// <summary>Upstream <c>set_iteration</c>.</summary>
    public static void SetIteration(this IDictionary<string, string> metadata, long iteration) =>
        Set(metadata, Iteration, iteration.ToString(CultureInfo.InvariantCulture));

    /// <summary>Upstream <c>remove_iteration</c>.</summary>
    public static void RemoveIteration(this IDictionary<string, string> metadata) => metadata.Remove(Iteration);

    /// <summary>Upstream <c>get_shuffle_seed</c> (custom entry <c>shuffle_seed</c>).</summary>
    public static string? GetShuffleSeed(this IDictionary<string, string> metadata) => metadata.Get(CustomShuffleSeed);

    /// <summary>Upstream <c>set_shuffle_seed</c>.</summary>
    public static void SetShuffleSeed(this IDictionary<string, string> metadata, ulong shuffleSeed) =>
        Set(metadata, CustomShuffleSeed, shuffleSeed.ToString(CultureInfo.InvariantCulture));

    /// <summary>Upstream <c>remove_shuffle_seed</c>.</summary>
    public static void RemoveShuffleSeed(this IDictionary<string, string> metadata) => metadata.Remove(CustomShuffleSeed);

    /// <summary>Upstream <c>get_initial_track</c> (custom entry <c>initial_track</c>).</summary>
    public static string? GetInitialTrack(this IDictionary<string, string> metadata) => metadata.Get(CustomInitialTrack);

    /// <summary>Upstream <c>set_initial_track</c>.</summary>
    public static void SetInitialTrack(this IDictionary<string, string> metadata, string initialTrack) => Set(metadata, CustomInitialTrack, initialTrack);

    /// <summary>Upstream <c>remove_initial_track</c>.</summary>
    public static void RemoveInitialTrack(this IDictionary<string, string> metadata) => metadata.Remove(CustomInitialTrack);
}
