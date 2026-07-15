using CalDesk.CalDav.Calendar;
using CalDesk.CalDav.Recurrence;
using CalDesk.Core.Models;
using CalDesk.Core.Recurrence;
using Xunit;

namespace CalDesk.Tests;

public sealed class RecurrenceExpanderTests
{
    private readonly IRecurrenceExpander _expander = new IcalRecurrenceExpander();

    private static CalendarEvent MakeRecurring(string rrule)
    {
        var start = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var evt = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Uid = "rec-1",
            Summary = "Recurring",
            StartUtc = start,
            EndUtc = start.AddHours(1),
        };
        evt.RecurrenceRule = rrule;
        evt.ICalendarData = IcalMapper.Serialize(evt);
        return evt;
    }

    [Fact]
    public void DailyExpandsAcrossWeek()
    {
        var evt = MakeRecurring("FREQ=DAILY;INTERVAL=1");
        var rangeStart = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);
        var rangeEnd = rangeStart.AddDays(7);

        var occs = _expander.Expand(evt, new CalendarInfo(), rangeStart, rangeEnd).ToList();

        Assert.True(occs.Count >= 7, $"expected >=7 daily occurrences, got {occs.Count}");
        // Each occurrence on a distinct day.
        var days = occs.Select(o => o.StartUtc.Date).Distinct().Count();
        Assert.Equal(occs.Count, days);
    }

    [Fact]
    public void WeeklyByDayExpandsOnlyMatchingDays()
    {
        var evt = MakeRecurring("FREQ=WEEKLY;INTERVAL=1;BYDAY=MO,WE,FR");
        var rangeStart = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero); // Monday
        var rangeEnd = rangeStart.AddDays(7);

        var occs = _expander.Expand(evt, new CalendarInfo(), rangeStart, rangeEnd).ToList();

        // Mon (13), Wed (15), Fri (17) => 3 within the week.
        Assert.Equal(3, occs.Count);
    }

    [Fact]
    public void NonRecurringSingleEventYieldsOneWhenOverlapping()
    {
        var evt = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Uid = "s1",
            Summary = "Single",
            StartUtc = new DateTimeOffset(2026, 7, 15, 14, 0, 0, TimeSpan.Zero),
            EndUtc = new DateTimeOffset(2026, 7, 15, 15, 0, 0, TimeSpan.Zero),
        };

        var occs = _expander.Expand(evt, new CalendarInfo(),
            new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero)).ToList();

        Assert.Single(occs);
    }

    // ---- Issue 6: range-overlap for long events ----------------------------

    [Fact]
    public void LongEvent_Starting_Before_Range_Is_Included_When_Overlapping()
    {
        // A single 48-hour event starting the day before the range. The naive implementation
        // asks Ical.Net for occurrences starting at rangeStart, dropping anything that started
        // earlier — but this event still overlaps the range and must be returned.
        var start = new DateTimeOffset(2026, 7, 12, 9, 0, 0, TimeSpan.Zero);
        var evt = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Uid = "long-single",
            Summary = "48-hour event",
            StartUtc = start,
            EndUtc = start.AddHours(48), // 2026-07-14 09:00
        };
        evt.ICalendarData = IcalMapper.Serialize(evt);

        var rangeStart = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);
        var rangeEnd = rangeStart.AddDays(7);

        var occs = _expander.Expand(evt, new CalendarInfo(), rangeStart, rangeEnd).ToList();

        var occ = Assert.Single(occs);
        Assert.True(occ.StartUtc < rangeStart, "occurrence should start before range");
        Assert.True(occ.EndUtc > rangeStart, "occurrence should still overlap range");
        Assert.Equal(start.AddHours(48), occ.EndUtc);
    }

    [Fact]
    public void Recurring_LongEvent_Occurrences_Overlapping_Range_Are_Returned_With_Correct_EndTime()
    {
        // A daily 48-hour event whose first occurrence starts the day before the range. Each
        // overlapping occurrence must be returned with its correct end time (start + 48h).
        var firstStart = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
        var evt = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Uid = "long-recur",
            Summary = "48h-daily",
            StartUtc = firstStart,
            EndUtc = firstStart.AddHours(48),
        };
        evt.RecurrenceRule = "FREQ=DAILY;INTERVAL=1;COUNT=5";
        evt.ICalendarData = IcalMapper.Serialize(evt);

        var rangeStart = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);
        var rangeEnd = rangeStart.AddDays(7); // up to 2026-07-20

        var occs = _expander.Expand(evt, new CalendarInfo(), rangeStart, rangeEnd).ToList();

        // Occurrence on 07-12 (overlaps range via [07-12, 07-14)) — must be present.
        Assert.Contains(occs, o => o.StartUtc == firstStart && o.EndUtc == firstStart.AddHours(48));
        // Each overlapping occurrence must carry the right end time (no zero-length corruption).
        Assert.All(occs, o => Assert.Equal(o.StartUtc.AddHours(48), o.EndUtc));
        // And no occurrence should end at-or-before rangeStart (would mean it doesn't overlap).
        Assert.All(occs, o => Assert.True(o.EndUtc > rangeStart));
        // No occurrence starts on/after rangeEnd.
        Assert.All(occs, o => Assert.True(o.StartUtc < rangeEnd));
    }
}
