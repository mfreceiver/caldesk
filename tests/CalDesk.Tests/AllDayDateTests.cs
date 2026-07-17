using CalDesk.Core;
using Xunit;

namespace CalDesk.Tests;

/// <summary>
/// All-day civil DATE ↔ UTC instant mapping in an explicit time zone
/// (IcalMapper uses TimeZoneInfo.Local via these helpers).
/// </summary>
public sealed class AllDayDateTests
{
    private static readonly TimeZoneInfo Plus08 = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
    private static readonly TimeZoneInfo Minus05 = ResolveEastern();

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

    [Theory]
    [MemberData(nameof(Zones))]
    public void RoundTrip_LocalDate_Preserves_Civil_Date(TimeZoneInfo tz)
    {
        var date = new DateOnly(2026, 7, 15);
        var utc = AllDayDate.ToUtcInstant(date, tz);
        var back = AllDayDate.ToLocalDate(utc, tz);
        Assert.Equal(date, back);

        // utc.Date is wrong whenever local midnight is on the other side of UTC midnight
        // (e.g. +08: Jul 15 local → Jul 14 16:00Z). EDT summer midnight stays same UTC day.
        var utcDate = DateOnly.FromDateTime(utc.UtcDateTime);
        if (utcDate != date)
        {
            Assert.Equal(date, back); // already asserted; documents the utc.Date trap
            Assert.NotEqual(date, utcDate);
        }
    }

    [Fact]
    public void Plus08_Local_Midnight_Is_Previous_Utc_Afternoon()
    {
        var utc = AllDayDate.ToUtcInstant(new DateOnly(2026, 7, 15), Plus08);
        Assert.Equal(new DateTimeOffset(2026, 7, 14, 16, 0, 0, TimeSpan.Zero), utc.ToUniversalTime());
        Assert.Equal(new DateOnly(2026, 7, 15), AllDayDate.ToLocalDate(utc, Plus08));
    }

    [Fact]
    public void Minus05_Summer_Local_Midnight_Is_Next_Utc_Morning()
    {
        // 2026-07-15 00:00 America/New_York (EDT, UTC-4) = 2026-07-15 04:00Z
        var utc = AllDayDate.ToUtcInstant(new DateOnly(2026, 7, 15), Minus05);
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 4, 0, 0, TimeSpan.Zero), utc.ToUniversalTime());
        Assert.Equal(new DateOnly(2026, 7, 15), AllDayDate.ToLocalDate(utc, Minus05));
    }

    public static IEnumerable<object[]> Zones()
    {
        yield return new object[] { Plus08 };
        yield return new object[] { Minus05 };
    }
}
