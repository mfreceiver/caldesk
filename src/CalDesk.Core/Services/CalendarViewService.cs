using System.Globalization;
using CalDesk.Core.Models;
using CalDesk.Core.Recurrence;

namespace CalDesk.Core.Services;

/// <summary>Builds display-ready <see cref="WeekView"/> from stored events.
/// Pure / testable logic; no data access (callers fetch events via repositories).</summary>
public sealed class CalendarViewService(IRecurrenceExpander expander)
{
    private static readonly string[] WeekdayNames = ["周一", "周二", "周三", "周四", "周五", "周六", "周日"];

    /// <summary>Build a 7-day week view starting at <paramref name="weekStartMonday"/>.</summary>
    public WeekView BuildWeek(
        IEnumerable<CalendarEvent> events,
        IReadOnlyDictionary<Guid, CalendarInfo> calendars,
        DateOnly weekStartMonday,
        TimeZoneInfo displayTz,
        DateOnly today,
        DateTimeOffset nowUtc)
    {
        var rangeStartUtc = LocalMidnightToUtc(weekStartMonday, displayTz);
        var rangeEndUtc = rangeStartUtc.AddDays(7);
        var todayStartUtc = LocalMidnightToUtc(today, displayTz);

        // Expand every event into occurrences, bucket by local start-day, drop past-day items.
        var byDay = new Dictionary<DateOnly, List<OccurrenceItem>>();
        for (var i = 0; i < 7; i++)
        {
            byDay[weekStartMonday.AddDays(i)] = new List<OccurrenceItem>();
        }

        foreach (var evt in events)
        {
            calendars.TryGetValue(evt.CalendarId, out var cal);
            cal ??= new CalendarInfo { DisplayName = string.Empty };

            foreach (var occ in expander.Expand(evt, cal, rangeStartUtc, rangeEndUtc))
            {
                // Auto-hide fully-past occurrences (ended before today) — keeps cross-day into today.
                if (occ.EndUtc <= todayStartUtc)
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

        var days = new List<WeekDay>(7);
        for (var i = 0; i < 7; i++)
        {
            var date = weekStartMonday.AddDays(i);
            var all = byDay[date].Where(x => x.IsAllDay).OrderBy(x => x.StartUtc).ToList();
            var timed = byDay[date].Where(x => !x.IsAllDay).OrderBy(x => x.StartUtc).ToList();

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

    private static DateTimeOffset LocalMidnightToUtc(DateOnly date, TimeZoneInfo tz)
    {
        var local = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, tz.GetUtcOffset(local)).UtcDateTime;
    }
}
