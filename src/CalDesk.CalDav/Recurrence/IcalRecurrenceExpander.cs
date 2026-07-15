using CalDesk.Core.Models;
using CalDesk.Core.Recurrence;
using Ical.Net.CalendarComponents;
// The method signature works in terms of CalDesk.Core.Models.CalendarEvent; alias it so the
// reference is unambiguous against Ical.Net.CalendarComponents.CalendarEvent used elsewhere below.
using CalendarEvent = CalDesk.Core.Models.CalendarEvent;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;

namespace CalDesk.CalDav.Recurrence;

/// <summary>Expands events into occurrences using Ical.Net's recurrence engine
/// (handles RRULE / RDATE / EXDATE). Non-recurring events skip parsing for speed.</summary>
public sealed class IcalRecurrenceExpander : IRecurrenceExpander
{
    public IEnumerable<EventOccurrence> Expand(CalendarEvent evt, CalendarInfo calendar, DateTimeOffset rangeStart, DateTimeOffset rangeEnd)
    {
        if (!evt.IsRecurring)
        {
            if (Overlaps(evt.StartUtc, evt.EndUtc, rangeStart, rangeEnd))
            {
                yield return new EventOccurrence
                {
                    Source = evt,
                    Calendar = calendar,
                    StartUtc = evt.StartUtc,
                    EndUtc = evt.EndUtc,
                };
            }
            yield break;
        }

        if (string.IsNullOrWhiteSpace(evt.ICalendarData))
        {
            yield break;
        }

        Ical.Net.Calendar cal;
        try
        {
            cal = Ical.Net.Calendar.Load(new StringReader(evt.ICalendarData));
        }
        catch
        {
            yield break;
        }

        var ve = cal.Events.FirstOrDefault(e => string.Equals(e.Uid, evt.Uid, StringComparison.Ordinal))
                 ?? cal.Events.FirstOrDefault();
        if (ve is null)
        {
            yield break;
        }

        var startCal = new CalDateTime(rangeStart.UtcDateTime, "UTC");
        var rangeEndDt = rangeEnd.UtcDateTime;
        var options = new EvaluationOptions();
        var guard = 0;
        foreach (var occ in ve.GetOccurrences(startCal, options))
        {
            if (++guard > 2000)
            {
                yield break;
            }

            var startUtc = occ.Period.StartTime.AsUtc;
            if (startUtc >= rangeEndDt)
            {
                yield break;
            }

            var endUtc = occ.Period.EndTime?.AsUtc ?? startUtc;
            yield return new EventOccurrence
            {
                Source = evt,
                Calendar = calendar,
                StartUtc = new DateTimeOffset(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc)),
                EndUtc = new DateTimeOffset(DateTime.SpecifyKind(endUtc, DateTimeKind.Utc)),
            };
        }
    }

    private static bool Overlaps(DateTimeOffset start, DateTimeOffset end, DateTimeOffset rangeStart, DateTimeOffset rangeEnd)
        => start < rangeEnd && end > rangeStart;
}
