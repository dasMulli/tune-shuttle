using Airplay2.Sender.Playback;
using Airplay2.Sender.Session;
using Xunit;

namespace Airplay2.Sender.Tests.Playback;

/// <summary>
/// The receiver side of the volume mapping (SPEC-receiver.md R9, 4.8, 11.1): <see cref="VolumeMapping"/> in both
/// directions, the six-decimal <c>GET_PARAMETER</c> answer and the no-output default of <see cref="ReceiverParameters"/>.
/// </summary>
public sealed class VolumeMappingReceiverTests
{
    [Theory]
    [InlineData(0, -144.0)]
    [InlineData(1, -29.7)]
    [InlineData(20, -24.0)]
    [InlineData(50, -15.0)]
    [InlineData(100, 0.0)]
    public void Level_maps_to_db_and_back(int level, double db)
    {
        Assert.Equal(db, VolumeMapping.LevelToDb(level), 9);
        Assert.Equal(level, VolumeMapping.DbToLevel(db));
    }

    [Fact]
    public void Every_level_round_trips_also_through_the_wire_text()
    {
        for (var level = 0; level <= 100; level++)
        {
            Assert.Equal(level, VolumeMapping.DbToLevel(VolumeMapping.LevelToDb(level)));
            var wire = VolumeMapping.FormatParameter(VolumeMapping.LevelToDb(level));
            Assert.Equal(level, VolumeMapping.DbToLevel(ReceiverParameters.ParseVolume(wire)!.Value));
        }
    }

    [Theory]
    [InlineData(-144.0)]
    [InlineData(-31.0)]
    [InlineData(-30.0)]
    [InlineData(-1000.0)]
    [InlineData(double.NegativeInfinity)]
    public void Mute_and_anything_at_or_below_minus_30_is_level_0(double db)
    {
        Assert.Equal(0, VolumeMapping.DbToLevel(db));
    }

    [Fact]
    public void Values_above_full_scale_clamp_to_100()
    {
        Assert.Equal(100, VolumeMapping.DbToLevel(6.0));
    }

    [Fact]
    public void Get_parameter_answer_has_the_airplay2_receiver_shape()
    {
        Assert.Equal("volume: -24.000000\r\n", VolumeMapping.FormatParameter(-24.0));
        Assert.Equal("volume: -144.000000\r\n", VolumeMapping.FormatParameter(VolumeMapping.LevelToDb(0)));
        Assert.Equal("volume: -29.700000\r\n", VolumeMapping.FormatParameter(VolumeMapping.LevelToDb(1)));
        Assert.Equal("volume: 0.000000\r\n", VolumeMapping.FormatParameter(VolumeMapping.LevelToDb(100)));
        Assert.Equal("volume: -24.000000\r\n", ReceiverParameters.FormatVolumeAnswer(outputLevel: 20, lastSenderLevel: null));
    }

    [Theory]
    [InlineData(20, null, 20)]
    [InlineData(20, 70, 20)] // an output's G wins over the session's last value
    [InlineData(null, 70, 70)] // no output: the last level the sender set in this session
    [InlineData(null, 0, 0)]
    [InlineData(null, null, 50)] // no output, nothing set: 50 (-15 dB)
    public void Answered_level_follows_the_no_output_default(int? outputLevel, int? lastSenderLevel, int expected)
    {
        Assert.Equal(expected, ReceiverParameters.AnsweredLevel(outputLevel, lastSenderLevel));
        Assert.Equal(VolumeMapping.LevelToDb(expected), ReceiverParameters.AnsweredDb(outputLevel, lastSenderLevel));
        Assert.Equal(VolumeMapping.FormatParameter(VolumeMapping.LevelToDb(expected)), ReceiverParameters.FormatVolumeAnswer(outputLevel, lastSenderLevel));
    }

    [Fact]
    public void No_output_and_no_sender_value_answers_minus_15()
    {
        Assert.Equal(50, ReceiverParameters.DefaultLevel);
        Assert.Equal("volume: -15.000000\r\n", ReceiverParameters.FormatVolumeAnswer(null, null));
        Assert.Equal(-15.0, ReceiverParameters.AnsweredDb(null, null));
    }

    [Fact]
    public void Out_of_range_levels_are_clamped_in_the_answer()
    {
        Assert.Equal(100, ReceiverParameters.AnsweredLevel(150, null));
        Assert.Equal(0, ReceiverParameters.AnsweredLevel(null, -5));
    }

    [Fact]
    public void Sender_volume_maps_through_db_to_level()
    {
        Assert.Equal(20, ReceiverParameters.SenderLevel("volume: -24.000000\r\n"));
        Assert.Equal(0, ReceiverParameters.SenderLevel("volume: -144.000000\r\n"));
        Assert.Null(ReceiverParameters.SenderLevel("progress: 1/2/3\r\n"));
    }
}
