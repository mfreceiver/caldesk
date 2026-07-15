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

    /// <summary>Build a 7-day week view starting at <paramref name="weekStartMonday"/>.</summary>
    public WeekView BuildWeek(
        IEnumerable<CalendarEvent> events,
        IReadOnlyDictionary<Guid, CalendarInfo> calendars,
        DateOnly weekStartMonday,
        TimeZoneInfo displayTz,
        DateOnly today,
        DateTimeOffset nowUtc)
    {
        // Week view auto-hides fully-past occurrences to reduce clutter.
        var byDay = BucketByDay(
            events, calendars, weekStartMonday, weekStartMonday.AddDays(7),
            displayTz, nowUtc, hideFullyPastBefore: today);

        var days = new List<WeekDay>(7);
        for (var i = 0; i < 7; i++)
        {
            var date = weekStartMonday.AddDays(i);
            var bucket = byDay[date];
            var all = bucket.Where(x => x.IsAllDay).OrderBy(x => x.StartUtc).ToList();
            var timed = bucket.Where(x => !x.IsAllDay).OrderBy(x => x.StartUtc).ToList();

            days.Add(new WeekDay
            {
                Date = date,
                Header = $"{WeekdayNames[i]} {date.Day}",
                IsToday = date == today,
                AllDay = all,
                Timed = timed,
            });
        }

        return new WeekView { Days = days };
    }

    /// <summary>Build a month-grid view for <paramref name="year"/>/<paramref name="month"/>.
    /// The grid is aligned to Mon–Sun weeks and includes spillover days from neighbouring months
    /// (marked <see cref="MonthDay.IsInMonth"/>=false). Past occurrences are NOT hidden, so
    /// navigating to previous months keeps their cells populated.</summary>
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

        var first = new DateOnly(year, month, 1);
        var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));

        var gridStart = first.AddDays(-DaysSinceMonday(first));
        var gridEndExclusive = last.AddDays(-DaysSinceMonday(last)).AddDays(7);

        // Month grid never hides past (lets users browse history).
        var byDay = BucketByDay(
            events, calendars, gridStart, gridEndExclusive,
            displayTz, nowUtc, hideFullyPastBefore: null);

        var weeks = new List<MonthWeek>();
        for (var w = gridStart; w < gridEndExclusive; w = w.AddDays(7))
        {
            var row = new List<MonthDay>(7);
            for (var i = 0; i < 7; i++)
            {
                var date = w.AddDays(i);
                var bucket = byDay.TryGetValue(date, out var b) ? b : new List<OccurrenceItem>();

                var ordered = bucket
                    .OrderByDescending(x => x.IsAllDay) // all-day first
                    .ThenBy(x => x.StartUtc)
                    .ToList();

                var preview = ordered
                    .Take(options.PreviewLimit)
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

    /// <summary>Expand every event in range and bucket occurrences by their local start-day.
    /// Shared by week and month views. <paramref name="hideFullyPastBefore"/> (when set) drops
    /// occurrences that ended at/before that date's local midnight — used only by the week view.</summary>
    private Dictionary<DateOnly, List<OccurrenceItem>> BucketByDay(
        IEnumerable<CalendarEvent> events,
        IReadOnlyDictionary<Guid, CalendarInfo> calendars,
        DateOnly rangeStart,
        DateOnly rangeEndExclusive,
        TimeZoneInfo displayTz,
        DateTimeOffset nowUtc,
        DateOnly? hideFullyPastBefore)
    {
        var rangeStartUtc = LocalMidnightToUtc(rangeStart, displayTz);
        var rangeEndUtc = LocalMidnightToUtc(rangeEndExclusive, displayTz);
        var pastCutoffUtc = hideFullyPastBefore is { } hide
            ? LocalMidnightToUtc(hide, displayTz)
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

                var zoned = TimeZoneInfo.ConvertTime(occ.StartUtc, displayTz);
                var day = DateOnly.FromDateTime(zoned.DateTime);
                if (!byDay.TryGetValue(day, out var bucket))
                {
                    continue;
                }

                bucket.Add(ToItem(occ, zoned, nowUtc));
            }
        }

        return byDay;
    }

    private static OccurrenceItem ToItem(EventOccurrence occ, DateTimeOffset zonedStart, DateTimeOffset nowUtc)
    {
        var ongoing = occ.StartUtc <= nowUtc && nowUtc < occ.EndUtc;
        return new OccurrenceItem
        {
            EventId = occ.Source.Id,
            Summary = string.IsNullOrEmpty(occ.Summary) ? "(无标题)" : occ.Summary,
            TimeLabel = occ.IsAllDay ? "全天" : zonedStart.ToString("HH:mm", CultureInfo.InvariantCulture),
            IsAllDay = occ.IsAllDay,
            IsOngoing = ongoing,
            Color = occ.Color,
            StartUtc = occ.StartUtc,
            EndUtc = occ.EndUtc,
        };
    }

    /// <summary>0 for Monday .. 6 for Sunday.</summary>
    private static int DaysSinceMonday(DateOnly date)
        => ((int)date.DayOfWeek + 6) % 7;

    private static DateTimeOffset LocalMidnightToUtc(DateOnly date, TimeZoneInfo tz)
    {
        var local = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, tz.GetUtcOffset(local)).UtcDateTime;
    }
}
