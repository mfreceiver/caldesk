using CalDesk.Core.Services;

namespace CalDesk.Core;

/// <summary>
/// Civil DATE ↔ UTC instant for all-day events in a specific time zone.
/// DATE values must be derived from the <em>local</em> calendar day of the stored
/// UTC instant (not <c>utc.Date</c>), and a server DATE maps to that local midnight.
/// </summary>
public static class AllDayDate
{
    /// <summary>Local midnight of <paramref name="date"/> in <paramref name="tz"/> as a UTC instant
    /// (zero offset). Delegates to <see cref="CalendarDateMath.LocalMidnightToUtc"/> so DST-safety
    /// (spring-forward gap at midnight) and per-date offset selection live in one place (T2-C2).
    /// <para>For valid local midnights the returned instant is identical to the previous inline
    /// <c>new DateTimeOffset(local, tz.GetUtcOffset(local))</c> computation; only the rare midnight
    /// spring-forward gap (historical America/Sao_Paulo pattern) changes behaviour from
    /// "depends on platform GetUtcOffset direction" to "guaranteed no-throw + reasonable UTC".</para>
    /// <para>Returned value is offset-zero-flavoured; the previous inline form returned a
    /// local-offset-flavoured <see cref="DateTimeOffset"/>. The two represent the same instant in
    /// time and all current callers are offset-agnostic (<c>.UtcDateTime</c>,
    /// <c>.ToUniversalTime()</c>, <c>TimeZoneInfo.ConvertTime</c>, or instant-only equality).</para></summary>
    public static DateTimeOffset ToUtcInstant(DateOnly date, TimeZoneInfo tz)
        => CalendarDateMath.LocalMidnightToUtc(date, tz);

    /// <summary>Civil local date of an all-day UTC instant in <paramref name="tz"/>.</summary>
    public static DateOnly ToLocalDate(DateTimeOffset utcInstant, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTime(utcInstant, tz);
        return DateOnly.FromDateTime(local.DateTime);
    }
}
