using System.Globalization;
using CalDesk.Core.Models;
using CalDesk.Core.Recurrence;

namespace CalDesk.Core.Services;

/// <summary>Builds display-ready views (<see cref="WeekView"/>, <see cref="MonthView"/>) from stored events.
/// Pure / testable logic; no data access (callers fetch events via repositories).</summary>
public sealed class CalendarViewService(IRecurrenceExpander expander)
{
    private static readonly string[] WeekdayNames = ["周一", "周二", "周三", "周四", "周五", "周六", "周日"];

    /// <summary>Shared weekday headers (Mon..Sun, Chinese).</summary>
    public static IReadOnlyList<string> Headers => WeekdayNames;

    /// <summary>Build a 7-day week view starting at <paramref name="weekStartMonday"/>.
    /// The full visible week is rendered (past / present / future); only
    /// <see cref="WeekDay.IsToday"/> and <see cref="OccurrenceItem.IsOngoing"/> mark the present.</summary>
    public WeekView BuildWeek(
        IEnumerable<CalendarEvent> events,
        IReadOnlyDictionary<Guid, CalendarInfo> calendars,
        DateOnly weekStartMonday,
        TimeZoneInfo displayTz,
        DateOnly today,
        DateTimeOffset nowUtc)
    {
        // Do not hide past occurrences — the whole week (WeekStart..+7) must stay populated.
        var byDay = BucketByDay(
            events, calendars, weekStartMonday, weekStartMonday.AddDays(7),
            displayTz, nowUtc, hideFullyPastBefore: null);

        var days = new List<WeekDay>(7);
        for (var i = 0; i < 7; i++)
        {
            var date = weekStartMonday.AddDays(i);
            var bucket = byDay[date];
            var all = bucket.Where(x => x.IsAllDay).OrderBy(x => x.StartUtc).ToList();
            var timed = bucket.Where(x => !x.IsAllDay).OrderBy(x => x.StartUtc).ToList();

            var allEvents = all.Concat(timed).ToList();
            var half = (allEvents.Count + 1) / 2;
            days.Add(new WeekDay
            {
                Date = date,
                Header = $"{WeekdayNames[i]} {date.Day}",
                IsToday = date == today,
                IsWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
                AllDay = all,
                Timed = timed,
                AllEvents = allEvents,
                LeftEvents = allEvents.Take(half).ToList(),
                RightEvents = allEvents.Skip(half).ToList(),
            });
        }

        return new WeekView { Days = days };
    }

    /// <summary>Build a month-grid view for <paramref name="year"/>/<paramref name="month"/>.
    /// The grid is aligned to Mon–Sun weeks and includes spillover days from neighbouring months
    /// (marked <see cref="MonthDay.IsInMonth"/>=false). Past occurrences are NOT hidden, so
    /// navigating to previous months keeps their cells populated.</summary>
    /// <remarks>Behaviour-preserving delegate (T3): composes <see cref="ExpandRange"/> +
    /// <see cref="BuildMonthFromExpanded"/> so the legacy signature stays available for existing
    /// tests and call sites. New code should prefer <see cref="ExpandRange"/> once +
    /// <see cref="BuildMonthFromExpanded"/> reuse to avoid the duplicate query+expand.</remarks>
    public MonthView BuildMonth(
        IEnumerable<CalendarEvent> events,
        IReadOnlyDictionary<Guid, CalendarInfo> calendars,
        int year,
        int month,
        TimeZoneInfo displayTz,
        DateOnly today,
        DateTimeOffset nowUtc,
        MonthViewOptions? options = null)
    {
        options ??= MonthViewOptions.Default;
        var grid = MonthGridRange.For(year, month);
        var range = ExpandRange(events, calendars, grid.Start, grid.EndExclusive, displayTz, nowUtc);
        return BuildMonthFromExpanded(range, year, month, today, options);
    }

    /// <summary>Expand every event in the half-open <c>[<paramref name="rangeStart"/>,
    /// <paramref name="rangeEndExclusive"/>)</c> interval ONCE and bucket occurrences by their
    /// local-day start, returning a reusable <see cref="ExpandedCalendarRange"/> that
    /// <see cref="BuildMonthFromExpanded"/> and <see cref="BuildDayFromExpanded"/> read from without
    /// re-running the expander. Past occurrences are NOT hidden (matches the legacy BuildMonth/BuildDay
    /// semantics).</summary>
    public ExpandedCalendarRange ExpandRange(
        IEnumerable<CalendarEvent> events,
        IReadOnlyDictionary<Guid, CalendarInfo> calendars,
        DateOnly rangeStart,
        DateOnly rangeEndExclusive,
        TimeZoneInfo displayTz,
        DateTimeOffset nowUtc)
    {
        var byDay = BucketByDay(
            events, calendars, rangeStart, rangeEndExclusive,
            displayTz, nowUtc, hideFullyPastBefore: null);
        return new ExpandedCalendarRange(byDay);
    }

    /// <summary>Build a month-grid view from a pre-expanded <paramref name="range"/> (T3). Reads
    /// only from <see cref="ExpandedCalendarRange.GetDay"/>; does <em>not</em> re-run the expander.
    /// <para>The <paramref name="range"/> must cover at least <c>[MonthGridRange.For(year, month).Start,
    /// .EndExclusive)</c> — typically obtained from <see cref="ExpandRange"/> called with those
    /// exact bounds in <c>MonthViewModel.LoadAsync</c>.</para></summary>
    /// <param name="options">Reserved; defaults to <see cref="MonthViewOptions.Default"/> (currently
    /// unused, kept for parity with <see cref="BuildMonth"/>).</param>
    public MonthView BuildMonthFromExpanded(
        ExpandedCalendarRange range,
        int year,
        int month,
        DateOnly today,
        MonthViewOptions? options = null)
    {
        options ??= MonthViewOptions.Default;
        var grid = MonthGridRange.For(year, month);
        var gridStart = grid.Start;
        var gridEndExclusive = grid.EndExclusive;

        var weeks = new List<MonthWeek>();
        for (var w = gridStart; w < gridEndExclusive; w = w.AddDays(7))
        {
            var row = new List<MonthDay>(7);
            for (var i = 0; i < 7; i++)
            {
                var date = w.AddDays(i);
                var bucket = range.GetDay(date);

                var ordered = bucket
                    .OrderByDescending(x => x.IsAllDay) // all-day first
                    .ThenBy(x => x.StartUtc)
                    .ToList();

                var count = ordered.Count;
                var visible = count > 3 ? 2 : 3;
                var preview = ordered
                    .Take(visible)
                    .Select(x => new MonthOccurrence
                    {
                        EventId = x.EventId,
                        Summary = x.Summary,
                        TimeLabel = x.TimeLabel,
                        IsAllDay = x.IsAllDay,
                        Color = x.Color,
                    })
                    .ToList();

                row.Add(new MonthDay
                {
                    Date = date,
                    Day = date.Day,
                    IsInMonth = date.Month == month,
                    IsToday = date == today,
                    IsWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
                    OccurrenceCount = ordered.Count,
                    OverflowCount = count > 3 ? count - 2 : 0,
                    Preview = preview,
                });
            }

            weeks.Add(new MonthWeek { Days = row });
        }

        return new MonthView
        {
            Year = year,
            Month = month,
            Title = $"{year}年{month}月",
            WeekdayHeaders = WeekdayNames,
            Weeks = weeks,
        };
    }

    /// <summary>Build a single-day detail (all-day + timed) for the month-view bottom panel.
    /// Behaviour-preserving delegate (T3): composes <see cref="ExpandRange"/> over a 1-day range +
    /// <see cref="BuildDayFromExpanded"/>.</summary>
    public WeekDay BuildDay(
        IEnumerable<CalendarEvent> events,
        IReadOnlyDictionary<Guid, CalendarInfo> calendars,
        DateOnly date,
        TimeZoneInfo displayTz,
        DateOnly today,
        DateTimeOffset nowUtc)
    {
        var range = ExpandRange(events, calendars, date, date.AddDays(1), displayTz, nowUtc);
        return BuildDayFromExpanded(range, date, today, nowUtc);
    }

    /// <summary>Build a single-day detail from a pre-expanded <paramref name="range"/> (T3). Reads
    /// only from <see cref="ExpandedCalendarRange.GetDay"/>; does <em>not</em> re-run the expander.
    /// <para><paramref name="nowUtc"/> is kept in the signature for parity with
    /// <see cref="BuildDay"/>; it is unused here because <see cref="OccurrenceItem.IsOngoing"/> was
    /// already baked into each item during <see cref="ExpandRange"/>.</para></summary>
    public WeekDay BuildDayFromExpanded(
        ExpandedCalendarRange range,
        DateOnly date,
        DateOnly today,
        DateTimeOffset nowUtc)
    {
        _ = nowUtc; // parity-only; ongoing state is already baked into range items (see XML doc).
        var bucket = range.GetDay(date);
        var all = bucket.Where(x => x.IsAllDay).OrderBy(x => x.StartUtc).ToList();
        var timed = bucket.Where(x => !x.IsAllDay).OrderBy(x => x.StartUtc).ToList();
        var idx = CalendarDateMath.DaysSinceMonday(date);
        var allEvents = all.Concat(timed).ToList();
        var half = (allEvents.Count + 1) / 2;
        return new WeekDay
        {
            Date = date,
            Header = $"{WeekdayNames[idx]} {date.Day}",
            IsToday = date == today,
            IsWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
            AllDay = all,
            Timed = timed,
            AllEvents = allEvents,
            LeftEvents = allEvents.Take(half).ToList(),
            RightEvents = allEvents.Skip(half).ToList(),
        };
    }

    /// <summary>Expand every event in range and bucket occurrences by their local start-day.
    /// Shared by week and month views. <paramref name="hideFullyPastBefore"/> (when set) drops
    /// occurrences that ended at/before that date's local midnight. Week and month both pass
    /// null so the full visible range stays populated.</summary>
    private Dictionary<DateOnly, List<OccurrenceItem>> BucketByDay(
        IEnumerable<CalendarEvent> events,
        IReadOnlyDictionary<Guid, CalendarInfo> calendars,
        DateOnly rangeStart,
        DateOnly rangeEndExclusive,
        TimeZoneInfo displayTz,
        DateTimeOffset nowUtc,
        DateOnly? hideFullyPastBefore)
    {
        var rangeStartUtc = CalendarDateMath.LocalMidnightToUtc(rangeStart, displayTz);
        var rangeEndUtc = CalendarDateMath.LocalMidnightToUtc(rangeEndExclusive, displayTz);
        var pastCutoffUtc = hideFullyPastBefore is { } hide
            ? CalendarDateMath.LocalMidnightToUtc(hide, displayTz)
            : DateTimeOffset.MinValue;

        var byDay = new Dictionary<DateOnly, List<OccurrenceItem>>();
        for (var d = rangeStart; d < rangeEndExclusive; d = d.AddDays(1))
        {
            byDay[d] = new List<OccurrenceItem>();
        }

        foreach (var evt in events)
        {
            calendars.TryGetValue(evt.CalendarId, out var cal);
            cal ??= new CalendarInfo { DisplayName = string.Empty };

            foreach (var occ in expander.Expand(evt, cal, rangeStartUtc, rangeEndUtc))
            {
                if (occ.EndUtc <= pastCutoffUtc)
                {
                    continue;
                }

                var startZoned = TimeZoneInfo.ConvertTime(occ.StartUtc, displayTz);
                var startDay = DateOnly.FromDateTime(startZoned.DateTime);

                // Inclusive last local day the occurrence overlaps. End is exclusive, so an event
                // ending exactly at local midnight covers through the previous day only (e.g. an
                // all-day event Mon 00:00..Tue 00:00 covers just Mon; one Mon..Thu covers Mon-Wed).
                var endZoned = TimeZoneInfo.ConvertTime(occ.EndUtc, displayTz);
                var lastDay = endZoned.TimeOfDay == TimeSpan.Zero
                    ? DateOnly.FromDateTime(endZoned.Date).AddDays(-1)
                    : DateOnly.FromDateTime(endZoned.DateTime);
                if (lastDay < startDay)
                {
                    lastDay = startDay; // zero-length / sub-day occurrence safety
                }

                // Place the occurrence on every overlapping day (clipped to the bucketed range),
                // so multi-day all-day events and overnight events aren't dropped from later days.
                var item = ToItem(occ, startZoned, endZoned, nowUtc);
                for (var day = startDay; day <= lastDay; day = day.AddDays(1))
                {
                    if (byDay.TryGetValue(day, out var bucket))
                    {
                        bucket.Add(item);
                    }
                }
            }
        }

        return byDay;
    }

    private static OccurrenceItem ToItem(EventOccurrence occ, DateTimeOffset zonedStart, DateTimeOffset zonedEnd, DateTimeOffset nowUtc)
    {
        var ongoing = occ.StartUtc <= nowUtc && nowUtc < occ.EndUtc;
        return new OccurrenceItem
        {
            EventId = occ.Source.Id,
            Summary = string.IsNullOrEmpty(occ.Summary) ? "(无标题)" : occ.Summary,
            TimeLabel = occ.IsAllDay ? "全天" : zonedStart.ToString("HH:mm", CultureInfo.InvariantCulture),
            TimeRangeLabel = FormatTimeRange(occ.IsAllDay, zonedStart, zonedEnd),
            IsAllDay = occ.IsAllDay,
            IsOngoing = ongoing,
            Color = occ.Color,
            StartUtc = occ.StartUtc,
            EndUtc = occ.EndUtc,
        };
    }

    private static string FormatTimeRange(bool allDay, DateTimeOffset start, DateTimeOffset end)
    {
        if (allDay) return "全天";
        var startText = start.ToString("HH:mm", CultureInfo.InvariantCulture);
        var endText = end.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (start.Date == end.Date) return $"{startText}-{endText}";
        if (end.Date == start.Date.AddDays(1)) return $"{startText}→次日{endText}";
        return $"{startText}→{end:MM-dd HH:mm}";
    }
}
