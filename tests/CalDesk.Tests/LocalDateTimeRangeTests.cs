using CalDesk.Core.Services;
using Xunit;

namespace CalDesk.Tests;

/// <summary>
/// Storage query windows must be local-midnight → local-midnight, not fixed 7×24h UTC spans.
/// Across DST transitions a local week is 167h (spring) or 169h (fall), not 168h.
/// </summary>
public sealed class LocalDateTimeRangeTests
{
    private static TimeZoneInfo Eastern()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }

    [Fact]
    public void WeekStorageRange_FallBackWeek_Is169HoursNot168()
    {
        // US Eastern 2025: clocks fall back Sunday 2025-11-02 → week Mon 10-27 .. Mon 11-03 is 169 local hours.
        var tz = Eastern();
        var weekStart = new DateOnly(2025, 10, 27);

        var (rangeStart, rangeEnd) = LocalDateTimeRange.WeekStorageRange(weekStart, tz);

        Assert.Equal(TimeSpan.FromHours(169), rangeEnd - rangeStart);
        Assert.NotEqual(TimeSpan.FromDays(7), rangeEnd - rangeStart);
        // Both bounds are local midnights converted independently.
        Assert.Equal(LocalDateTimeRange.LocalMidnightToUtc(weekStart, tz), rangeStart);
        Assert.Equal(LocalDateTimeRange.LocalMidnightToUtc(weekStart.AddDays(7), tz), rangeEnd);
    }

    [Fact]
    public void WeekStorageRange_SpringForwardWeek_Is167HoursNot168()
    {
        // US Eastern 2025: clocks spring forward Sunday 2025-03-09 → week Mon 03-03 .. Mon 03-10 is 167 local hours.
        var tz = Eastern();
        var weekStart = new DateOnly(2025, 3, 3);

        var (rangeStart, rangeEnd) = LocalDateTimeRange.WeekStorageRange(weekStart, tz);

        Assert.Equal(TimeSpan.FromHours(167), rangeEnd - rangeStart);
        Assert.NotEqual(TimeSpan.FromDays(7), rangeEnd - rangeStart);
    }

    [Fact]
    public void WeekStorageRange_NonDstWeek_IsExactly168Hours()
    {
        var tz = Eastern();
        var weekStart = new DateOnly(2025, 7, 7); // mid-summer, no transition

        var (rangeStart, rangeEnd) = LocalDateTimeRange.WeekStorageRange(weekStart, tz);

        Assert.Equal(TimeSpan.FromDays(7), rangeEnd - rangeStart);
    }
}
