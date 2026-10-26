using Airplay2.Receiver.Codecs;
using Airplay2.Receiver.Discovery;
using Airplay2.Receiver.Playout;
using Airplay2.Receiver.Timing;
using Airplay2.Sender.Discovery;
using NSubstitute;
using Xunit;

namespace Airplay2.Receiver.Tests;

/// <summary>
/// The wave 0 scaffold of <c>Airplay2.Receiver</c> (SPEC-receiver.md 13): the parts that already behave (identity, feature
/// word, status flags, the AAC factory's fallback) and the inert state of a receiver that was never started.
/// </summary>
public class ScaffoldTests
{
    [Fact]
    public void Identity_is_a_locally_administered_mac_with_a_psi_and_a_derived_public_key()
    {
        var identity = ReceiverIdentity.Create();

        Assert.True(ReceiverIdentity.IsLocallyAdministeredMac(identity.DeviceId), identity.DeviceId);
        Assert.Matches("^([0-9A-F]{2}:){5}[0-9A-F]{2}$", identity.DeviceId);
        Assert.Equal(12, identity.DeviceIdCompact.Length);
        Assert.True(Guid.TryParse(identity.Psi, out _));
        Assert.Equal(identity.Psi, identity.Psi.ToUpperInvariant());
        Assert.Equal(ReceiverIdentity.SeedSize, identity.Seed.Length);
        Assert.Equal(32, identity.PublicKey.Length);
        Assert.Equal(64, identity.PublicKeyHex.Length);
        Assert.Equal(identity.PublicKeyHex, identity.PublicKeyHex.ToLowerInvariant());

        // R11: the public key is derived from the seed, so the same seed gives the same pk after a restart.
        var reloaded = new ReceiverIdentity(identity.DeviceId, identity.Psi, identity.Seed.ToArray());
        Assert.Equal(identity.PublicKey, reloaded.PublicKey);
        Assert.Equal(identity.DeviceId, identity.ToAccessoryIdentity().Id);
        Assert.NotEqual(identity.DeviceId, ReceiverIdentity.Create().DeviceId);
    }

    [Theory]
    [InlineData("02:00:00:00:00:01", true)]
    [InlineData("03:00:00:00:00:01", false)] // multicast
    [InlineData("02:00:00:00:01", false)]
    [InlineData("zz:00:00:00:00:01", false)]
    public void Locally_administered_mac_check(string mac, bool expected) => Assert.Equal(expected, ReceiverIdentity.IsLocallyAdministeredMac(mac));

    [Fact]
    public void Globally_administered_mac_is_rejected()
    {
        // Built at run time: a globally administered MAC literal would look like real hardware to the privacy scan.
        var global = "10" + ReceiverIdentity.Create().DeviceId[2..];
        Assert.False(ReceiverIdentity.IsLocallyAdministeredMac(global), global);
    }

    [Fact]
    public void Feature_word_is_the_R15_word_with_the_listed_bits()
    {
        Assert.Equal(0x00018340405FCA00UL, ReceiverFeatures.Default);
        Assert.Equal(new[] { 9, 11, 14, 15, 16, 17, 18, 19, 20, 22, 30, 38, 40, 41, 47, 48 }, ReceiverFeatures.Bits(ReceiverFeatures.Default));
        Assert.Equal("0x405FCA00,0x18340", ReceiverFeatures.ToTxt(ReceiverFeatures.Default));
        Assert.True(ReceiverFeatures.Has(ReceiverFeatures.Default, AirplayFeatures.Audio | AirplayFeatures.BufferedAudio | AirplayFeatures.PtpClock | AirplayFeatures.ControlChannelEncrypt));
        Assert.True(ReceiverFeatures.Has(ReceiverFeatures.Default, AirplayFeatures.AudioMetaCovers | AirplayFeatures.AudioMetaProgress | AirplayFeatures.AudioMetaTxtDaap));
        Assert.False(ReceiverFeatures.Has(ReceiverFeatures.Default, 50)); // R10: no bit 50
        Assert.False(ReceiverFeatures.Has(ReceiverFeatures.Default, 46)); // RQ1: no HomeKit pairing bit
        Assert.Equal(ReceiverFeatures.Default, ReceiverFeatures.For(aacAvailable: true));
    }

    [Fact]
    public void Alac_only_variant_clears_bit_20_and_nothing_else()
    {
        Assert.Equal(ReceiverFeatures.Default & ~(1UL << 20), ReceiverFeatures.AlacOnly);
        Assert.False(ReceiverFeatures.Has(ReceiverFeatures.AlacOnly, AirplayFeatures.ReceiveAudioAacLc));
        Assert.True(ReceiverFeatures.Has(ReceiverFeatures.AlacOnly, AirplayFeatures.BufferedAudio)); // bit 40 stays: iOS would fall back to AirPlay 1 without it (R1)
        Assert.Equal("0x404FCA00,0x18340", ReceiverFeatures.ToTxt(ReceiverFeatures.AlacOnly));
        Assert.Equal(ReceiverFeatures.AlacOnly, ReceiverFeatures.For(aacAvailable: false));
        Assert.Equal(0x40000u, ReceiverFeatures.BufferStreamFormatsFor(aacAvailable: false));
        Assert.Equal(0x440000u, ReceiverFeatures.BufferStreamFormatsFor(aacAvailable: true));
        Assert.Equal("0,1", ReceiverFeatures.RaopCompressionFor(aacAvailable: false));
        Assert.Equal("0,1,2", ReceiverFeatures.RaopCompressionFor(aacAvailable: true));
    }

    [Fact]
    public void Fex_is_the_little_endian_word_in_unpadded_base64()
    {
        var fex = ReceiverFeatures.ToFex(ReceiverFeatures.Default);
        Assert.DoesNotContain('=', fex);
        var bytes = Convert.FromBase64String(fex + new string('=', (4 - fex.Length % 4) % 4));
        Assert.Equal(ReceiverFeatures.Default, BitConverter.ToUInt64(bytes, 0) is var le && BitConverter.IsLittleEndian ? le : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(le));
    }

    [Theory]
    [InlineData(false, false, 0x4u, "0x4")]
    [InlineData(true, false, 0x84u, "0x84")]
    [InlineData(false, true, 0x20004u, "0x20004")]
    public void Status_flags_follow_R7_and_bit_17(bool password, bool active, uint expected, string txt)
    {
        var flags = ReceiverFeatures.StatusFlagsFor(password, active);
        Assert.Equal(expected, (uint)flags);
        Assert.Equal(txt, ReceiverFeatures.StatusFlagsToTxt(flags));
    }

    [Fact]
    public void Ffmpeg_factory_fails_with_a_reason_when_the_libraries_are_missing()
    {
        // A directory that does not exist: the default search may find the libraries once Codecs/FfmpegAacDecoderTests bound them.
        var factory = new FfmpegAacDecoderFactory(Path.Combine(Path.GetTempPath(), "tune-shuttle-no-ffmpeg-" + Guid.NewGuid().ToString("N")));

        Assert.False(factory.TryCreate(out var decoder, out var reason));
        Assert.Null(decoder);
        Assert.Contains("libavcodec not found", reason);
    }

    [Fact]
    public void Playout_options_default_to_the_spec_values()
    {
        var options = new PlayoutOptions();

        Assert.Equal(77175 + 11025 + 8820, options.Lpipe); // 5.4
        Assert.Equal(TimeSpan.FromMilliseconds(300), options.AnchorLead); // R6
        Assert.Equal(TimeSpan.FromMilliseconds(200), options.LateThreshold); // R6
        Assert.Equal(TimeSpan.FromMilliseconds(20), options.DriftThreshold); // R6
        Assert.Equal(22050 + 11025 + 8820, PlayoutOptions.LpipeFor(22050, 8820)); // RQ7: a 0.5 s L
    }

    [Fact]
    public async Task A_new_receiver_is_off_and_inert_until_started()
    {
        var identity = ReceiverIdentity.Create();
        var output = Substitute.For<IReceiverAudioOutput>();
        var receiver = new AirplayReceiver(new AirplayReceiverOptions { Identity = identity, RtspPort = 0 }, output);
        Assert.Equal(ReceiverStatus.Off, receiver.Status);
        Assert.Null(receiver.Sender);
        Assert.Equal(0, receiver.RtspPort);
        Assert.Null(receiver.Statistics);
        Assert.Null(receiver.Advertisement);
        Assert.False(receiver.Events.Completion.IsCompleted);

        // Without a session these are no-ops (SPEC-receiver.md 4.9, 3.3); disposing completes the event stream.
        await receiver.EndSessionAsync("nothing to end", CancellationToken.None);
        await receiver.StopAsync(CancellationToken.None);
        await receiver.DisposeAsync();
        Assert.True(receiver.Events.Completion.IsCompleted);
        Assert.Empty(output.ReceivedCalls());

        await using var slave = new PtpSlave();
        Assert.False(slave.TryMapToLocal(0, out _)); // no estimate before the first Sync/Follow_Up pair (6.2)
        Assert.Equal(new PtpSlaveStatistics(null, 0, null, 0), slave.Statistics);
    }
}
