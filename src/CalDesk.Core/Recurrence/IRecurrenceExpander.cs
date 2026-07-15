using CalDesk.Core.Models;

namespace CalDesk.Core.Recurrence;

/// <summary>Expands a (possibly recurring) <see cref="CalendarEvent"/> into concrete
/// <see cref="EventOccurrence"/> instances overlapping a UTC display range.</summary>
public interface IRecurrenceExpander
{
    IEnumerable<EventOccurrence> Expand(
        CalendarEvent evt,
        CalendarInfo calendar,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc);
}
