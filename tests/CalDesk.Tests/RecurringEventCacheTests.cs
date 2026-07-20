using CalDesk.CalDav.Calendar;
using CalDesk.CalDav.Recurrence;
using CalDesk.Core.Models;

namespace CalDesk.Tests;

public sealed class RecurringEventCacheTests
{
    private static string MakeIcal(string uid, string rrule = "FREQ=DAILY;INTERVAL=1")
    {
        var start = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var evt = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Uid = uid,
            Summary = "Recurring",
            StartUtc = start,
            EndUtc = start.AddHours(1),
            RecurrenceRule = rrule,
        };
        return IcalMapper.Serialize(evt);
    }

    [Fact]
    public void SameKey_ParsesOnce_ReturnsSameInstance()
    {
        var cache = new RecurringEventCache();
        var id = Guid.NewGuid();
        var etag = "\"v1\"";
        var updated = DateTimeOffset.Parse("2026-07-13T00:00:00Z");
        var ical = MakeIcal("rec-cache-1");

        var a = cache.GetOrParse(id, etag, updated, ical);
        var b = cache.GetOrParse(id, etag, updated, ical);

        Assert.NotNull(a);
        Assert.Same(a, b);
        Assert.Same(a!.Calendar, b!.Calendar);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void ETagChange_Reparses()
    {
        var cache = new RecurringEventCache();
        var id = Guid.NewGuid();
        var updated = DateTimeOffset.Parse("2026-07-13T00:00:00Z");
        var ical = MakeIcal("rec-cache-2");

        var a = cache.GetOrParse(id, "\"v1\"", updated, ical);
        var b = cache.GetOrParse(id, "\"v2\"", updated, ical);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotSame(a, b);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void UpdatedAtChange_Reparses()
    {
        var cache = new RecurringEventCache();
        var id = Guid.NewGuid();
        var etag = "\"v1\"";
        var ical = MakeIcal("rec-cache-3");

        var a = cache.GetOrParse(id, etag, DateTimeOffset.Parse("2026-07-13T00:00:00Z"), ical);
        var b = cache.GetOrParse(id, etag, DateTimeOffset.Parse("2026-07-14T00:00:00Z"), ical);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotSame(a, b);
    }

    [Fact]
    public void ParseFailure_IsNegativeCached()
    {
        var cache = new RecurringEventCache();
        var id = Guid.NewGuid();
        var etag = "\"bad\"";
        var updated = DateTimeOffset.UtcNow;
        const string junk = "this is not icalendar data at all";

        var a = cache.GetOrParse(id, etag, updated, junk);
        var b = cache.GetOrParse(id, etag, updated, junk);

        Assert.Null(a);
        Assert.Null(b);
        // Negative entry still occupies a slot (miss vs cached-null distinction).
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void OverCapacity_Truncates()
    {
        var cache = new RecurringEventCache();
        var updated = DateTimeOffset.UtcNow;
        var ical = MakeIcal("rec-capacity");

        // Fill to the cap.
        for (var i = 0; i < RecurringEventCache.MaxCapacity; i++)
        {
            var p = cache.GetOrParse(Guid.NewGuid(), $"e{i}", updated, ical);
            Assert.NotNull(p);
        }

        Assert.Equal(RecurringEventCache.MaxCapacity, cache.Count);

        // Next insert must not grow unbounded (clear-then-add policy → 1).
        var overflow = cache.GetOrParse(Guid.NewGuid(), "overflow", updated, ical);
        Assert.NotNull(overflow);
        Assert.True(cache.Count <= RecurringEventCache.MaxCapacity,
            $"expected Count <= {RecurringEventCache.MaxCapacity}, got {cache.Count}");
        Assert.Equal(1, cache.Count); // clear-then-add
    }

    [Fact]
    public void Expander_WithCache_HitsCacheOnSecondExpand()
    {
        var cache = new CountingCache();
        var expander = new IcalRecurrenceExpander(cache);

        var start = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var evt = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Uid = "rec-expand-hit",
            Summary = "R",
            StartUtc = start,
            EndUtc = start.AddHours(1),
            RecurrenceRule = "FREQ=DAILY;INTERVAL=1",
            ETag = "\"e1\"",
            UpdatedAt = DateTimeOffset.Parse("2026-07-13T00:00:00Z"),
        };
        evt.ICalendarData = IcalMapper.Serialize(evt);

        var rangeStart = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);
        var rangeEnd = rangeStart.AddDays(7);
        var cal = new CalendarInfo();

        var first = expander.Expand(evt, cal, rangeStart, rangeEnd).ToList();
        var second = expander.Expand(evt, cal, rangeStart, rangeEnd).ToList();

        Assert.True(first.Count >= 7);
        Assert.Equal(first.Count, second.Count);
        Assert.Equal(1, cache.ParseCount);
        Assert.Equal(2, cache.GetCount);
    }

    /// <summary>Spy cache that counts parse vs hit.</summary>
    private sealed class CountingCache : IRecurringEventCache
    {
        private readonly RecurringEventCache _inner = new();
        public int ParseCount { get; private set; }
        public int GetCount { get; private set; }

        public ParsedCalendar? GetOrParse(Guid id, string? etag, DateTimeOffset updatedAt, string icalendarData)
        {
            GetCount++;
            var before = _inner.Count;
            // Detect miss: if key not present, inner will grow (or stay same only on race).
            // Use a side map to know if this key was seen.
            var key = (id, etag, updatedAt);
            var wasMiss = !_seen.Contains(key);
            var result = _inner.GetOrParse(id, etag, updatedAt, icalendarData);
            if (wasMiss)
            {
                ParseCount++;
                _seen.Add(key);
            }

            _ = before;
            return result;
        }

        private readonly HashSet<(Guid, string?, DateTimeOffset)> _seen = new();
    }
}
