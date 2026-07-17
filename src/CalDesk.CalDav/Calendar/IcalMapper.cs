using System.Globalization;
using CalDesk.Core;
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
    /// <summary>Parse a raw iCalendar object into one or more events. Returns an empty list
    /// when the resource is empty but parseable. <b>Does not</b> distinguish "parse failed"
    /// from "no events" — use <see cref="TryToCalendarEvents"/> from sync code where the
    /// distinction matters.</summary>
    public static List<CalendarEvent> ToCalendarEvents(string raw, Guid calendarId, string? href, string? etag)
    {
        return TryToCalendarEvents(raw, calendarId, href, etag).Events.ToList();
    }

    /// <summary>Parse with a typed outcome: a parse failure (G5-21 quarantine) is surfaced as
    /// <see cref="CalendarParseResult.Success"/> = false with <see cref="CalendarParseResult.Error"/>,
    /// rather than silently becoming an empty list. The caller (sync engine) must NOT advance
    /// past a resource that failed to parse — that would permanently lose the resource.</summary>
    public static CalendarParseResult TryToCalendarEvents(string raw, Guid calendarId, string? href, string? etag)
    {
        Ical.Net.Calendar cal;
        try
        {
            cal = Ical.Net.Calendar.Load(new StringReader(raw));
        }
        catch (Exception ex)
        {
            return new CalendarParseResult(
                Success: false,
                Events: new List<CalendarEvent>(),
                Error: ex.Message,
                Href: href,
                ETag: etag);
        }

        var events = new List<CalendarEvent>();
        foreach (var ve in cal.Events)
        {
            events.Add(Map(ve, calendarId, href, etag, raw));
        }

        return new CalendarParseResult(Success: true, Events: events, Error: null, Href: href, ETag: etag);
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
            RecurrenceRule = RuleToRrule(ve.RecurrenceRule),
            SyncStatus = SyncStatus.Clean,
        };
    }

    private static DateTimeOffset ToUtc(CalDateTime? dt)
    {
        if (dt is null)
        {
            return DateTimeOffset.UtcNow;
        }

        // All-day VALUE=DATE: map civil YYYYMMDD to local-midnight UTC so the date displays
        // as that same calendar day in TimeZoneInfo.Local (not floating UTC midnight).
        if (!dt.HasTime)
        {
            var date = new DateOnly(dt.Year, dt.Month, dt.Day);
            return AllDayDate.ToUtcInstant(date, TimeZoneInfo.Local);
        }

        var utc = dt.AsUtc;
        return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private static DateTimeOffset ResolveEnd(IcalEvent ve, CalDateTime? start, DateTimeOffset startUtc, bool isAllDay)
    {
        // 1) Explicit DTEND wins.
        if (ve.DtEnd is not null)
        {
            return ToUtc(ve.DtEnd);
        }

        // 2) DURATION: end = DTSTART + DURATION (RFC 5545 §3.6.1 — DTEND and DURATION are
        //    mutually exclusive but either may be present).
        if (ve.Duration is { } dur && dur.ToTimeSpanUnspecified() is { } ts && ts > TimeSpan.Zero)
        {
            return startUtc + ts;
        }

        // 3) RFC 5545 defaults when neither DTEND nor DURATION is present:
        //    all-day ⇒ exclusive next local midnight (DST-correct via AllDayDate), timed ⇒ zero-length.
        if (isAllDay && start is not null && !start.HasTime)
        {
            var next = new DateOnly(start.Year, start.Month, start.Day).AddDays(1);
            return AllDayDate.ToUtcInstant(next, TimeZoneInfo.Local);
        }

        return isAllDay ? startUtc.AddDays(1) : startUtc;
    }

    private static string? RuleToRrule(RecurrenceRule? p)
    {
        if (p is null)
        {
            return null;
        }

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
            ve.RecurrenceRule = new RecurrencePattern(rrule);
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
                    ve.RecurrenceRule = new RecurrencePattern(rrule);
                }
            }
            else
            {
                ve.RecurrenceRule = null;
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
        var instant = isStart ? evt.StartUtc : evt.EndUtc;
        var utc = instant.UtcDateTime;

        if (evt.IsAllDay)
        {
            // DATE must come from the local civil day of the stored instant, not utc.Date
            // (e.g. +08:00 midnight 7/15 = 7/14 16:00Z → utc.Date would emit 20260714).
            var d = AllDayDate.ToLocalDate(instant, TimeZoneInfo.Local);
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

/// <summary>Typed outcome of <see cref="IcalMapper.TryToCalendarEvents"/>. When
/// <see cref="Success"/> is false, <see cref="Events"/> is empty and <see cref="Error"/>
/// describes the parse failure; the caller must NOT advance past this resource.</summary>
public sealed record CalendarParseResult(
    bool Success,
    IReadOnlyList<CalendarEvent> Events,
    string? Error,
    string? Href,
    string? ETag);
