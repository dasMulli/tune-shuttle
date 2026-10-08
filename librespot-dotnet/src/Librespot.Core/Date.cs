// Port of core/src/date.rs.
using System.Globalization;
using DateMessage = Spotify.Metadata.Date;

namespace Librespot.Core;

/// <summary>A UTC timestamp (upstream wraps <c>time::OffsetDateTime</c>; here <see cref="DateTimeOffset"/>).</summary>
public readonly record struct Date(DateTimeOffset Value) : IComparable<Date>
{
    public long AsTimestampMs() => Value.ToUnixTimeMilliseconds();

    public static Date FromTimestampMs(long timestamp)
    {
        try
        {
            return new Date(DateTimeOffset.FromUnixTimeMilliseconds(timestamp));
        }
        catch (ArgumentOutOfRangeException e)
        {
            throw LibrespotException.OutOfRange(e);
        }
    }

    public DateTimeOffset AsUtc() => Value.ToUniversalTime();

    /// <summary>Upstream <c>from_utc(PrimitiveDateTime)</c>: interprets an unspecified-kind value as UTC.</summary>
    public static Date FromUtc(DateTime dateTime) => new(new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)));

    public static Date NowUtc() => new(DateTimeOffset.UtcNow);

    public static Date FromIso8601(string input)
    {
        if (!DateTimeOffset.TryParse(input, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            throw LibrespotException.FailedPrecondition($"could not parse ISO 8601 date: {input}");
        }

        return new Date(parsed);
    }

    /// <summary>Upstream <c>TryFrom&lt;&amp;protocol::metadata::Date&gt;</c>.</summary>
    public static Date From(DateMessage msg)
    {
        // Some metadata contains a year, but no month. In that case just set January.
        var month = msg.HasMonth ? msg.Month : 1;

        // Having no day will work, but may be unexpected: it will imply the last day
        // of the month before. So prevent that, and just set day 1.
        var day = msg.HasDay ? msg.Day : 1;

        try
        {
            var dt = new DateTime(msg.Year, month, day, msg.Hour, msg.Minute, 0, DateTimeKind.Utc);
            return new Date(new DateTimeOffset(dt));
        }
        catch (ArgumentOutOfRangeException e)
        {
            throw LibrespotException.OutOfRange(e);
        }
    }

    public static Date From(DateTimeOffset dateTime) => new(dateTime);

    public int CompareTo(Date other) => Value.CompareTo(other.Value);
}
