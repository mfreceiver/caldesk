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
            Ev(13, 9, 0, 10, 0, summary: "Mon", calId: calId),     // past -> hidden
            Ev(14, 9, 0, 10, 0, summary: "Tue", calId: calId),     // past -> hidden
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
        Assert.True(view.Days[0].IsEmpty);   // Monday hidden (past)
        Assert.True(view.Days[1].IsEmpty);   // Tuesday hidden (past)

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

    [Fact]
    public void EmptyWeekHasNoItems()
    {
        var svc = new CalendarViewService(new StubExpander());
        var view = svc.BuildWeek(Array.Empty<CalendarEvent>(), new Dictionary<Guid, CalendarInfo>(), Monday, Tz, Today, Now);
        Assert.False(view.HasAny);
        Assert.All(view.Days, d => Assert.True(d.IsEmpty));
    }
}
