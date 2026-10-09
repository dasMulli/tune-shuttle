using System.Globalization;

namespace Airplay2.Sender.Discovery;

/// <summary>
/// The 64-bit <c>features</c> bitmask of an AirPlay receiver (SPEC-control.md 1.3). Names follow airplay2-receiver's
/// <c>FeatureFlags</c> where the three public lists agree; bit 32 is <see cref="CarPlay"/> (pyatv: SupportsVolume?)
/// and is not used by the sender.
/// </summary>
[Flags]
public enum AirplayFeatures : ulong
{
    None = 0,
    Video = 1UL << 0,
    Photo = 1UL << 1,
    VideoFairPlay = 1UL << 2,
    VideoVolumeControl = 1UL << 3,
    VideoHttpLiveStreams = 1UL << 4,
    Slideshow = 1UL << 5,
    Screen = 1UL << 7,
    ScreenRotate = 1UL << 8,
    /// <summary>Bit 9: required for audio at all.</summary>
    Audio = 1UL << 9,
    AudioRedundant = 1UL << 11,
    FpsApV2p5AesGcm = 1UL << 12,
    MfiHardware = 1UL << 13,
    MfiSoftFairPlay = 1UL << 14,
    /// <summary>Bit 15: artwork via SET_PARAMETER.</summary>
    AudioMetaCovers = 1UL << 15,
    /// <summary>Bit 16: progress via SET_PARAMETER.</summary>
    AudioMetaProgress = 1UL << 16,
    /// <summary>Bit 17: DMAP text metadata.</summary>
    AudioMetaTxtDaap = 1UL << 17,
    ReceiveAudioPcm = 1UL << 18,
    /// <summary>Bit 19: ALAC accepted (our codec).</summary>
    ReceiveAudioAlac = 1UL << 19,
    ReceiveAudioAacLc = 1UL << 20,
    ReceiveAudioAacEld = 1UL << 21,
    AudioUnencrypted = 1UL << 22,
    RsaAuth = 1UL << 23,
    ITunes4WEncryption = 1UL << 25,
    AudioAesMfi = 1UL << 26,
    LegacyPairing = 1UL << 27,
    PlistMetadata = 1UL << 29,
    UnifiedAdvertisingInfo = 1UL << 30,
    CarPlay = 1UL << 32,
    VideoPlayQueue = 1UL << 33,
    FromCloud = 1UL << 34,
    TlsPsk = 1UL << 35,
    CarPlayControl = 1UL << 37,
    /// <summary>Bit 38: control channel encryption, an AirPlay 2 marker.</summary>
    ControlChannelEncrypt = 1UL << 38,
    /// <summary>Bit 40: stream type 103 available.</summary>
    BufferedAudio = 1UL << 40,
    /// <summary>Bit 41: the receiver has a PTP clock.</summary>
    PtpClock = 1UL << 41,
    ScreenMultiCodec = 1UL << 42,
    SystemPairing = 1UL << 43,
    ValeriaScreenSender = 1UL << 44,
    /// <summary>Bit 45: the receiver prefers NTP realtime (rare).</summary>
    NtpClock = 1UL << 45,
    /// <summary>Bit 46: regular HomeKit pair-setup possible.</summary>
    HomeKitPairing = 1UL << 46,
    /// <summary>Bit 47: SETPEERS supported.</summary>
    PeerManagement = 1UL << 47,
    /// <summary>Bit 48: transient pairing supported.</summary>
    TransientPairing = 1UL << 48,
    VideoV2 = 1UL << 49,
    /// <summary>Bit 50: metadata via POST /command bplists instead of DMAP.</summary>
    NowPlayingInfo = 1UL << 50,
    /// <summary>Bit 51: unified pair-setup and MFi; see open question C-7.6.</summary>
    MfiPairSetup = 1UL << 51,
    PeersExtendedMessage = 1UL << 52,
    ApSync = 1UL << 54,
    WakeOnLan = 1UL << 55,
    WakeOnLan2 = 1UL << 56,
    HangdogRemoteControl = 1UL << 58,
    AudioStreamConnectionSetup = 1UL << 59,
    AudioMediaDataControl = 1UL << 60,
    Rfc2198Redundancy = 1UL << 61,
}

/// <summary>The <c>flags</c> / <c>statusFlags</c> / <c>sf</c> bitmask (SPEC-control.md 1.4).</summary>
[Flags]
public enum AirplayStatusFlags : uint
{
    None = 0,
    ProblemsExist = 1u << 0,
    NotConfigured = 1u << 1,
    AudioLink = 1u << 2,
    /// <summary>Bit 3: pairing mandatory, PIN shown by the receiver.</summary>
    PinMode = 1u << 3,
    PinEntry = 1u << 4,
    PinMatch = 1u << 5,
    SupportsFromCloud = 1u << 6,
    /// <summary>Bit 7: a password is required (used as the SRP password).</summary>
    PasswordRequired = 1u << 7,
    Unknown08 = 1u << 8,
    /// <summary>Bit 9: one-time pairing with a displayed PIN is required (regular HomeKit pair-setup).</summary>
    PairingPinRequired = 1u << 9,
    /// <summary>Bit 10: HomeKit access control configured; <c>acl</c> says who may connect.</summary>
    HomeKitAccessControl = 1u << 10,
    RemoteControlRelay = 1u << 11,
    SilentPrimary = 1u << 12,
    TightSyncIsGroupLeader = 1u << 13,
    TightSyncBuddyNotReachable = 1u << 14,
    AppleMusicSubscriber = 1u << 15,
    ICloudLibraryOn = 1u << 16,
    /// <summary>Bit 17: another sender is streaming (AirPlay 2 still allows interruption).</summary>
    ReceiverSessionActive = 1u << 17,
    Unknown19 = 1u << 19,
}

/// <summary>Parsers for the hex-formatted TXT values.</summary>
public static class AirplayTxtValues
{
    /// <summary>
    /// Parses <c>features=0x445F8A00,0x1C340</c> (low word, high word) or a single <c>0x...</c> value, or a plain decimal
    /// as returned by GET /info. Returns null for unparsable input.
    /// </summary>
    public static AirplayFeatures? ParseFeatures(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2)
        {
            return null;
        }

        if (!TryParseHexOrDecimal(parts[0], out var low))
        {
            return null;
        }

        ulong high = 0;
        if (parts.Length == 2 && !TryParseHexOrDecimal(parts[1], out high))
        {
            return null;
        }

        return parts.Length == 2 ? (AirplayFeatures)((high << 32) | (low & 0xFFFFFFFF)) : (AirplayFeatures)low;
    }

    /// <summary>Parses <c>flags=0x404</c> or a decimal <c>statusFlags</c>.</summary>
    public static AirplayStatusFlags? ParseFlags(string? value) =>
        TryParseHexOrDecimal(value, out var v) ? (AirplayStatusFlags)(uint)v : null;

    /// <summary>Formats a feature word the way receivers advertise it: <c>0x&lt;low&gt;,0x&lt;high&gt;</c>.</summary>
    public static string FormatFeatures(AirplayFeatures features)
    {
        var v = (ulong)features;
        return $"0x{(uint)v:X},0x{(uint)(v >> 32):X}";
    }

    /// <summary>The set bit numbers of a feature word, ascending (for logs and diagnostics).</summary>
    public static IEnumerable<int> Bits(AirplayFeatures features)
    {
        var v = (ulong)features;
        for (var i = 0; i < 64; i++)
        {
            if ((v & (1UL << i)) != 0)
            {
                yield return i;
            }
        }
    }

    private static bool TryParseHexOrDecimal(string? s, out ulong value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s))
        {
            return false;
        }

        s = s.Trim();
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
