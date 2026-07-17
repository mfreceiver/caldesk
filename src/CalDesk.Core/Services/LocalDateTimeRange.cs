namespace CalDesk.Core.Services;

/// <summary>
/// Pure helpers for converting local calendar days to UTC instants for storage queries.
/// Bounds must be computed as local midnights (not fixed-hour offsets) so DST transitions
/// produce the correct 167h / 169h week windows.
/// </summary>
public static class LocalDateTimeRange
{
    /// <summary>
    /// UTC instant of local midnight for <paramref name="date"/> in <paramref name="tz"/>.
    /// </summary>
    public static DateTimeOffset LocalMidnightToUtc(DateOnly date, TimeZoneInfo tz)
    {
        var local = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, tz.GetUtcOffset(local)).UtcDateTime;
    }

    /// <summary>
    /// Half-open storage query window for a week starting at local midnight of
    /// <paramref name="weekStart"/> through local midnight of <c>weekStart + 7 days</c>.
    /// Matches <see cref="CalendarViewService.BuildWeek"/> day boundaries under DST.
    /// </summary>
    public static (DateTimeOffset RangeStartUtc, DateTimeOffset RangeEndUtc) WeekStorageRange(
        DateOnly weekStart,
        TimeZoneInfo tz)
    {
        var rangeStart = LocalMidnightToUtc(weekStart, tz);
        // Local midnight of weekStart+7 — not rangeStart.AddDays(7) (always 168h; wrong across DST).
        var rangeEnd = LocalMidnightToUtc(weekStart.AddDays(7), tz);
        return (rangeStart, rangeEnd);
    }
}
