using System.Globalization;
using System.Text.RegularExpressions;
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
    [InlineData("fr", "1 234", "0", "-1 234")]
    [InlineData("es", "1.234", "0", "-1.234")]
    [InlineData("pt", "1.234", "0", "-1.234")]
    [InlineData("ru", "1 234", "0", "-1 234")]
    [InlineData("uk", "1 234", "0", "-1 234")]
    public void Integer_groups_thousands(string lang, string thousand, string zero, string negative)
    {
        using var scope = new CultureScope(lang);
        Assert.Equal(thousand, Spaces(Format.Integer(1234)));
        Assert.Equal(zero, Format.Integer(0));
        Assert.Equal(negative, Spaces(Format.Integer(-1234)));
    }

    [Theory]
    [InlineData("de", "1.234.567")]
    [InlineData("en", "1,234,567")]
    [InlineData("fr", "1 234 567")]
    [InlineData("es", "1.234.567")]
    [InlineData("pt", "1.234.567")]
    [InlineData("ru", "1 234 567")]
    [InlineData("uk", "1 234 567")]
    public void Integer_groups_millions_in_every_language(string lang, string expected)
    {
        using var scope = new CultureScope(lang);
        Assert.Equal(expected, Spaces(Format.Integer(1_234_567)));
    }

    [Theory]
    [InlineData("de", "1,75", "1.234,5", "2")]
    [InlineData("en", "1.75", "1,234.5", "2")]
    [InlineData("fr", "1,75", "1 234,5", "2")]
    [InlineData("es", "1,75", "1.234,5", "2")]
    [InlineData("pt", "1,75", "1.234,5", "2")]
    [InlineData("ru", "1,75", "1 234,5", "2")]
    [InlineData("uk", "1,75", "1 234,5", "2")]
    public void Decimal_uses_the_culture_separators(string lang, string twoDigits, string grouped, string noDigits)
    {
        using var scope = new CultureScope(lang);
        Assert.Equal(twoDigits, Format.Decimal(1.75, 2));
        Assert.Equal(grouped, Spaces(Format.Decimal(1234.5, 1)));
        Assert.Equal(noDigits, Format.Decimal(1.75, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Format.Decimal(1, -1));
    }

    [Theory]
    [InlineData("de", "0,4", "100,0", true)]
    [InlineData("en", "0.4", "100.0", false)]
    [InlineData("fr", "0,4", "100,0", true)]
    [InlineData("es", "0,4", "100,0", true)]
    [InlineData("pt", "0,4", "100,0", true)]
    [InlineData("ru", "0,4", "100,0", true)]
    [InlineData("uk", "0,4", "100,0", true)]
    public void Percent_uses_the_culture_separators_and_spacing(string lang, string small, string whole, bool spaceMayPrecedeSign)
    {
        // Whether a space precedes '%' (de/fr/es/ru "0,4 %", pt/uk "0,4%" on Windows ICU) is ICU-version data: the WASM
        // icudt.dat, a Linux ICU and Windows can differ. The separator and the digits are strict; the comma locales may
        // or may not have one (non-breaking or plain) space before the sign, en never has one.
        using var scope = new CultureScope(lang);
        var sign = spaceMayPrecedeSign ? "[   ]?%" : "%";
        Assert.Matches($"^{Regex.Escape(small)}{sign}$", Format.Percent(0.004, 1));
        Assert.Matches($"^{Regex.Escape(whole)}{sign}$", Format.Percent(1, 1));
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

    [Theory]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("pt")]
    [InlineData("ru")]
    [InlineData("uk")]
    public void Time_is_the_24_hour_clock_in_the_other_languages(string lang)
    {
        using var scope = new CultureScope(lang);
        Assert.Equal("14:05:09", Format.Time(Afternoon));
        Assert.Equal("14:05:09", Format.Time(Afternoon.ToUniversalTime()));
        Assert.Matches("^0?9:05:09$", Format.Time(Afternoon.AddHours(-5))); // with or without a leading zero, never AM/PM
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
    [InlineData("fr")]
    [InlineData("ru")]
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

    [Theory]
    [InlineData("de", -60, "Zuletzt gesehen vor weniger als einer Minute")] // a clock ahead of ours
    [InlineData("de", 30, "Zuletzt gesehen vor weniger als einer Minute")]
    [InlineData("de", 60, "Zuletzt gesehen vor 1 Minute")]
    [InlineData("de", 59 * 60, "Zuletzt gesehen vor 59 Minuten")]
    [InlineData("de", 3600, "Zuletzt gesehen vor 1 Stunde")]
    [InlineData("de", 5 * 3600 + 1800, "Zuletzt gesehen vor 5 Stunden")]
    [InlineData("de", 86400, "Zuletzt gesehen vor 1 Tag")]
    [InlineData("de", 3 * 86400 + 7200, "Zuletzt gesehen vor 3 Tagen")]
    [InlineData("de", 30 * 86400, "Zuletzt gesehen vor 30 Tagen")]
    [InlineData("en", 30, "Last seen less than a minute ago")]
    [InlineData("en", 60, "Last seen 1 minute ago")]
    [InlineData("en", 2 * 60, "Last seen 2 minutes ago")]
    [InlineData("en", 3600, "Last seen 1 hour ago")]
    [InlineData("en", 23 * 3600, "Last seen 23 hours ago")]
    [InlineData("en", 86400, "Last seen 1 day ago")]
    [InlineData("en", 3 * 86400, "Last seen 3 days ago")]
    [InlineData("ru", 30, "Был в сети меньше минуты назад")]
    [InlineData("ru", 60, "Был в сети 1 минуту назад")]
    [InlineData("ru", 3 * 60, "Был в сети 3 минуты назад")]
    [InlineData("ru", 11 * 60, "Был в сети 11 минут назад")]
    [InlineData("ru", 21 * 60, "Был в сети 21 минуту назад")]
    [InlineData("ru", 3600, "Был в сети 1 час назад")]
    [InlineData("ru", 2 * 3600, "Был в сети 2 часа назад")]
    [InlineData("ru", 5 * 3600, "Был в сети 5 часов назад")]
    [InlineData("ru", 86400, "Был в сети 1 день назад")]
    [InlineData("ru", 3 * 86400, "Был в сети 3 дня назад")]
    [InlineData("ru", 12 * 86400, "Был в сети 12 дней назад")]
    [InlineData("ru", 21 * 86400, "Был в сети 21 день назад")]
    [InlineData("ru", 22 * 86400, "Был в сети 22 дня назад")]
    public void Last_seen_is_relative_with_the_languages_plural_forms(string lang, int secondsAgo, string expected)
    {
        var now = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, Format.LastSeen(new I18n(lang), now - TimeSpan.FromSeconds(secondsAgo), now));
    }

    [Theory]
    [InlineData("de", "Zuletzt gesehen am 26.08.2026")]
    [InlineData("en", "Last seen on 8/26/2026")]
    [InlineData("ru", "Был в сети 26.08.2026")]
    public void Last_seen_older_than_a_month_is_the_date(string lang, string expected)
    {
        // Noon local time, so the local date is the 26th on any machine.
        var lastSeen = AtLocal(new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Unspecified));
        var l = new I18n(lang);
        Assert.Equal(expected, Format.LastSeen(l, lastSeen, lastSeen + TimeSpan.FromDays(45)));
        Assert.Equal(expected, Format.LastSeen(l, lastSeen, lastSeen + Format.LastSeenRelativeLimit));
        Assert.StartsWith(lang switch { "de" => "Zuletzt gesehen vor 30 Tagen", "en" => "Last seen 30 days ago", _ => "Был в сети 30 дней назад" }, Format.LastSeen(l, lastSeen, lastSeen + Format.LastSeenRelativeLimit - TimeSpan.FromHours(12)), StringComparison.Ordinal);
    }

    private static DateTimeOffset AtLocal(DateTime wallClock) => new(wallClock, TimeZoneInfo.Local.GetUtcOffset(wallClock));

    // ICU versions differ in the space before AM/PM and before % (U+202F, U+00A0 or a plain space); the spec writes a space.
    private static string Spaces(string text) => text.Replace('\u202F', ' ').Replace('\u00A0', ' ');
}
