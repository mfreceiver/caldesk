using CalDesk.Core.Models;

namespace CalDesk.Core.Services;

/// <summary>Holds the result of expanding events for a half-open <c>[rangeStart, rangeEndExclusive)</c>
/// interval, so that both the month grid (<see cref="CalendarViewService.BuildMonthFromExpanded"/>)
/// and the selected-day detail (<see cref="CalendarViewService.BuildDayFromExpanded"/>) can read
/// from a single expansion pass.
/// <para>This eliminates the duplicate GetInRangeAsync + expansion work that previously happened
/// in <c>MonthViewModel.LoadAsync</c> (one query/expansion for the month grid, then a second
/// query/expansion for the selected-day detail). With <see cref="ExpandedCalendarRange"/>, the
/// selected-day detail reuses the same already-bucketed occurrences — no second query, no second
/// expansion (T3).</para>
/// <para>Instances are built by <see cref="CalendarViewService.ExpandRange"/>; the
/// <c>internal</c> constructor keeps callers from constructing an empty/inconsistent range.</para></summary>
public sealed class ExpandedCalendarRange
{
    private readonly IReadOnlyDictionary<DateOnly, IReadOnlyList<OccurrenceItem>> _byDay;

    /// <summary>Constructs a range over the given per-day buckets. The dictionary reference is
    /// shared (no deep copy); <see cref="OccurrenceItem"/> is immutable so the sharing is safe.</summary>
    internal ExpandedCalendarRange(IReadOnlyDictionary<DateOnly, List<OccurrenceItem>> byDay)
    {
        // Project List<OccurrenceItem> → IReadOnlyList<OccurrenceItem> without copying the lists.
        // Cost: O(n_days) — at most ~42 for a month grid; negligible.
        _byDay = byDay.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<OccurrenceItem>)kv.Value);
    }

    /// <summary>Occurrences bucketed by their local-day start, as computed by the source
    /// <see cref="CalendarViewService.ExpandRange"/> call. Days inside the expansion range with no
    /// overlapping occurrences are present with an empty list; days outside the range are absent
    /// (use <see cref="GetDay"/> for a non-throwing accessor).
    /// <para><b>Mutation contract:</b> the returned lists are shared mutable <see cref="List{T}"/>
    /// instances surfaced read-only; callers MUST NOT mutate them (no add/remove/clear, no in-place
    /// element reordering). The contained <see cref="OccurrenceItem"/> values are init-only immutable,
    /// so read-only consumption is safe, but the list surface itself is not protected against writes
    /// — mutating it would corrupt subsequent <see cref="CalendarViewService.BuildMonthFromExpanded"/>
    /// / <see cref="CalendarViewService.BuildDayFromExpanded"/> reads from the same range.</para></summary>
    public IReadOnlyDictionary<DateOnly, IReadOnlyList<OccurrenceItem>> ByDay => _byDay;

    /// <summary>Occurrences whose local-day start equals <paramref name="date"/>. Returns an empty
    /// list (never throws) when <paramref name="date"/> is outside the expansion range.
    /// <para>Same <b>mutation contract</b> as <see cref="ByDay"/>: do not mutate the returned list.</para></summary>
    public IReadOnlyList<OccurrenceItem> GetDay(DateOnly date) =>
        _byDay.TryGetValue(date, out var bucket) ? bucket : Array.Empty<OccurrenceItem>();
}
