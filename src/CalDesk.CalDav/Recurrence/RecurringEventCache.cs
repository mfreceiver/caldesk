using System.Collections.Concurrent;

namespace CalDesk.CalDav.Recurrence;

/// <summary>
/// Thread-safe parse cache for recurring events.
/// Key = (Id, ETag, UpdatedAt); value may be null to negative-cache parse failures.
/// Capacity is hard-capped; on overflow the map is cleared (simple, bounded).
/// </summary>
public sealed class RecurringEventCache : IRecurringEventCache
{
    public const int MaxCapacity = 128;

    // ConcurrentDictionary allows null TValue for reference types; TryGetValue
    // distinguishes "miss" (false) from "cached null" (true, value=null).
    private readonly ConcurrentDictionary<(Guid Id, string? ETag, DateTimeOffset UpdatedAt), ParsedCalendar?> _map = new();

    /// <summary>Current entry count (for tests / diagnostics).</summary>
    public int Count => _map.Count;

    public ParsedCalendar? GetOrParse(Guid id, string? etag, DateTimeOffset updatedAt, string icalendarData)
    {
        var key = (id, etag, updatedAt);
        if (_map.TryGetValue(key, out var cached))
        {
            return cached;
        }

        ParsedCalendar? parsed = TryParse(icalendarData);

        // Bound growth: simple truncate — drop everything when full, then insert.
        if (_map.Count >= MaxCapacity)
        {
            _map.Clear();
        }

        // GetOrAdd wins races; first writer's parse is kept. A lost second parse is fine
        // (same inputs) and still cheaper than unbounded re-parse on the hot path.
        return _map.GetOrAdd(key, parsed);
    }

    private static ParsedCalendar? TryParse(string icalendarData)
    {
        try
        {
            var cal = Ical.Net.Calendar.Load(new StringReader(icalendarData));
            if (cal is null)
            {
                return null;
            }

            var uid = cal.Events.FirstOrDefault()?.Uid ?? string.Empty;
            return new ParsedCalendar(uid, cal);
        }
        catch
        {
            // Negative-cache failures so a corrupt blob is not re-thrown every frame.
            return null;
        }
    }
}
