namespace CalDesk.Core.Services;

/// <summary>
/// Pure helpers for converting local calendar days to UTC instants for storage queries.
/// Bounds must be computed as local midnights (not fixed-hour offsets) so DST transitions
/// produce the correct 167h / 169h week windows.
/// </summary>
public static class LocalDateTimeRange
{
    /// <summary>
    /// Half-open storage query window for a week starting at local midnight of
    /// <paramref name="weekStart"/> through local midnight of <c>weekStart + 7 days</c>.
    /// Matches <see cref="CalendarViewService.BuildWeek"/> day boundaries under DST.
    /// </summary>
    public static (DateTimeOffset RangeStartUtc, DateTimeOffset RangeEndUtc) WeekStorageRange(
        DateOnly weekStart,
        TimeZoneInfo tz)
    {
        // Delegate to CalendarDateMath.LocalMidnightToUtc so there is a single DST-safe
        // definition in the codebase (T2-C2). Each bound is converted independently so a
        // spring-forward week = 167h and a fall-back week = 169h (never a fixed 168h span).
        var rangeStart = CalendarDateMath.LocalMidnightToUtc(weekStart, tz);
        var rangeEnd = CalendarDateMath.LocalMidnightToUtc(weekStart.AddDays(7), tz);
        return (rangeStart, rangeEnd);
    }
}
