using CalDesk.CalDav.Calendar;
using CalDesk.Core.Models;
using CalDesk.Core.Recurrence;
using Ical.Net.CalendarComponents;
// The method signature works in terms of CalDesk.Core.Models.CalendarEvent; alias it so the
// reference is unambiguous against Ical.Net.CalendarComponents.CalendarEvent used elsewhere below.
using CalendarEvent = CalDesk.Core.Models.CalendarEvent;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;

namespace CalDesk.CalDav.Recurrence;

/// <summary>Expands events into occurrences using Ical.Net's recurrence engine
/// (handles RRULE / RDATE / EXDATE). Non-recurring events skip parsing for speed.</summary>
public sealed class IcalRecurrenceExpander : IRecurrenceExpander
{
    /// <summary>Safety cap on how far back we ask Ical.Net to start evaluating, relative to
    /// rangeStart. Even very long events (e.g. a 7-day retreat) are covered; we just refuse to
    /// walk back unboundedly for pathological inputs.</summary>
    private static readonly TimeSpan MaxLookback = TimeSpan.FromDays(366);

    // G4: Ical.Net 5.2.3 GetOccurrences/Evaluator is not documented as thread-safe.
    // EventEvaluator is created once per CalendarEvent and Evaluate streams mutable state;
    // when the same ParsedCalendar is shared across UI + ReminderService threads we serialize
    // GetOccurrences on a process-wide lock (simpler than per-calendar locks; expand is cheap
    // relative to parse which is what the cache avoids).
    private static readonly object ExpandLock = new();

    private readonly IRecurringEventCache? _cache;

    /// <summary>
    /// <paramref name="cache"/> may be null (no-cache path) so existing unit tests that use the
    /// parameterless construction continue to work without injection.
    /// </summary>
    public IcalRecurrenceExpander(IRecurringEventCache? cache = null)
    {
        _cache = cache;
    }

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

        IcalEvent? ve;
        if (_cache is not null)
        {
            // Cached null = prior parse failure (negative cache) → skip.
            var parsed = _cache.GetOrParse(evt.Id, evt.ETag, evt.UpdatedAt, evt.ICalendarData);
            if (parsed is null)
            {
                yield break;
            }

            ve = parsed.Calendar.Events.FirstOrDefault(e => string.Equals(e.Uid, evt.Uid, StringComparison.Ordinal))
                 ?? parsed.Calendar.Events.FirstOrDefault();
        }
        else
        {
            Ical.Net.Calendar? cal;
            try
            {
                cal = Ical.Net.Calendar.Load(new StringReader(evt.ICalendarData));
            }
            catch
            {
                yield break;
            }
            if (cal is null)
            {
                yield break;
            }

            ve = cal.Events.FirstOrDefault(e => string.Equals(e.Uid, evt.Uid, StringComparison.Ordinal))
                 ?? cal.Events.FirstOrDefault();
        }

        if (ve is null)
        {
            yield break;
        }

        // An occurrence that STARTED before rangeStart but still overlaps the window must be
        // included. Ical.Net's GetOccurrences(dateStart) is documented to return occurrences
        // whose period intersects [dateStart, …], but the safest contract is to ask it to start
        // a little earlier — at least one event-duration before rangeStart — and then apply the
        // existing overlap predicate ourselves. The lookback is bounded so a 100-year event does
        // not pull in a century of generated occurrences.
        var occDuration = EffectiveDuration(ve);
        var lookback = occDuration > TimeSpan.Zero && occDuration <= MaxLookback ? occDuration : TimeSpan.Zero;
        var evalStartUtc = rangeStart.UtcDateTime - lookback;
        // Never ask for anything earlier than the event's own DTSTART (would just be wasted work).
        // Use CalDateTimeToUtc, NOT ve.DtStart.AsUtc: AsUtc relabels a FLOATING wall-clock value as
        // UTC (10:00 → 10:00Z), which would give a wrong DTSTART boundary and clip floating
        // recurring occurrences out of the evaluation window.
        if (ve.DtStart is { } dtStart)
        {
            var dtStartUtc = IcalMapper.CalDateTimeToUtc(dtStart);
            if (dtStartUtc > evalStartUtc)
            {
                evalStartUtc = dtStartUtc;
            }
        }

        var startCal = new CalDateTime(evalStartUtc, "UTC");
        var rangeEndDt = rangeEnd.UtcDateTime;
        var options = new EvaluationOptions();

        // Materialize under lock when sharing a cached Calendar (G4). Without a cache the
        // Calendar is thread-local to this call, so no lock is needed.
        List<(DateTime startUtc, DateTime endUtc)> materialised;
        if (_cache is not null)
        {
            lock (ExpandLock)
            {
                materialised = MaterialiseOccurrences(ve, startCal, options, occDuration, rangeEndDt);
            }
        }
        else
        {
            materialised = MaterialiseOccurrences(ve, startCal, options, occDuration, rangeEndDt);
        }

        foreach (var (startUtc, endUtc) in materialised)
        {
            // Final overlap filter: keep occurrences that actually intersect [rangeStart, rangeEnd).
            if (!Overlaps(
                    new DateTimeOffset(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc)),
                    new DateTimeOffset(DateTime.SpecifyKind(endUtc, DateTimeKind.Utc)),
                    rangeStart,
                    rangeEnd))
            {
                continue;
            }

            yield return new EventOccurrence
            {
                Source = evt,
                Calendar = calendar,
                StartUtc = new DateTimeOffset(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc)),
                EndUtc = new DateTimeOffset(DateTime.SpecifyKind(endUtc, DateTimeKind.Utc)),
            };
        }
    }

    private static List<(DateTime startUtc, DateTime endUtc)> MaterialiseOccurrences(
        IcalEvent ve,
        CalDateTime startCal,
        EvaluationOptions options,
        TimeSpan occDuration,
        DateTime rangeEndDt)
    {
        var list = new List<(DateTime, DateTime)>();
        var guard = 0;
        foreach (var occ in ve.GetOccurrences(startCal, options))
        {
            if (++guard > 2000)
            {
                break;
            }

            var startUtc = IcalMapper.CalDateTimeToUtc(occ.Period.StartTime);
            if (startUtc >= rangeEndDt)
            {
                break;
            }

            // In this Ical.Net build each occurrence's Period.EndTime is frequently null even
            // when DTEND/DURATION are set on the parent VEVENT — only Period.Duration is populated.
            // Recover the end from the parent's effective duration when needed.
            var endUtc = occ.Period.EndTime is not null
                ? IcalMapper.CalDateTimeToUtc(occ.Period.EndTime)
                : (occDuration > TimeSpan.Zero ? startUtc.Add(occDuration) : startUtc);

            list.Add((startUtc, endUtc));
        }

        return list;
    }

    private static TimeSpan EffectiveDuration(IcalEvent ve)
    {
        if (ve.DtStart is { } s && ve.DtEnd is { } e)
        {
            var delta = e.AsUtc - s.AsUtc;
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }

        if (ve.Duration is { } d)
        {
            var ts = d.ToTimeSpanUnspecified();
            return ts > TimeSpan.Zero ? ts : TimeSpan.Zero;
        }

        return TimeSpan.Zero;
    }

    private static bool Overlaps(DateTimeOffset start, DateTimeOffset end, DateTimeOffset rangeStart, DateTimeOffset rangeEnd)
        => start < rangeEnd && end > rangeStart;
}
