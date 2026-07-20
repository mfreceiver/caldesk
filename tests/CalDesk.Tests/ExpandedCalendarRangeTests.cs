using CalDesk.Core.Models;
using CalDesk.Core.Recurrence;
using CalDesk.Core.Services;
using Xunit;

namespace CalDesk.Tests;

/// <summary>Tests for T3: single-pass event expansion reused by both the month grid and the
/// selected-day detail via <see cref="CalendarViewService.ExpandRange"/> +
/// <see cref="ExpandedCalendarRange"/>. Equivalence with the legacy
/// <see cref="CalendarViewService.BuildMonth"/> / <see cref="CalendarViewService.BuildDay"/>
/// overloads is asserted cell-by-cell (T3-C1/C2).</summary>
public sealed class ExpandedCalendarRangeTests
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.Utc;
    private static readonly DateOnly Today = new(2026, 7, 15);      // Wed
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Stub expander that emits one occurrence per event when its UTC span overlaps the
    /// query range — mirrors the one in <see cref="CalendarViewServiceTests"/>.</summary>
    private sealed class StubExpander : IRecurrenceExpander
    {
        public IEnumerable<EventOccurrence> Expand(CalendarEvent e, CalendarInfo c, DateTimeOffset rs, DateTimeOffset re)
        {
            if (e.StartUtc < re && e.EndUtc > rs)
            {
                yield return new EventOccurrence
                {
                    Source = e, Calendar = c, StartUtc = e.StartUtc, EndUtc = e.EndUtc,
                };
            }
        }
    }

    /// <summary>Counting wrapper to assert ExpandRange is called exactly once per event even when
    /// the result is reused by both build paths (T3-C3 core invariant: no re-expansion).</summary>
    private sealed class CountingExpander : IRecurrenceExpander
    {
        public int InvocationCount;
        public IEnumerable<EventOccurrence> Expand(CalendarEvent e, CalendarInfo c, DateTimeOffset rs, DateTimeOffset re)
        {
            InvocationCount++;
            if (e.StartUtc < re && e.EndUtc > rs)
            {
                yield return new EventOccurrence
                {
                    Source = e, Calendar = c, StartUtc = e.StartUtc, EndUtc = e.EndUtc,
                };
            }
        }
    }

    private static CalendarEvent Ev(int day, int h0, int m0, int h1, int m1, bool allDay = false, string summary = "x", Guid calId = default)
    {
        var s = new DateTimeOffset(2026, 7, day, h0, m0, 0, TimeSpan.Zero);
        var e = new DateTimeOffset(2026, 7, day, h1, m1, 0, TimeSpan.Zero);
        return new CalendarEvent
        {
            Id = Guid.NewGuid(), CalendarId = calId, Summary = summary,
            StartUtc = s, EndUtc = e, IsAllDay = allDay,
        };
    }

    private static CalendarEvent MultiDay(int d0, int d1, string summary, Guid calId, bool allDay = true) => new()
    {
        Id = Guid.NewGuid(),
        CalendarId = calId,
        Summary = summary,
        StartUtc = new DateTimeOffset(2026, 7, d0, 0, 0, 0, TimeSpan.Zero),
        EndUtc = new DateTimeOffset(2026, 7, d1, 0, 0, 0, TimeSpan.Zero),
        IsAllDay = allDay,
    };

    /// <summary>Shared fixture: a multi-day all-day event (Mon–Wed), a Tuesday timed event, a
    /// Wednesday timed event that is "ongoing" at Now, and a Thursday afternoon timed event.
    /// Calendar carries a non-null colour so the preview's colour field is exercised.</summary>
    private static (CalendarEvent[] Events, Dictionary<Guid, CalendarInfo> Calendars) MakeFixture()
    {
        var calId = Guid.NewGuid();
        var calendars = new Dictionary<Guid, CalendarInfo> { [calId] = new() { Color = "#00ff00", DisplayName = "C" } };
        var events = new[]
        {
            MultiDay(13, 16, "Retreat", calId),                 // Mon 00:00 .. Thu 00:00 → Mon, Tue, Wed
            Ev(14, 9, 0, 10, 0, summary: "Tue9", calId: calId),
            Ev(15, 9, 0, 11, 0, summary: "WedOngoing", calId: calId),
            Ev(16, 14, 0, 15, 0, summary: "Thu14", calId: calId),
        };
        return (events, calendars);
    }

    private static MonthDay Cell(MonthView view, DateOnly date) =>
        view.Weeks.SelectMany(w => w.Days).First(d => d.Date == date);

    /// <summary>T3-C1: BuildMonthFromExpanded(ExpandRange(...)) produces the same cells
    /// (OccurrenceCount, OverflowCount, Preview summaries/time-labels/all-day flags/colours) as
    /// the legacy BuildMonth(events,...) overload for the same input.
    /// <para><b>Self-reference caveat:</b> because the legacy <c>BuildMonth</c> overload now
    /// delegates to <c>ExpandRange</c> + <c>BuildMonthFromExpanded</c> (behaviour-preserving
    /// refactor in T3), this test effectively exercises the delegation wiring on both sides —
    /// the two paths share <see cref="CalendarViewService.BucketByDay"/> for the actual
    /// bucketing. The <em>real</em> behavioural gold standard / regression protection lives in
    /// the value-level assertions of the pre-existing <c>CalendarViewServiceTests</c> (overflow
    /// counts, all-day ordering, multi-day spread). This test's job is to lock the wiring so a
    /// future refactor that breaks the delegation (e.g. diverging sort/slice in the FromExpanded
    /// path) is caught.</para></summary>
    [Fact]
    public void BuildMonthFromExpanded_MatchesBuildMonth_CellByCell()
    {
        var (events, calendars) = MakeFixture();
        var svc = new CalendarViewService(new StubExpander());

        var oldView = svc.BuildMonth(events, calendars, 2026, 7, Tz, Today, Now);
        var grid = MonthGridRange.For(2026, 7);
        var range = svc.ExpandRange(events, calendars, grid.Start, grid.EndExclusive, Tz, Now);
        var newView = svc.BuildMonthFromExpanded(range, 2026, 7, Today);

        Assert.Equal(oldView.Year, newView.Year);
        Assert.Equal(oldView.Month, newView.Month);
        Assert.Equal(oldView.Title, newView.Title);
        Assert.Equal(oldView.WeekdayHeaders, newView.WeekdayHeaders);
        Assert.Equal(oldView.Weeks.Count, newView.Weeks.Count);

        var oldCells = oldView.Weeks.SelectMany(w => w.Days).ToArray();
        var newCells = newView.Weeks.SelectMany(w => w.Days).ToArray();
        Assert.Equal(oldCells.Length, newCells.Length);  // 42

        for (var i = 0; i < oldCells.Length; i++)
        {
            var o = oldCells[i];
            var n = newCells[i];
            Assert.Equal(o.Date, n.Date);
            Assert.Equal(o.Day, n.Day);
            Assert.Equal(o.IsInMonth, n.IsInMonth);
            Assert.Equal(o.IsToday, n.IsToday);
            Assert.Equal(o.IsWeekend, n.IsWeekend);
            Assert.Equal(o.OccurrenceCount, n.OccurrenceCount);
            Assert.Equal(o.OverflowCount, n.OverflowCount);
            Assert.Equal(o.Preview.Count, n.Preview.Count);
            for (var j = 0; j < o.Preview.Count; j++)
            {
                Assert.Equal(o.Preview[j].EventId, n.Preview[j].EventId);
                Assert.Equal(o.Preview[j].Summary, n.Preview[j].Summary);
                Assert.Equal(o.Preview[j].TimeLabel, n.Preview[j].TimeLabel);
                Assert.Equal(o.Preview[j].IsAllDay, n.Preview[j].IsAllDay);
                Assert.Equal(o.Preview[j].Color, n.Preview[j].Color);
            }
        }

        // Spot-check the cell that has overflow (4 events on a day would force overflow; here we
        // verify the multi-day event appears on each spanned day in BOTH views identically).
        var monOld = Cell(oldView, new DateOnly(2026, 7, 13));
        var monNew = Cell(newView, new DateOnly(2026, 7, 13));
        Assert.Equal(monOld.OccurrenceCount, monNew.OccurrenceCount);
        Assert.Equal("Retreat", monNew.Preview.Single(p => p.IsAllDay).Summary);
    }

    /// <summary>T3-C2: BuildDayFromExpanded(ExpandRange(...)) matches the legacy BuildDay(events,...)
    /// overload for the same day — same header, all/timed split, left/right half partition, summaries.
    /// <para><b>Self-reference caveat:</b> the legacy <c>BuildDay</c> now delegates to
    /// <c>ExpandRange</c> + <c>BuildDayFromExpanded</c>, so both sides share the bucketing path.
    /// The behavioural gold standard lives in <c>CalendarViewServiceTests.BuildDay_*</c> assertions
    /// (header weekday, all/timed split, overnight time-range labels). This test locks the wiring
    /// so a future divergence in the FromExpanded path is caught.</para></summary>
    [Fact]
    public void BuildDayFromExpanded_MatchesBuildDay()
    {
        var (events, calendars) = MakeFixture();
        var svc = new CalendarViewService(new StubExpander());
        var date = new DateOnly(2026, 7, 15);  // Wed — has Retreat (all-day) + WedOngoing (timed)

        var oldDay = svc.BuildDay(events, calendars, date, Tz, Today, Now);
        var range = svc.ExpandRange(events, calendars, date, date.AddDays(1), Tz, Now);
        var newDay = svc.BuildDayFromExpanded(range, date, Today, Now);

        Assert.Equal(oldDay.Date, newDay.Date);
        Assert.Equal(oldDay.Header, newDay.Header);
        Assert.Equal(oldDay.IsToday, newDay.IsToday);
        Assert.Equal(oldDay.AllDay.Count, newDay.AllDay.Count);
        Assert.Equal(oldDay.Timed.Count, newDay.Timed.Count);
        Assert.Equal(oldDay.AllEvents.Count, newDay.AllEvents.Count);
        Assert.Equal(oldDay.LeftEvents.Count, newDay.LeftEvents.Count);
        Assert.Equal(oldDay.RightEvents.Count, newDay.RightEvents.Count);
        Assert.Equal(oldDay.AllEvents.Select(x => x.Summary), newDay.AllEvents.Select(x => x.Summary));
        Assert.Equal(oldDay.AllEvents.Select(x => x.IsAllDay), newDay.AllEvents.Select(x => x.IsAllDay));
        Assert.Equal(oldDay.AllEvents.Select(x => x.TimeLabel), newDay.AllEvents.Select(x => x.TimeLabel));
    }

    /// <summary>T3 spec: ExpandRange places the SAME item (same EventId) on every local day a
    /// multi-day event spans — confirming BucketByDay's per-day spread is preserved through the
    /// ExpandedCalendarRange wrapper.</summary>
    [Fact]
    public void ExpandRange_MultiDayEvent_PlacesSameItemOnEveryOverlappingDay()
    {
        var calId = Guid.NewGuid();
        var calendars = new Dictionary<Guid, CalendarInfo> { [calId] = new() { Color = "#00ff00" } };
        var events = new[] { MultiDay(13, 16, "Retreat", calId) };  // Mon 00:00 .. Thu 00:00 → Mon, Tue, Wed
        var svc = new CalendarViewService(new StubExpander());

        var range = svc.ExpandRange(events, calendars,
            new DateOnly(2026, 7, 13), new DateOnly(2026, 7, 20), Tz, Now);

        var mon = range.GetDay(new DateOnly(2026, 7, 13));
        var tue = range.GetDay(new DateOnly(2026, 7, 14));
        var wed = range.GetDay(new DateOnly(2026, 7, 15));
        var thu = range.GetDay(new DateOnly(2026, 7, 16));   // exclusive end → empty

        Assert.Single(mon);
        Assert.Single(tue);
        Assert.Single(wed);
        Assert.Empty(thu);
        // Same EventId across days — the same item spread, not three separate occurrences.
        Assert.Equal(mon[0].EventId, tue[0].EventId);
        Assert.Equal(mon[0].EventId, wed[0].EventId);
        Assert.Equal("Retreat", mon[0].Summary);
    }

    /// <summary>T3 spec: overnight timed event (Wed 22:00 → Thu 02:00) is placed on both days.</summary>
    [Fact]
    public void ExpandRange_OvernightTimed_PlacesItemOnBothDays()
    {
        var calId = Guid.NewGuid();
        var calendars = new Dictionary<Guid, CalendarInfo> { [calId] = new() };
        var ev = Ev(15, 22, 0, 0, 0, summary: "Late", calId: calId);
        ev.EndUtc = new DateTimeOffset(2026, 7, 16, 2, 0, 0, TimeSpan.Zero);
        var svc = new CalendarViewService(new StubExpander());

        var range = svc.ExpandRange(new[] { ev }, calendars,
            new DateOnly(2026, 7, 13), new DateOnly(2026, 7, 20), Tz, Now);

        Assert.Single(range.GetDay(new DateOnly(2026, 7, 15)));  // Wed
        Assert.Single(range.GetDay(new DateOnly(2026, 7, 16)));  // Thu
        Assert.Empty(range.GetDay(new DateOnly(2026, 7, 17)));   // Fri
    }

    /// <summary>T3 spec: GetDay outside the expansion range returns an empty list (does not throw).</summary>
    [Fact]
    public void GetDay_OutsideRange_ReturnsEmpty()
    {
        var svc = new CalendarViewService(new StubExpander());
        var range = svc.ExpandRange(Array.Empty<CalendarEvent>(), new Dictionary<Guid, CalendarInfo>(),
            new DateOnly(2026, 7, 13), new DateOnly(2026, 7, 20), Tz, Now);

        Assert.Empty(range.GetDay(new DateOnly(2025, 1, 1)));  // before range
        Assert.Empty(range.GetDay(new DateOnly(2026, 8, 1)));  // after range
        Assert.Empty(range.GetDay(new DateOnly(2026, 7, 20))); // == EndExclusive (half-open)
    }

    /// <summary>T3 core invariant: BuildMonthFromExpanded + BuildDayFromExpanded reading from the
    /// SAME ExpandedCalendarRange never re-invoke the expander — ExpandRange runs exactly once per
    /// event regardless of how many build calls read the result.</summary>
    [Fact]
    public void ExpandRange_InvokedOncePerEvent_WhenResultReused()
    {
        var calId = Guid.NewGuid();
        var calendars = new Dictionary<Guid, CalendarInfo> { [calId] = new() };
        var events = new[] { Ev(15, 9, 0, 10, 0, summary: "A", calId: calId) };

        var spy = new CountingExpander();
        var svc = new CalendarViewService(spy);

        var grid = MonthGridRange.For(2026, 7);
        var range = svc.ExpandRange(events, calendars, grid.Start, grid.EndExclusive, Tz, Now);
        var firstCall = spy.InvocationCount;
        Assert.Equal(1, firstCall);

        // Reuse the SAME range for both build paths — expander must NOT be called again.
        _ = svc.BuildMonthFromExpanded(range, 2026, 7, Today);
        _ = svc.BuildDayFromExpanded(range, new DateOnly(2026, 7, 15), Today, Now);

        Assert.Equal(1, spy.InvocationCount);
    }

    /// <summary>T3-C1 equivalence for a cell with overflow (≥4 events → preview shows 2 + overflow
    /// count). The fixture drives the legacy path through BuildMonth and the new path through
    /// ExpandRange + BuildMonthFromExpanded, asserting identical OverflowCount/Preview count.</summary>
    [Fact]
    public void BuildMonthFromExpanded_MatchesBuildMonth_OnOverflowCell()
    {
        var calId = Guid.NewGuid();
        var calendars = new Dictionary<Guid, CalendarInfo> { [calId] = new() };
        // 4 timed events on Tue Jul 14 — exactly the existing BuildMonth overflow test scenario.
        var events = Enumerable.Range(0, 4)
            .Select(i => Ev(14, 10 + i, 0, 11 + i, 0, summary: "E" + i, calId: calId)).ToArray();
        var svc = new CalendarViewService(new StubExpander());

        var oldView = svc.BuildMonth(events, calendars, 2026, 7, Tz, Today, Now);
        var grid = MonthGridRange.For(2026, 7);
        var range = svc.ExpandRange(events, calendars, grid.Start, grid.EndExclusive, Tz, Now);
        var newView = svc.BuildMonthFromExpanded(range, 2026, 7, Today);

        var oldCell = Cell(oldView, new DateOnly(2026, 7, 14));
        var newCell = Cell(newView, new DateOnly(2026, 7, 14));

        Assert.Equal(4, newCell.OccurrenceCount);
        Assert.Equal(oldCell.OccurrenceCount, newCell.OccurrenceCount);
        Assert.Equal(2, newCell.Preview.Count);
        Assert.Equal(oldCell.Preview.Count, newCell.Preview.Count);
        Assert.Equal(2, newCell.OverflowCount);
        Assert.Equal(oldCell.OverflowCount, newCell.OverflowCount);
    }
}
