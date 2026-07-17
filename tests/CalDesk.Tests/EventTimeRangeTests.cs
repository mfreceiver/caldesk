using CalDesk.Core;
using Xunit;

namespace CalDesk.Tests;

/// <summary>
/// Pure save-range computation for QuickAdd: all-day exclusive end, overnight retry,
/// explicit earlier EndDate reject, DST spring-gap reject.
/// </summary>
public sealed class EventTimeRangeTests
{
    private static readonly TimeZoneInfo Shanghai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
    private static readonly TimeZoneInfo Eastern = ResolveEastern();

    private static TimeZoneInfo ResolveEastern()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }

    [Fact]
    public void AllDay_SingleDay_ExclusiveEnd_Is_Next_Local_Midnight()
    {
        var d = new DateOnly(2026, 7, 15);
        var r = EventTimeRange.TryCompute(
            isAllDay: true,
            startDate: d,
            endDate: d,
            startTimeText: null,
            endTimeText: null,
            tz: Shanghai);

        Assert.Equal(EventTimeRangeStatus.Ok, r.Status);
        Assert.Equal(LocalMidnightUtc(d, Shanghai), r.StartUtc);
        Assert.Equal(LocalMidnightUtc(d.AddDays(1), Shanghai), r.EndUtc);
        // Not a fixed +24h from start when expressed as AddDays on the UTC instant alone is fine in +08,
        // but exclusive end must equal next local midnight (DST-correct construction).
        Assert.Equal(d.AddDays(1), DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(r.EndUtc, Shanghai).DateTime));
    }

    [Fact]
    public void AllDay_MultiDay_Span_Uses_Next_Local_Midnight_After_Inclusive_End()
    {
        var start = new DateOnly(2026, 7, 15);
        var end = new DateOnly(2026, 7, 17);
        var r = EventTimeRange.TryCompute(true, start, end, null, null, Shanghai);

        Assert.Equal(EventTimeRangeStatus.Ok, r.Status);
        Assert.Equal(LocalMidnightUtc(start, Shanghai), r.StartUtc);
        Assert.Equal(LocalMidnightUtc(end.AddDays(1), Shanghai), r.EndUtc);
    }

    [Fact]
    public void AllDay_EndDate_Before_StartDate_Is_Rejected()
    {
        var r = EventTimeRange.TryCompute(
            true,
            new DateOnly(2026, 7, 16),
            new DateOnly(2026, 7, 15),
            null,
            null,
            Shanghai);

        Assert.Equal(EventTimeRangeStatus.RangeInvalid, r.Status);
        Assert.True(r.RangeInvalid);
    }

    [Fact]
    public void Timed_SameDay_Overnight_Pushes_End_One_Day()
    {
        var d = new DateOnly(2026, 7, 15);
        var r = EventTimeRange.TryCompute(
            false,
            d,
            d,
            "22:00",
            "06:00",
            Shanghai);

        Assert.Equal(EventTimeRangeStatus.Ok, r.Status);
        Assert.Equal(d.AddDays(1), r.AdjustedEndDate);
        Assert.True(r.EndUtc > r.StartUtc);

        var endLocal = TimeZoneInfo.ConvertTime(r.EndUtc, Shanghai);
        Assert.Equal(new DateOnly(2026, 7, 16), DateOnly.FromDateTime(endLocal.DateTime));
        Assert.Equal(6, endLocal.Hour);
    }

    [Fact]
    public void Timed_EndDate_Before_StartDate_Is_Rejected_Not_Silently_Rewritten()
    {
        var r = EventTimeRange.TryCompute(
            false,
            new DateOnly(2026, 7, 16),
            new DateOnly(2026, 7, 15),
            "10:00",
            "11:00",
            Shanghai);

        Assert.Equal(EventTimeRangeStatus.RangeInvalid, r.Status);
        Assert.True(r.RangeInvalid);
        Assert.Null(r.AdjustedEndDate);
    }

    [Fact]
    public void Timed_MultiDay_Uses_EndDate_As_Is()
    {
        var r = EventTimeRange.TryCompute(
            false,
            new DateOnly(2026, 7, 15),
            new DateOnly(2026, 7, 17),
            "09:00",
            "10:00",
            Shanghai);

        Assert.Equal(EventTimeRangeStatus.Ok, r.Status);
        Assert.Null(r.AdjustedEndDate);

        var endLocal = TimeZoneInfo.ConvertTime(r.EndUtc, Shanghai);
        Assert.Equal(new DateOnly(2026, 7, 17), DateOnly.FromDateTime(endLocal.DateTime));
    }

    [Fact]
    public void Timed_SpringForward_Gap_Is_Rejected()
    {
        // US Eastern 2026-03-08: clocks jump 02:00 → 03:00; 02:30 is invalid.
        var d = new DateOnly(2026, 3, 8);
        var r = EventTimeRange.TryCompute(
            false,
            d,
            d,
            "02:30",
            "04:00",
            Eastern);

        Assert.Equal(EventTimeRangeStatus.InvalidLocalTime, r.Status);
        Assert.True(r.StartTimeInvalid);
    }

    [Fact]
    public void Timed_Ambiguous_FallBack_Does_Not_Throw()
    {
        // US Eastern 2026-11-01: 01:30 occurs twice; GetUtcOffset picks one (standard/first).
        var d = new DateOnly(2026, 11, 1);
        var r = EventTimeRange.TryCompute(
            false,
            d,
            d,
            "01:30",
            "02:30",
            Eastern);

        Assert.Equal(EventTimeRangeStatus.Ok, r.Status);
        Assert.True(r.EndUtc > r.StartUtc);
    }

    [Fact]
    public void Timed_Bad_TimeText_Reports_Parse_Failure()
    {
        var d = new DateOnly(2026, 7, 15);
        var r = EventTimeRange.TryCompute(false, d, d, "99:99", "10:00", Shanghai);

        Assert.Equal(EventTimeRangeStatus.TimeParseFailed, r.Status);
        Assert.True(r.StartTimeInvalid);
        Assert.False(r.EndTimeInvalid);
    }

    private static DateTimeOffset LocalMidnightUtc(DateOnly date, TimeZoneInfo tz)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, tz.GetUtcOffset(local));
    }
}
