using CalDesk.Core.Models;

namespace CalDesk.Core.Services;

/// <summary>Display-path extensions over <see cref="IEventRepository"/>. The two-phase load
/// (lightweight range query + on-demand recurring ICalendarData backfill) is identical across the
/// week/month view models and the month SelectDay slow path; centralising it here removes 3× 8-line
/// duplication and tightens the non-atomic window between the two queries (T7 nit).</summary>
public static class EventRepositoryDisplayExtensions
{
    /// <summary>Display-path loader: lightweight range query (<see cref="IEventRepository.GetInRangeLightweightAsync"/>)
    /// followed by an on-demand <see cref="IEventRepository.GetRecurringDataAsync"/> backfill of the
    /// recurring subset's raw <c>icalendar_data</c>. Non-recurring events keep
    /// <see cref="CalendarEvent.ICalendarData"/> = "" (the recurrence expander doesn't read it).
    /// <para><b>ReminderService MUST NOT use this</b> — that service reads VALARM via
    /// <c>ParseTriggerMinutes(evt)</c> on EVERY event (not just recurring) and so needs the
    /// full-weight <see cref="IEventRepository.GetInRangeAsync"/> path.</para>
    /// <para>The two underlying repository calls are not atomic (no transaction): the small window
    /// between them is acceptable for display (worst case = a recurring event whose row was
    /// deleted between calls appears empty and gets re-fetched on next navigation). The same
    /// non-atomic window existed at every inline call site before this helper existed.</para></summary>
    public static async Task<IReadOnlyList<CalendarEvent>> GetInRangeForDisplayAsync(
        this IEventRepository repo,
        IReadOnlyCollection<Guid> calendarIds,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken ct = default)
    {
        var stored = await repo.GetInRangeLightweightAsync(calendarIds, startUtc, endUtc, ct);
        var recurringIds = stored.Where(e => e.IsRecurring).Select(e => e.Id).ToArray();
        if (recurringIds.Length > 0)
        {
            var datas = await repo.GetRecurringDataAsync(recurringIds, ct);
            foreach (var e in stored)
                if (datas.TryGetValue(e.Id, out var d))
                    e.ICalendarData = d;
        }
        return stored;
    }
}
