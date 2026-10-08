// Port of core/src/version.rs (+ the build-time values from core/build.rs).
using System.Reflection;

namespace Librespot.Core;

/// <summary>Version constants reported to Spotify and shown by the CLI (upstream module <c>version</c>; named LibrespotVersion to avoid clashing with System.Version).</summary>
public static class LibrespotVersion
{
    private static readonly Lazy<string> _buildId = new(GenerateBuildId);

    /// <summary>Short sha of the upstream librespot commit this port tracks (upstream <c>SHA_SHORT</c> = VERGEN_GIT_SHA).</summary>
    public static string ShaShort { get; } = ReadMetadata("UpstreamCommit")?[..7] ?? "unknown";

    /// <summary>Version string of the form "librespot-&lt;sha&gt;" (upstream <c>VERSION_STRING</c>).</summary>
    public static string VersionString => "librespot-" + ShaShort;

    /// <summary>Build date (UTC, yyyy-MM-dd) (upstream <c>BUILD_DATE</c>).</summary>
    public static string BuildDate { get; } = ReadMetadata("BuildDate") ?? "unknown";

    /// <summary>Date of the upstream commit (upstream <c>COMMIT_DATE</c>).</summary>
    public static string CommitDate { get; } = ReadMetadata("UpstreamCommitDate") ?? "unknown";

    /// <summary>Librespot crate version (upstream <c>SEMVER</c> = CARGO_PKG_VERSION).</summary>
    public const string SemVer = "0.8.0";

    /// <summary>
    /// A random build id (upstream <c>BUILD_ID</c>). Upstream generates eight random alphanumerics at build
    /// time, or uses SOURCE_DATE_EPOCH when set; this port generates it once per process unless
    /// SOURCE_DATE_EPOCH is set.
    /// </summary>
    public static string BuildId => _buildId.Value;

    /// <summary>The protocol version of the Spotify desktop client.</summary>
    public const ulong SpotifyVersion = 124200290;

    /// <summary>The semantic version of the Spotify desktop client.</summary>
    public const string SpotifySemanticVersion = "1.2.52.442";

    /// <summary><c>property_set_id</c> related to desktop version 1.2.52.442.</summary>
    public const string SpotifyPropertySetId = "b4c7e4b5835079ed94391b2e65fca0fdba65eb50";

    /// <summary>The protocol version of the Spotify mobile app.</summary>
    public const string SpotifyMobileVersion = "8.9.82.620";

    /// <summary><c>property_set_id</c> related to mobile version 8.9.82.620.</summary>
    public const string SpotifyMobilePropertySetId = "5ec87c2cc32e7c509703582cfaaa3c7ad253129d5701127c1f5eab5c9531736c";

    /// <summary>The general spirc version.</summary>
    public const string SpotifySpircVersion = "3.2.6";

    /// <summary>The user agent to fall back to, if one could not be determined dynamically.</summary>
    public const string FallbackUserAgent = "Spotify/124200290 Linux/0 (librespot)";

    /// <summary>Upstream <c>spotify_version()</c>: mobile version on android/ios, else the desktop protocol version.</summary>
    public static string SpotifyVersionForPlatform() => Platform.Os switch
    {
        "android" or "ios" => SpotifyMobileVersion,
        _ => SpotifyVersion.ToString(),
    };

    /// <summary>Upstream <c>spotify_semantic_version()</c>.</summary>
    public static string SpotifySemanticVersionForPlatform() => Platform.Os switch
    {
        "android" or "ios" => SpotifyMobileVersion,
        _ => SpotifySemanticVersion,
    };

    private static string? ReadMetadata(string key) =>
        typeof(LibrespotVersion).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key && !string.IsNullOrEmpty(a.Value))?.Value;

    private static string GenerateBuildId()
    {
        var fromEnv = Environment.GetEnvironmentVariable("SOURCE_DATE_EPOCH");
        if (!string.IsNullOrEmpty(fromEnv))
        {
            return fromEnv;
        }

        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return string.Create(8, alphabet, static (span, chars) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = chars[Random.Shared.Next(chars.Length)];
            }
        });
    }
}
