using CalDesk.Core.Models;
using CalDesk.Core.Recurrence;
using CalDesk.Core.Services;
using Xunit;

namespace CalDesk.Tests;

public sealed class CalendarViewServiceTests
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.Utc;
    private static readonly DateOnly Monday = new(2026, 7, 13);      // Mon
    private static readonly DateOnly Today = new(2026, 7, 15);       // Wed
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Treats each event as a single occurrence (used to test grouping / hiding / ongoing).</summary>
    private sealed class StubExpander : IRecurrenceExpander
    {
        public IEnumerable<EventOccurrence> Expand(CalendarEvent e, CalendarInfo c, DateTimeOffset rs, DateTimeOffset re)
        {
            if (e.StartUtc < re && e.EndUtc > rs)
            {
                yield return new EventOccurrence { Source = e, Calendar = c, StartUtc = e.StartUtc, EndUtc = e.EndUtc };
            }
        }
    }

    private static CalendarEvent Ev(int day, int h0, int m0, int h1, int m1, bool allDay = false, string summary = "x", Guid calId = default)
    {
        var s = new DateTimeOffset(2026, 7, day, h0, m0, 0, TimeSpan.Zero);
        var e = new DateTimeOffset(2026, 7, day, h1, m1, 0, TimeSpan.Zero);
        return new CalendarEvent { Id = Guid.NewGuid(), CalendarId = calId, Summary = summary, StartUtc = s, EndUtc = e, IsAllDay = allDay };
    }

    [Fact]
    public void GroupsByDay_SeparatesAllDay_SortsByTime()
    {
        var calId = Guid.NewGuid();
        var calendars = new Dictionary<Guid, CalendarInfo> { [calId] = new() { Color = "#ff0000" } };
        var events = new[]
        {
            Ev(13, 9, 0, 10, 0, summary: "Mon", calId: calId),
            Ev(14, 9, 0, 10, 0, summary: "Tue", calId: calId),
            Ev(15, 9, 0, 11, 0, summary: "WedOngoing", calId: calId),
            Ev(16, 0, 0, 0, 0, allDay: true, summary: "ThuAllDay", calId: calId), // Thu 00:00 .. Fri 00:00
            Ev(16, 14, 0, 15, 0, summary: "Thu14", calId: calId),
        };
        // fix all-day end to next day
        events[3].EndUtc = new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero);

        var svc = new CalendarViewService(new StubExpander());
        var view = svc.BuildWeek(events, calendars, Monday, Tz, Today, Now);

        Assert.Equal(7, view.Days.Count);
        Assert.Equal("周一 13", view.Days[0].Header);
        Assert.Single(view.Days[0].Timed);  // past Monday still shown
        Assert.Equal("Mon", view.Days[0].Timed[0].Summary);
        Assert.Single(view.Days[1].Timed);  // past Tuesday still shown
        Assert.Equal("Tue", view.Days[1].Timed[0].Summary);

        Assert.True(view.Days[2].IsToday);
        Assert.Single(view.Days[2].Timed);
        Assert.True(view.Days[2].Timed[0].IsOngoing);
        Assert.Equal("09:00", view.Days[2].Timed[0].TimeLabel);
        Assert.Equal("#ff0000", view.Days[2].Timed[0].Color);

        Assert.Single(view.Days[3].AllDay);
        Assert.Equal("全天", view.Days[3].AllDay[0].TimeLabel);
        Assert.Single(view.Days[3].Timed);
        Assert.Equal("14:00", view.Days[3].Timed[0].TimeLabel);

        Assert.True(view.HasAny);
    }

    /// <summary>
    /// Week view must show the full visible week: past days and earlier-today events that already
    /// ended still render. Only "今天" / IsOngoing styling distinguishes present from past.
    /// </summary>
    [Fact]
    public void BuildWeek_IncludesPastDaysAndEarlierTodayEndedEvents()
    {
        var calId = Guid.NewGuid();
        var calendars = new Dictionary<Guid, CalendarInfo> { [calId] = new() { Color = "#abc" } };
        // Now = Wed 10:00. Include yesterday + this morning (already ended) + ongoing + future.
        var events = new[]
        {
            Ev(14, 15, 0, 16, 0, summary: "Yesterday", calId: calId),       // Tue — fully past day
            Ev(15, 7, 0, 8, 0, summary: "EarlierToday", calId: calId),      // Wed 07–08, ended 2h ago
            Ev(15, 9, 0, 11, 0, summary: "Ongoing", calId: calId),          // Wed 09–11, ongoing at 10:00
            Ev(16, 14, 0, 15, 0, summary: "Tomorrow", calId: calId),
        };

        var svc = new CalendarViewService(new StubExpander());
        var view = svc.BuildWeek(events, calendars, Monday, Tz, Today, Now);

        Assert.Single(view.Days[1].Timed);
        Assert.Equal("Yesterday", view.Days[1].Timed[0].Summary);
        Assert.False(view.Days[1].Timed[0].IsOngoing);

        Assert.Equal(2, view.Days[2].Timed.Count);
        Assert.Equal("EarlierToday", view.Days[2].Timed[0].Summary);
        Assert.False(view.Days[2].Timed[0].IsOngoing);
        Assert.Equal("Ongoing", view.Days[2].Timed[1].Summary);
        Assert.True(view.Days[2].Timed[1].IsOngoing);
        Assert.True(view.Days[2].IsToday);

        Assert.Single(view.Days[3].Timed);
        Assert.Equal("Tomorrow", view.Days[3].Timed[0].Summary);
    }

    [Fact]
    public void EmptyWeekHasNoItems()
    {
        var svc = new CalendarViewService(new StubExpander());
        var view = svc.BuildWeek(Array.Empty<CalendarEvent>(), new Dictionary<Guid, CalendarInfo>(), Monday, Tz, Today, Now);
        Assert.False(view.HasAny);
        Assert.All(view.Days, d => Assert.True(d.IsEmpty));
    }

    // ---- Month view ----

    private static MonthDay CellAt(MonthView v, int month, int day)
        => v.Weeks.SelectMany(w => w.Days).First(d => d.Date == new DateOnly(2026, month, day));

    [Fact]
    public void MonthGrid_AlignsToMonday_AndMarksSpilloverAndToday()
    {
        // July 2026: 1st is a Wednesday -> grid opens Mon Jun 29.
        var calId = Guid.NewGuid();
        var calendars = new Dictionary<Guid, CalendarInfo> { [calId] = new() };
        var svc = new CalendarViewService(new StubExpander());

        var view = svc.BuildMonth(Array.Empty<CalendarEvent>(), calendars, 2026, 7, Tz, Today, Now);

        Assert.Equal("2026年7月", view.Title);
        Assert.Equal(7, view.WeekdayHeaders.Count);
        Assert.Equal("周一", view.WeekdayHeaders[0]);

        var first = view.Weeks[0].Days[0];
        Assert.Equal(new DateOnly(2026, 6, 29), first.Date);  // Monday before Jul 1
        Assert.False(first.IsInMonth);                        // spillover from June
        Assert.Equal(29, first.Day);

        var todayCell = CellAt(view, 7, 15);
        Assert.True(todayCell.IsToday);
        Assert.True(todayCell.IsInMonth);

        // Weekend flag on a Saturday (Jul 18 2026) and Sunday (Jul 19).
        Assert.True(CellAt(view, 7, 18).IsWeekend);
        Assert.True(CellAt(view, 7, 19).IsWeekend);
        Assert.False(CellAt(view, 7, 16).IsWeekend);
    }

    [Fact]
    public void MonthView_DoesNotHidePast_AllDayFirst_PreviewsCapped()
    {
        var calId = Guid.NewGuid();
        var calendars = new Dictionary<Guid, CalendarInfo> { [calId] = new() { Color = "#00f" } };
        var events = new[]
        {
            Ev(1, 9, 0, 10, 0, summary: "EarlyPast", calId: calId),   // Jul 1, before today
            Ev(20, 0, 0, 0, 0, allDay: true, summary: "AllDay", calId: calId),
            Ev(20, 8, 0, 9, 0, summary: "T1", calId: calId),
            Ev(20, 9, 0, 10, 0, summary: "T2", calId: calId),
            Ev(20, 10, 0, 11, 0, summary: "T3", calId: calId),
            Ev(20, 11, 0, 12, 0, summary: "T4", calId: calId),
        };
        events[1].EndUtc = new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero); // all-day span

        var svc = new CalendarViewService(new StubExpander());
        var view = svc.BuildMonth(events, calendars, 2026, 7, Tz, Today, Now);

        // Past NOT hidden: Jul 1 still shows.
        Assert.Equal(1, CellAt(view, 7, 1).OccurrenceCount);

        var d20 = CellAt(view, 7, 20);
        Assert.Equal(5, d20.OccurrenceCount);              // 1 all-day + 4 timed
        Assert.Equal(3, d20.Preview.Count);                // capped at default PreviewLimit
        Assert.True(d20.Preview[0].IsAllDay);              // all-day surfaced first
        Assert.Equal("AllDay", d20.Preview[0].Summary);
        Assert.Equal("全天", d20.Preview[0].TimeLabel);
        Assert.Equal("#00f", d20.Preview[0].Color);

        Assert.True(view.HasAny);
    }

    [Fact]
    public void EmptyMonthHasNoItems()
    {
        var svc = new CalendarViewService(new StubExpander());
        var view = svc.BuildMonth(Array.Empty<CalendarEvent>(), new Dictionary<Guid, CalendarInfo>(), 2026, 7, Tz, Today, Now);
        Assert.False(view.HasAny);
        Assert.All(view.Weeks.SelectMany(w => w.Days), d => Assert.True(d.IsEmpty));
    }

    [Fact]
    public void MultiDayAllDayEvent_AppearsOnEveryOverlappingDay()
    {
        var calId = Guid.NewGuid();
        var calendars = new Dictionary<Guid, CalendarInfo> { [calId] = new() };
        // Mon Jul 13 00:00 .. Thu Jul 16 00:00 → covers Mon, Tue, Wed (3 full days).
        var ev = Ev(13, 0, 0, 0, 0, allDay: true, summary: "Retreat", calId: calId);
        ev.EndUtc = new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero);

        var svc = new CalendarViewService(new StubExpander());
        var view = svc.BuildWeek(new[] { ev }, calendars, Monday, Tz, Today, Now);

        Assert.Single(view.Days[0].AllDay); // Mon
        Assert.Single(view.Days[1].AllDay); // Tue
        Assert.Single(view.Days[2].AllDay); // Wed
        Assert.True(view.Days[3].IsEmpty);  // Thu — event ends at Thu 00:00 (exclusive)
    }

    [Fact]
    public void OvernightTimedEvent_AppearsOnBothDays()
    {
        var calId = Guid.NewGuid();
        var calendars = new Dictionary<Guid, CalendarInfo> { [calId] = new() };
        // Wed Jul 15 22:00 .. Thu Jul 16 02:00 → spans Wed and Thu.
        var ev = Ev(15, 22, 0, 0, 0, summary: "Late", calId: calId);
        ev.EndUtc = new DateTimeOffset(2026, 7, 16, 2, 0, 0, TimeSpan.Zero);

        var svc = new CalendarViewService(new StubExpander());
        var view = svc.BuildWeek(new[] { ev }, calendars, Monday, Tz, Today, Now);

        Assert.Single(view.Days[2].Timed); // Wed
        Assert.Single(view.Days[3].Timed); // Thu
    }
}
