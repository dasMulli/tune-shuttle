using System.Globalization;
using Bridge.Web;
using Bridge.Web.Localization;
using Xunit;

namespace Bridge.Tests.Web;

/// <summary>
/// The culture-aware <see cref="Format"/> helpers (UI-SPEC 3.4) under the cultures <see cref="I18n.SetLanguage"/> sets,
/// and proof that the test runtime has real ICU data (InvariantGlobalization=false in Bridge.Tests.csproj, 3.3). In
/// <see cref="ProcessCultureCollection"/>: the language-switch tests change the process-wide default-thread cultures.
/// </summary>
[Collection(ProcessCultureCollection.Name)]
public sealed class FormattingTests
{
    // Moments built at the local offset of that wall-clock time, so ToLocalTime() keeps 14:05:09 on any machine.
    private static readonly DateTimeOffset Afternoon = AtLocal(new DateTime(2026, 10, 10, 14, 5, 9, DateTimeKind.Unspecified));

    [Fact]
    public void Icu_data_is_loaded_so_german_groups_with_dots()
    {
        using var scope = new CultureScope("de");
        Assert.Equal("1.234,5", 1234.5.ToString("N1", CultureInfo.CurrentCulture));
        Assert.Equal("1.234,5", 1234.5.ToString("N1", CultureInfo.GetCultureInfo("de-DE")));
        Assert.False(CultureInfo.CurrentCulture.Equals(CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("de", "1.234", "0", "-1.234")]
    [InlineData("en", "1,234", "0", "-1,234")]
    public void Integer_groups_thousands(string lang, string thousand, string zero, string negative)
    {
        using var scope = new CultureScope(lang);
        Assert.Equal(thousand, Format.Integer(1234));
        Assert.Equal(zero, Format.Integer(0));
        Assert.Equal(negative, Format.Integer(-1234));
    }

    [Theory]
    [InlineData("de", "1,75", "1.234,5", "2")]
    [InlineData("en", "1.75", "1,234.5", "2")]
    public void Decimal_uses_the_culture_separators(string lang, string twoDigits, string grouped, string noDigits)
    {
        using var scope = new CultureScope(lang);
        Assert.Equal(twoDigits, Format.Decimal(1.75, 2));
        Assert.Equal(grouped, Format.Decimal(1234.5, 1));
        Assert.Equal(noDigits, Format.Decimal(1.75, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Format.Decimal(1, -1));
    }

    [Theory]
    [InlineData("de", "0,4 %", "100,0 %")]
    [InlineData("en", "0.4%", "100.0%")]
    public void Percent_uses_the_culture_spacing(string lang, string small, string whole)
    {
        using var scope = new CultureScope(lang);
        Assert.Equal(small, Spaces(Format.Percent(0.004, 1)));
        Assert.Equal(whole, Spaces(Format.Percent(1, 1)));
    }

    [Theory]
    [InlineData("de", "1.750", "10", "-10")]
    [InlineData("en", "1,750", "10", "-10")]
    public void Milliseconds_turns_frames_into_whole_grouped_ms(string lang, string latency, string offset, string negative)
    {
        using var scope = new CultureScope(lang);
        Assert.Equal(latency, Format.Milliseconds(77175));
        Assert.Equal(offset, Format.Milliseconds(441));
        Assert.Equal(negative, Format.Milliseconds(-441));
        Assert.Equal("1750", Format.FramesAsMs(77175)); // the invariant helper stays
    }

    [Fact]
    public void Time_is_the_local_long_time_pattern()
    {
        using (new CultureScope("de"))
        {
            Assert.Equal("14:05:09", Format.Time(Afternoon));
            Assert.Equal("14:05:09", Format.Time(Afternoon.ToUniversalTime())); // converted to local time
        }

        using (new CultureScope("en"))
        {
            Assert.Equal("2:05:09 PM", Spaces(Format.Time(Afternoon)));
        }
    }

    [Fact]
    public void DateTime_is_the_local_general_pattern()
    {
        using (new CultureScope("de"))
        {
            Assert.Equal("10.10.2026 14:05:09", Format.DateTime(Afternoon));
        }

        using (new CultureScope("en"))
        {
            Assert.Equal("10/10/2026 2:05:09 PM", Spaces(Format.DateTime(Afternoon)));
        }
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void Playback_times_are_invariant_and_remaining_uses_the_minus_sign(string lang)
    {
        using var scope = new CultureScope(lang);
        Assert.Equal("1:12", Format.PlaybackTime(72_000));
        Assert.Equal("0:00", Format.PlaybackTime(-5));
        Assert.Equal("1:01:01", Format.PlaybackTime(3_661_000));
        Assert.Equal("−2:22", Format.Remaining(142_000));
        Assert.Equal("−0:00", Format.Remaining(0));
    }

    [Fact]
    public void The_helpers_follow_a_language_switch()
    {
        // The app's flows have no AsyncLocal culture and read the default-thread one that SetLanguage sets.
        using var scope = new CultureScope("en");
        var l = new I18n("en");
        l.SetLanguage("en");
        Assert.Equal("1,234", CultureScope.InFreshFlow(() => Format.Integer(1234)));
        l.SetLanguage("de");
        var integer = CultureScope.InFreshFlow(() => Format.Integer(1234));
        Assert.Equal("1.234", integer);
        Assert.Equal("1.234 von 100", l.Format("common.levelText", ("value", integer)));
    }

    [Fact]
    public void A_flow_started_after_a_switch_follows_the_next_switch()
    {
        // A page's Every() timer loop starts after a language switch (in the select's change handler) and keeps
        // running across the next one: it must format in the new language, never pin the one current at its start.
        using var scope = new CultureScope("en");
        var l = new I18n("en");
        var (atStart, afterSwitch) = CultureScope.InFreshFlow(() =>
        {
            l.SetLanguage("de");
            using var ticked = new ManualResetEventSlim();
            using var switched = new ManualResetEventSlim();
            var loop = Task.Run(() =>
            {
                var first = Format.Integer(1234);
                ticked.Set();
                switched.Wait();
                return (first, Format.Integer(1234));
            });
            ticked.Wait();
            l.SetLanguage("en");
            switched.Set();
            return loop.GetAwaiter().GetResult();
        });
        Assert.Equal("1.234", atStart);
        Assert.Equal("1,234", afterSwitch);
    }

    private static DateTimeOffset AtLocal(DateTime wallClock) => new(wallClock, TimeZoneInfo.Local.GetUtcOffset(wallClock));

    // ICU versions differ in the space before AM/PM and before % (U+202F, U+00A0 or a plain space); the spec writes a space.
    private static string Spaces(string text) => text.Replace(' ', ' ').Replace(' ', ' ');
}
