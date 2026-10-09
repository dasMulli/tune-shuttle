namespace Airplay2.Sender.Discovery;

/// <summary>How a receiver must be paired (SPEC-control.md 1.6 step 2).</summary>
public enum PairingRequirement
{
    /// <summary>Transient pair-setup with PIN 3939 on every connection; nothing persisted.</summary>
    Transient,

    /// <summary>Regular HomeKit pair-setup with a PIN shown by the receiver, once; pair-verify afterwards.</summary>
    Pin,

    /// <summary>Regular pair-setup with the receiver's password as the SRP password.</summary>
    Password,

    /// <summary>Only HomeKit home members may connect (<c>acl=1</c>) or <c>act=2</c>: show an explanation, do not try.</summary>
    Disabled,
}

/// <summary>What the sender concluded from the TXT records about how to talk to a receiver (SPEC-control.md 1.6).</summary>
public sealed record ReceiverCapabilities(
    bool IsAirPlay2,
    PairingRequirement Pairing,
    bool SupportsRealtimeStream,
    bool SupportsBufferedStream,
    bool HasPtpClock,
    bool PrefersNtp,
    bool SupportsAlac,
    bool WantsTextMetadata,
    bool WantsArtwork,
    bool WantsProgress,
    bool UsesNowPlayingCommands,
    bool MayNeedPairSetupWithAuth)
{
    /// <summary>Applies the decision procedure of SPEC-control.md 1.6 to the TXT records.</summary>
    public static ReceiverCapabilities From(AirplayTxtRecord airplay, RaopTxtRecord? raop)
    {
        ArgumentNullException.ThrowIfNull(airplay);
        var f = airplay.Features;
        var s = airplay.Flags;
        var isAirPlay2 = (f & AirplayFeatures.Audio) != 0 && (f & AirplayFeatures.TransientPairing) != 0;

        PairingRequirement pairing;
        if (airplay.AccessControlLevel == 1 || airplay.AccessControlType == 2)
        {
            pairing = PairingRequirement.Disabled;
        }
        else if ((s & (AirplayStatusFlags.PairingPinRequired | AirplayStatusFlags.PinMode)) != 0)
        {
            pairing = PairingRequirement.Pin;
        }
        else if ((s & AirplayStatusFlags.PasswordRequired) != 0 || airplay.PasswordRequired || raop?.PasswordRequired == true)
        {
            pairing = PairingRequirement.Password;
        }
        else if ((f & (AirplayFeatures.TransientPairing | AirplayFeatures.SystemPairing)) != 0)
        {
            pairing = PairingRequirement.Transient;
        }
        else
        {
            pairing = PairingRequirement.Pin;
        }

        var transportUdp = raop is null || string.Equals(raop.Transport, "UDP", StringComparison.OrdinalIgnoreCase) || raop.Transport is null;
        var alac = (f & AirplayFeatures.ReceiveAudioAlac) != 0 && (raop is null || raop.Codecs.Contains(RaopTxtRecord.CodecAlac));

        return new ReceiverCapabilities(
            isAirPlay2,
            pairing,
            SupportsRealtimeStream: (f & AirplayFeatures.Audio) != 0 && transportUdp,
            SupportsBufferedStream: (f & AirplayFeatures.BufferedAudio) != 0,
            HasPtpClock: (f & AirplayFeatures.PtpClock) != 0,
            PrefersNtp: (f & AirplayFeatures.NtpClock) != 0,
            SupportsAlac: alac,
            WantsTextMetadata: (f & AirplayFeatures.AudioMetaTxtDaap) != 0 || raop?.MetadataTypes.Contains(RaopTxtRecord.MetadataText) == true,
            WantsArtwork: (f & AirplayFeatures.AudioMetaCovers) != 0 || raop?.MetadataTypes.Contains(RaopTxtRecord.MetadataArtwork) == true,
            WantsProgress: (f & AirplayFeatures.AudioMetaProgress) != 0 || raop?.MetadataTypes.Contains(RaopTxtRecord.MetadataProgress) == true,
            UsesNowPlayingCommands: (f & AirplayFeatures.NowPlayingInfo) != 0,
            MayNeedPairSetupWithAuth: (f & AirplayFeatures.MfiPairSetup) != 0);
    }
}
