using CalDesk.Core.Services;
using Xunit;

namespace CalDesk.Tests;

/// <summary>Tests for <see cref="CalendarDateMath.LocalMidnightToUtc"/>.
/// Proves the method is DST-aware (different offset for DST-active vs DST-inactive
/// midnights in the same zone), handles real DST transition days without throwing,
/// and handles a synthesized spring-forward-at-midnight gap zone (the historical
/// Brazil / Sao Paulo pattern, where local midnight does not exist on the transition day).</summary>
public class CalendarDateMathTests
{
    /// <summary>Eastern US summer midnight must resolve to EDT (UTC-4), not the standard EST offset.
    /// A naive fixed-offset computation (e.g. always applying -5) would fail this.</summary>
    [Fact]
    public void EasternUsSummerMidnight_UsesDstOffset()
    {
        var tz = ResolveEastern();
        // 2024-07-15 is firmly in DST (EDT, UTC-4): local midnight = 04:00 UTC.
        var utc = CalendarDateMath.LocalMidnightToUtc(new DateOnly(2024, 7, 15), tz);
        Assert.Equal(new DateTimeOffset(2024, 7, 15, 4, 0, 0, TimeSpan.Zero), utc);
    }

    /// <summary>Eastern US winter midnight must resolve to EST (UTC-5).</summary>
    [Fact]
    public void EasternUsWinterMidnight_UsesStandardOffset()
    {
        var tz = ResolveEastern();
        // 2024-01-15 is firmly in standard time (EST, UTC-5): local midnight = 05:00 UTC.
        var utc = CalendarDateMath.LocalMidnightToUtc(new DateOnly(2024, 1, 15), tz);
        Assert.Equal(new DateTimeOffset(2024, 1, 15, 5, 0, 0, TimeSpan.Zero), utc);
    }

    /// <summary>Core DST-correctness proof: same wall-clock midnight (00:00 local) on a DST-active
    /// summer day vs a DST-inactive winter day in the SAME zone must resolve to UTC instants whose
    /// implied offsets differ by exactly the DST delta (1h). The returned value is a UTC instant
    /// (offset zero), so we derive the applied local offset as <c>localMidnight - utcInstant</c> and
    /// assert that the derived offsets are EDT/EST and differ by 1h. A fixed-offset helper would
    /// produce identical derived offsets and fail this.</summary>
    [Fact]
    public void EasternUs_DstActiveVsInactive_MidnightOffsetsDifferByDstDelta()
    {
        var tz = ResolveEastern();
        var summerLocal = new DateTime(2024, 7, 15);
        var summerUtc = CalendarDateMath.LocalMidnightToUtc(new DateOnly(2024, 7, 15), tz).UtcDateTime;
        var summerAppliedOffset = summerLocal - summerUtc; // west-of-UTC → negative

        var winterLocal = new DateTime(2024, 1, 15);
        var winterUtc = CalendarDateMath.LocalMidnightToUtc(new DateOnly(2024, 1, 15), tz).UtcDateTime;
        var winterAppliedOffset = winterLocal - winterUtc;

        Assert.Equal(TimeSpan.FromHours(-4), summerAppliedOffset); // EDT
        Assert.Equal(TimeSpan.FromHours(-5), winterAppliedOffset); // EST
        Assert.Equal(TimeSpan.FromHours(1), summerAppliedOffset - winterAppliedOffset); // DST delta
    }

    /// <summary>Fixed-offset zone (UTC+08:00, e.g. Asia/Shanghai which does not observe DST):
    /// local midnight must map to 16:00Z of the previous day, year-round. The same UTC instant
    /// for January and July proves no spurious DST delta is applied.</summary>
    [Fact]
    public void FixedOffsetZone_MidnightIsConsistentYearRound()
    {
        var tz = ResolveFixedPlus8();
        var jan = CalendarDateMath.LocalMidnightToUtc(new DateOnly(2024, 1, 15), tz);
        var jul = CalendarDateMath.LocalMidnightToUtc(new DateOnly(2024, 7, 15), tz);
        Assert.Equal(new DateTimeOffset(2024, 1, 14, 16, 0, 0, TimeSpan.Zero), jan);
        Assert.Equal(new DateTimeOffset(2024, 7, 14, 16, 0, 0, TimeSpan.Zero), jul);
    }

    /// <summary>Real spring-forward transition day in US Eastern (2024-03-10, transitions at 02:00 local).
    /// Midnight 00:00 is BEFORE the transition, so it is a valid time and must resolve to EST (UTC-5)
    /// = 05:00Z. This proves the method does not throw on the transition calendar day.</summary>
    [Fact]
    public void EasternUs_SpringForwardTransitionDay_MidnightDoesNotThrow()
    {
        var tz = ResolveEastern();
        var utc = CalendarDateMath.LocalMidnightToUtc(new DateOnly(2024, 3, 10), tz);
        // Midnight is pre-transition (gap is 02:00→03:00), so still EST = 05:00Z.
        Assert.Equal(new DateTimeOffset(2024, 3, 10, 5, 0, 0, TimeSpan.Zero), utc);
    }

    /// <summary>T2-C1 explicit scenario: a zone whose DST spring-forward transition occurs AT local
    /// midnight, so that local 00:00 on the transition day does not exist. Built synthetically so the
    /// test is deterministic across platforms (does not depend on system tzdata containing historical
    /// America/Sao_Paulo midnight transitions). The method must not throw and must return a UTC instant
    /// near the base-offset midnight (within the DST delta).</summary>
    [Fact]
    public void MidnightSpringForwardGap_DoesNotThrowAndReturnsReasonableUtc()
    {
        // Historical Brazil pattern: base UTC-3, spring forward at 00:00 on 3rd Sunday of October,
        // fall back at 00:00 on 3rd Sunday of February. DST delta = +1h.
        var tz = TimeZoneInfo.CreateCustomTimeZone(
            "Custom/MidnightGap",
            baseUtcOffset: TimeSpan.FromHours(-3),
            displayName: "(UTC-03:00) Custom Midnight Gap",
            standardDisplayName: "Standard",
            daylightDisplayName: "Daylight",
            adjustmentRules:
            [
                TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                    dateStart: new DateTime(2000, 1, 1),
                    dateEnd: DateTime.MaxValue.Date,
                    daylightDelta: TimeSpan.FromHours(1),
                    daylightTransitionStart: TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                        timeOfDay: new DateTime(1, 1, 1, 0, 0, 0), month: 10, week: 3, dayOfWeek: DayOfWeek.Sunday),
                    daylightTransitionEnd: TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                        timeOfDay: new DateTime(1, 1, 1, 0, 0, 0), month: 2, week: 3, dayOfWeek: DayOfWeek.Sunday)),
            ]);

        // 3rd Sunday of October 2018 = 2018-10-21. The clock jumps 00:00 → 01:00 at midnight, so
        // local 00:00 on this date is an invalid (skipped) time — the canonical DST "missing hour".
        var gapDate = new DateOnly(2018, 10, 21);
        var gapLocal = new DateTime(2018, 10, 21, 0, 0, 0, DateTimeKind.Unspecified);
        Assert.True(tz.IsInvalidTime(gapLocal), "test setup: midnight must fall in the spring-forward gap");

        // Must NOT throw (T2-C1) and must return a UTC instant within 2h of base-offset midnight
        // (00:00 local at base -3 = 03:00Z; with DST applied = 04:00Z — either is acceptable).
        var utc = CalendarDateMath.LocalMidnightToUtc(gapDate, tz);
        var baseMidnightUtc = new DateTimeOffset(2018, 10, 21, 3, 0, 0, TimeSpan.Zero);
        Assert.True(TimeSpan.FromHours(2) >= TimeSpan.FromTicks(Math.Abs((utc - baseMidnightUtc).Ticks)));
        Assert.Equal(TimeSpan.Zero, utc.Offset); // returned value is a UTC instant
    }

    private static TimeZoneInfo ResolveEastern()
    {
        // IANA name works on both Linux (.NET reads /usr/share/zoneinfo) and Windows (.NET 6+
        // auto-converts IANA → Windows zone id). Fall back to the Windows id if lookup fails.
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
    }

    private static TimeZoneInfo ResolveFixedPlus8()
    {
        // Asia/Shanghai is UTC+8 year-round (no DST since 1991). IANA on Linux, Windows fallback.
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
    }
}
