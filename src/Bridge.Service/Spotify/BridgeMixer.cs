using Librespot.Playback.Mixer;

namespace Bridge.Service.Spotify;

/// <summary>
/// The librespot mixer of the bridge: it only remembers the Spotify volume and never attenuates the samples
/// (<see cref="IMixer.GetSoftVolume"/> stays <see cref="NoOpVolume"/>). The volume reaches the receivers as the group
/// master level G through <c>PlayerEvent.VolumeChanged</c> (docs/ARCHITECTURE.md, "Group volume model"; SPEC-audio.md 6), so the audio is
/// always sent at full scale and each receiver attenuates it with its own r_i.
/// </summary>
public sealed class BridgeMixer : IMixer
{
    private int _volume;

    /// <summary>Creates the mixer with an initial volume (0..65535).</summary>
    public BridgeMixer(ushort initialVolume)
    {
        _volume = initialVolume;
    }

    /// <inheritdoc />
    public ushort Volume => (ushort)Volatile.Read(ref _volume);

    /// <inheritdoc />
    public void SetVolume(ushort volume) => Volatile.Write(ref _volume, volume);
}
