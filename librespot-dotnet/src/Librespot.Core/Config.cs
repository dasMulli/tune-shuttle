// Port of core/src/config.rs.
using System.Runtime.InteropServices;
using ProtoDeviceType = Spotify.Connectstate.Devices.DeviceType;

namespace Librespot.Core;

/// <summary>Platform constants (upstream <c>config::OS</c> / <c>config::os_version()</c>).</summary>
public static class Platform
{
    /// <summary>Spotify client id of the desktop "keymaster" client (upstream <c>KEYMASTER_CLIENT_ID</c>).</summary>
    public const string KeymasterClientId = "65b708073fc0480ea92a077233ca87bd";

    /// <summary>Spotify client id of the Android app (upstream <c>ANDROID_CLIENT_ID</c>).</summary>
    public const string AndroidClientId = "9a8d2f0ce77a4e248bb71fefcb557637";

    /// <summary>Spotify client id of the iOS app (upstream <c>IOS_CLIENT_ID</c>).</summary>
    public const string IosClientId = "58bd3c95768941ea9eb4350aaa033eb3";

    /// <summary>
    /// The current platform using Rust's <c>std::env::consts::OS</c> vocabulary ("windows", "linux", "macos",
    /// "android", "ios", "freebsd", ...). Upstream hardcodes this at compile time; adjust it only to mock another
    /// platform (then <see cref="OsVersion"/> has to return a valid version for that platform, e.g. android: 30, ios: 17).
    /// </summary>
    public static string Os { get; set; } = DetectOs();

    /// <summary>See <c>sysinfo::System::os_version</c>; "0" when unknown.</summary>
    public static string OsVersion()
    {
        var v = Environment.OSVersion.Version;
        return v.Major == 0 ? "0" : v.ToString();
    }

    private static string DetectOs()
    {
        if (OperatingSystem.IsAndroid()) return "android";
        if (OperatingSystem.IsIOS() || OperatingSystem.IsTvOS()) return "ios";
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()) return "macos";
        if (OperatingSystem.IsLinux()) return "linux";
        if (OperatingSystem.IsFreeBSD()) return "freebsd";
        return RuntimeInformation.OSDescription.ToLowerInvariant();
    }
}

/// <summary>Session-wide configuration (upstream <c>SessionConfig</c>).</summary>
public sealed record SessionConfig
{
    public required string ClientId { get; init; }
    public required string DeviceId { get; init; }
    public Uri? Proxy { get; init; }
    public ushort? ApPort { get; init; }
    public required string TmpDir { get; init; }
    public bool? Autoplay { get; init; }

    /// <summary>Upstream <c>SessionConfig::default()</c>: random device id, client id chosen by platform, system temp dir.</summary>
    public static SessionConfig Default() => DefaultForOs(Platform.Os);

    internal static SessionConfig DefaultForOs(string os)
    {
        var clientId = os switch
        {
            "android" => Platform.AndroidClientId,
            "ios" => Platform.IosClientId,
            _ => Platform.KeymasterClientId,
        };

        return new SessionConfig
        {
            ClientId = clientId,
            DeviceId = Guid.NewGuid().ToString("D"),
            Proxy = null,
            ApPort = null,
            TmpDir = Path.GetTempPath(),
            Autoplay = null,
        };
    }
}

/// <summary>Kind of device shown in the Spotify Connect device list (upstream <c>config::DeviceType</c>).</summary>
public enum DeviceType
{
    Unknown = 0,
    Computer = 1,
    Tablet = 2,
    Smartphone = 3,
    Speaker = 4,
    Tv = 5,
    Avr = 6,
    Stb = 7,
    AudioDongle = 8,
    GameConsole = 9,
    CastAudio = 10,
    CastVideo = 11,
    Automobile = 12,
    Smartwatch = 13,
    Chromebook = 14,
    UnknownSpotify = 100,
    CarThing = 101,
    Observer = 102,
}

public static class DeviceTypeExtensions
{
    /// <summary>Upstream <c>Default</c>: <see cref="DeviceType.Speaker"/>.</summary>
    public const DeviceType Default = DeviceType.Speaker;

    /// <summary>Upstream <c>FromStr</c> (case-insensitive). Returns null for unknown names.</summary>
    public static DeviceType? Parse(string s) => s.ToLowerInvariant() switch
    {
        "computer" => DeviceType.Computer,
        "tablet" => DeviceType.Tablet,
        "smartphone" => DeviceType.Smartphone,
        "speaker" => DeviceType.Speaker,
        "tv" => DeviceType.Tv,
        "avr" => DeviceType.Avr,
        "stb" => DeviceType.Stb,
        "audiodongle" => DeviceType.AudioDongle,
        "gameconsole" => DeviceType.GameConsole,
        "castaudio" => DeviceType.CastAudio,
        "castvideo" => DeviceType.CastVideo,
        "automobile" => DeviceType.Automobile,
        "smartwatch" => DeviceType.Smartwatch,
        "chromebook" => DeviceType.Chromebook,
        "carthing" => DeviceType.CarThing,
        _ => null,
    };

    /// <summary>Upstream <c>Display</c> / <c>From&lt;DeviceType&gt; for &amp;str</c>.</summary>
    public static string ToDisplayString(this DeviceType d) => d switch
    {
        DeviceType.Unknown => "Unknown",
        DeviceType.Computer => "Computer",
        DeviceType.Tablet => "Tablet",
        DeviceType.Smartphone => "Smartphone",
        DeviceType.Speaker => "Speaker",
        DeviceType.Tv => "TV",
        DeviceType.Avr => "AVR",
        DeviceType.Stb => "STB",
        DeviceType.AudioDongle => "AudioDongle",
        DeviceType.GameConsole => "GameConsole",
        DeviceType.CastAudio => "CastAudio",
        DeviceType.CastVideo => "CastVideo",
        DeviceType.Automobile => "Automobile",
        DeviceType.Smartwatch => "Smartwatch",
        DeviceType.Chromebook => "Chromebook",
        DeviceType.UnknownSpotify => "UnknownSpotify",
        DeviceType.CarThing => "CarThing",
        DeviceType.Observer => "Observer",
        _ => "Unknown",
    };

    /// <summary>
    /// Upstream <c>From&lt;DeviceType&gt; for protocol::devices::DeviceType</c>.
    /// Note: upstream maps CastAudio to CAST_VIDEO and CastVideo to CAST_AUDIO; this is reproduced verbatim.
    /// </summary>
    public static ProtoDeviceType ToProto(this DeviceType value) => value switch
    {
        DeviceType.Unknown => ProtoDeviceType.Unknown,
        DeviceType.Computer => ProtoDeviceType.Computer,
        DeviceType.Tablet => ProtoDeviceType.Tablet,
        DeviceType.Smartphone => ProtoDeviceType.Smartphone,
        DeviceType.Speaker => ProtoDeviceType.Speaker,
        DeviceType.Tv => ProtoDeviceType.Tv,
        DeviceType.Avr => ProtoDeviceType.Avr,
        DeviceType.Stb => ProtoDeviceType.Stb,
        DeviceType.AudioDongle => ProtoDeviceType.AudioDongle,
        DeviceType.GameConsole => ProtoDeviceType.GameConsole,
        DeviceType.CastAudio => ProtoDeviceType.CastVideo,
        DeviceType.CastVideo => ProtoDeviceType.CastAudio,
        DeviceType.Automobile => ProtoDeviceType.Automobile,
        DeviceType.Smartwatch => ProtoDeviceType.Smartwatch,
        DeviceType.Chromebook => ProtoDeviceType.Chromebook,
        DeviceType.UnknownSpotify => ProtoDeviceType.UnknownSpotify,
        DeviceType.CarThing => ProtoDeviceType.CarThing,
        DeviceType.Observer => ProtoDeviceType.Observer,
        _ => ProtoDeviceType.Unknown,
    };
}
