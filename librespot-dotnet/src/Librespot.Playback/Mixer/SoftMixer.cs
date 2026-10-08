// Port of playback/src/mixer/softmixer.rs.
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Librespot.Playback.Mixer;

/// <summary>Software volume: the mapped attenuation factor is applied to samples by the player.</summary>
public sealed class SoftMixer : IMixer
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<SoftMixer>();

    public const string Name = "softvol";

    // There is no AtomicF64, so the f64 is stored as bits in a long (Interlocked / Volatile friendly).
    // Shared with the SoftVolume getter handed to the player.
    private readonly StrongBox<long> _volumeBits;
    private readonly VolumeCtrl _volumeCtrl;

    private SoftMixer(VolumeCtrl volumeCtrl)
    {
        _volumeBits = new StrongBox<long>(BitConverter.DoubleToInt64Bits(0.5));
        _volumeCtrl = volumeCtrl;
    }

    /// <summary>Upstream <c>Mixer::open</c>.</summary>
    public static IMixer Open(MixerConfig config)
    {
        var volumeCtrl = config.VolumeCtrl;
        Log.LogInformation("Mixing with softvol and volume control: {VolumeCtrl}", volumeCtrl);
        return new SoftMixer(volumeCtrl);
    }

    public ushort Volume
    {
        get
        {
            var mappedVolume = BitConverter.Int64BitsToDouble(Volatile.Read(ref _volumeBits.Value));
            return _volumeCtrl.AsUnmapped(mappedVolume);
        }
    }

    public void SetVolume(ushort volume)
    {
        var mappedVolume = _volumeCtrl.ToMapped(volume);
        Volatile.Write(ref _volumeBits.Value, BitConverter.DoubleToInt64Bits(mappedVolume));
    }

    public IVolumeGetter GetSoftVolume() => new SoftVolume(_volumeBits);

    private sealed class SoftVolume(StrongBox<long> volumeBits) : IVolumeGetter
    {
        public double AttenuationFactor() => BitConverter.Int64BitsToDouble(Volatile.Read(ref volumeBits.Value));
    }

    private sealed class StrongBox<T>(T value)
    {
        public T Value = value;
    }
}
