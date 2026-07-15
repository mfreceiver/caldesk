using System.Globalization;
using CalDesk.Core.Models;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using CalendarEvent = CalDesk.Core.Models.CalendarEvent;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace CalDesk.CalDav.Calendar;

/// <summary>Maps between raw iCalendar (via Ical.Net) and <see cref="CalendarEvent"/>.
/// Edits preserve the original raw object so unknown / X- properties survive (NFR-05).</summary>
public static class IcalMapper
{
    /// <summary>Parse a raw iCalendar object into one or more events.</summary>
    public static List<CalendarEvent> ToCalendarEvents(string raw, Guid calendarId, string? href, string? etag)
    {
        var result = new List<CalendarEvent>();
        Ical.Net.Calendar cal;
        try
        {
            cal = Ical.Net.Calendar.Load(new StringReader(raw));
        }
        catch
        {
            return result;
        }

        foreach (var ve in cal.Events)
        {
            result.Add(Map(ve, calendarId, href, etag, raw));
        }

        return result;
    }

    /// <summary>Serialize an event for upload. When raw data exists, edit it in place
    /// (preserving VTIMEZONE / X- / VALARM); otherwise build a fresh object.</summary>
    public static string Serialize(CalendarEvent evt)
    {
        if (!string.IsNullOrWhiteSpace(evt.ICalendarData))
        {
            return EditInPlace(evt.ICalendarData, evt);
        }

        return BuildICalendar(evt);
    }

    // ---- parse -------------------------------------------------------------

    private static CalendarEvent Map(IcalEvent ve, Guid calendarId, string? href, string? etag, string raw)
    {
        var start = ve.DtStart;
        var isAllDay = start is not null && !start.HasTime;
        var startUtc = ToUtc(start);
        var endUtc = ResolveEnd(ve, start, startUtc, isAllDay);

        return new CalendarEvent
        {
            CalendarId = calendarId,
            Uid = ve.Uid ?? string.Empty,
            RemoteHref = href,
            ETag = etag,
            ICalendarData = raw,
            Summary = ve.Summary ?? string.Empty,
            Description = string.IsNullOrEmpty(ve.Description) ? null : ve.Description,
            Location = string.IsNullOrEmpty(ve.Location) ? null : ve.Location,
            IsAllDay = isAllDay,
            StartUtc = startUtc,
            EndUtc = endUtc,
            TimeZoneId = start?.TzId,
            IsFloating = start is not null && start.IsFloating,
            RecurrenceRule = RulesToRrule(ve.RecurrenceRules),
            SyncStatus = SyncStatus.Clean,
        };
    }

    private static DateTimeOffset ToUtc(CalDateTime? dt)
    {
        if (dt is null)
        {
            return DateTimeOffset.UtcNow;
        }

        var utc = dt.AsUtc;
        return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private static DateTimeOffset ResolveEnd(IcalEvent ve, CalDateTime? start, DateTimeOffset startUtc, bool isAllDay)
    {
        if (ve.DtEnd is not null)
        {
            return ToUtc(ve.DtEnd);
        }

        return isAllDay ? startUtc.AddDays(1) : startUtc;
    }

    private static string? RulesToRrule(IList<RecurrencePattern>? rules)
    {
        if (rules is null || rules.Count == 0)
        {
            return null;
        }

        var parts = rules.Select(RuleToRrule).Where(x => x is not null).ToList();
        return parts.Count == 0 ? null : string.Join(";", parts);
    }

    private static string? RuleToRrule(RecurrencePattern p)
    {
        var freq = p.Frequency switch
        {
            FrequencyType.Daily => "DAILY",
            FrequencyType.Weekly => "WEEKLY",
            FrequencyType.Monthly => "MONTHLY",
            FrequencyType.Yearly => "YEARLY",
            _ => null,
        };
        if (freq is null)
        {
            return null;
        }

        var tokens = new List<string> { $"FREQ={freq}", $"INTERVAL={Math.Max(1, p.Interval)}" };

        if (p.Count > 0)
        {
            tokens.Add($"COUNT={p.Count}");
        }
        else if (p.Until is { } until)
        {
            tokens.Add($"UNTIL={until.AsUtc:yyyyMMdd'T'HHmmss'Z'}");
        }

        if (p.ByDay is { Count: > 0 })
        {
            var days = p.ByDay
                .Where(d => d.Offset == 0)
                .Select(d => DayCode(d.DayOfWeek))
                .Distinct();
            tokens.Add("BYDAY=" + string.Join(",", days));
        }

        return string.Join(";", tokens);
    }

    private static string DayCode(DayOfWeek d) => d switch
    {
        DayOfWeek.Sunday => "SU",
        DayOfWeek.Monday => "MO",
        DayOfWeek.Tuesday => "TU",
        DayOfWeek.Wednesday => "WE",
        DayOfWeek.Thursday => "TH",
        DayOfWeek.Friday => "FR",
        DayOfWeek.Saturday => "SA",
        _ => "MO",
    };

    // ---- serialize ---------------------------------------------------------

    private static string BuildICalendar(CalendarEvent evt)
    {
        var cal = new Ical.Net.Calendar { Version = "2.0" };

        var ve = new IcalEvent
        {
            Uid = evt.Uid,
            Summary = evt.Summary,
            Description = evt.Description,
            Location = evt.Location,
            DtStart = BuildDateTime(evt, isStart: true),
            DtEnd = BuildDateTime(evt, isStart: false),
            DtStamp = new CalDateTime(DateTimeOffset.UtcNow.UtcDateTime, "UTC"),
        };

        if (evt.IsRecurring && CalDesk.Core.Recurrence.RecurrencePattern.Parse(evt.RecurrenceRule)?.ToRrule() is { } rrule)
        {
            ve.RecurrenceRules.Add(new RecurrencePattern(rrule));
        }

        cal.Events.Add(ve);
        return new CalendarSerializer().SerializeToString(cal);
    }

    private static string EditInPlace(string raw, CalendarEvent evt)
    {
        try
        {
            var cal = Ical.Net.Calendar.Load(new StringReader(raw));
            var ve = cal.Events.FirstOrDefault(e => string.Equals(e.Uid, evt.Uid, StringComparison.Ordinal))
                     ?? cal.Events.FirstOrDefault();
            if (ve is null)
            {
                return BuildICalendar(evt);
            }

            ve.Summary = evt.Summary;
            ve.Description = evt.Description;
            ve.Location = evt.Location;
            ve.DtStart = BuildDateTime(evt, isStart: true);
            ve.DtEnd = BuildDateTime(evt, isStart: false);

            // Replace recurrence rules when the local rule changed.
            if (evt.IsRecurring)
            {
                var rrule = CalDesk.Core.Recurrence.RecurrencePattern.Parse(evt.RecurrenceRule)?.ToRrule();
                if (rrule is not null)
                {
                    ve.RecurrenceRules.Clear();
                    ve.RecurrenceRules.Add(new RecurrencePattern(rrule));
                }
            }
            else
            {
                ve.RecurrenceRules.Clear();
            }

            return new CalendarSerializer().SerializeToString(cal);
        }
        catch
        {
            return BuildICalendar(evt);
        }
    }

    private static CalDateTime BuildDateTime(CalendarEvent evt, bool isStart)
    {
        var utc = (isStart ? evt.StartUtc : evt.EndUtc).UtcDateTime;

        if (evt.IsAllDay)
        {
            var d = utc.Date;
            return new CalDateTime(d.Year, d.Month, d.Day);
        }

        if (evt.IsFloating)
        {
            var local = utc.ToLocalTime();
            return new CalDateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, local.Second, null);
        }

        if (!string.IsNullOrEmpty(evt.TimeZoneId) && ResolveTz(evt.TimeZoneId) is { } tz)
        {
            var wall = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
            return new CalDateTime(wall.Year, wall.Month, wall.Day, wall.Hour, wall.Minute, wall.Second, evt.TimeZoneId);
        }

        return new CalDateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, "UTC");
    }

    private static TimeZoneInfo? ResolveTz(string tzId)
    {
        if (string.IsNullOrEmpty(tzId))
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(tzId);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
