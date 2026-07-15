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
}
