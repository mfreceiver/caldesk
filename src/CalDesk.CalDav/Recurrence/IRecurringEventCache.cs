namespace CalDesk.CalDav.Recurrence;

/// <summary>Caches parsed iCalendar documents for recurring events so Expand
/// does not re-run <c>Ical.Net.Calendar.Load</c> on every view/reminder pass.</summary>
public interface IRecurringEventCache
{
    /// <summary>
    /// Returns a previously parsed calendar for the given identity tuple, or parses
    /// <paramref name="icalendarData"/> and stores the result.
    /// A cached <c>null</c> means a prior parse failed (negative cache).
    /// </summary>
    ParsedCalendar? GetOrParse(Guid id, string? etag, DateTimeOffset updatedAt, string icalendarData);
}

/// <summary>Parsed VCALENDAR retained by <see cref="IRecurringEventCache"/>.</summary>
/// <param name="Uid">Primary VEVENT UID (best-effort; may be empty).</param>
/// <param name="Calendar">The loaded Ical.Net calendar graph.</param>
public sealed record ParsedCalendar(string Uid, Ical.Net.Calendar Calendar);
