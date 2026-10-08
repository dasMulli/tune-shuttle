using Librespot.Playback;
using Librespot.Playback.Mixer;
using Xunit;

namespace Librespot.Tests.Playback;

public class VolumeMappingTests
{
    public static IEnumerable<object[]> Controls =>
    [
        [VolumeCtrl.Default],
        [new VolumeCtrl.Cubic(VolumeCtrl.DefaultDbRange)],
        [new VolumeCtrl.Linear()],
        [new VolumeCtrl.Fixed()],
    ];

    [Theory]
    [MemberData(nameof(Controls))]
    public void Endpoints_are_exact_and_mapping_roundtrips(VolumeCtrl ctrl)
    {
        Assert.Equal(0.0, ctrl.ToMapped(0));
        Assert.Equal(1.0, ctrl.ToMapped(VolumeCtrl.MaxVolume));
        Assert.Equal(0, ctrl.AsUnmapped(0.0));
        Assert.Equal(VolumeCtrl.MaxVolume, ctrl.AsUnmapped(1.0));

        foreach (ushort v in new ushort[] { 1, 1000, 32768, 60000, 65534 })
        {
            var mapped = ctrl.ToMapped(v);
            Assert.InRange(mapped, 0.0, 1.0);
            Assert.InRange(ctrl.AsUnmapped(mapped), v - 1, v + 1);
        }
    }

    [Fact]
    public void Log_mapping_at_half_volume_is_minus_30_db()
    {
        var ctrl = new VolumeCtrl.Log(60.0);
        var mapped = ctrl.ToMapped(32768);
        Assert.Equal(-30.0, Player.RatioToDb(mapped), 1);
    }

    [Fact]
    public void SoftMixer_stores_mapped_volume()
    {
        var mixer = SoftMixer.Open(new MixerConfig());
        mixer.SetVolume(VolumeCtrl.MaxVolume);
        Assert.Equal(1.0, mixer.GetSoftVolume().AttenuationFactor());
        Assert.Equal(VolumeCtrl.MaxVolume, mixer.Volume);
        mixer.SetVolume(0);
        Assert.Equal(0.0, mixer.GetSoftVolume().AttenuationFactor());
    }

    [Fact]
    public void Config_parsers()
    {
        Assert.Equal(Bitrate.Bitrate320, BitrateExtensions.Parse("320"));
        Assert.Equal(AudioFormat.S24_3, AudioFormatExtensions.Parse("s24_3"));
        Assert.Equal(3, AudioFormat.S24_3.Size());
        Assert.Equal(new VolumeCtrl.Cubic(40.0), VolumeCtrl.ParseWithRange("CUBIC", 40.0));
        Assert.Null(VolumeCtrl.Parse("loud"));
        Assert.True(new PlayerConfig().Gapless);
        Assert.Equal(Player.DurationToCoefficient(TimeSpan.FromMilliseconds(5)), new PlayerConfig().NormalisationAttackCf);
    }

    [Fact]
    public void PlayerEvent_play_request_id()
    {
        var uri = Librespot.Core.SpotifyUri.FromUri("spotify:track:4GNcXTGWmnZ3ySrqvol3o4");
        Assert.Equal(7ul, new PlayerEvent.Playing(7, uri, 0).GetPlayRequestId());
        Assert.Null(new PlayerEvent.VolumeChanged(1).GetPlayRequestId());
    }
}
