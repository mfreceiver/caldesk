namespace CalDesk.Core;

/// <summary>
/// Civil DATE ↔ UTC instant for all-day events in a specific time zone.
/// DATE values must be derived from the <em>local</em> calendar day of the stored
/// UTC instant (not <c>utc.Date</c>), and a server DATE maps to that local midnight.
/// </summary>
public static class AllDayDate
{
    /// <summary>Local midnight of <paramref name="date"/> in <paramref name="tz"/> as a UTC instant.</summary>
    public static DateTimeOffset ToUtcInstant(DateOnly date, TimeZoneInfo tz)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // Midnight is never in a spring-forward gap for IANA zones used by calendars.
        return new DateTimeOffset(local, tz.GetUtcOffset(local));
    }

    /// <summary>Civil local date of an all-day UTC instant in <paramref name="tz"/>.</summary>
    public static DateOnly ToLocalDate(DateTimeOffset utcInstant, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTime(utcInstant, tz);
        return DateOnly.FromDateTime(local.DateTime);
    }
}
