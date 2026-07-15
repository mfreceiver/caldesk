using CalDesk.Core.Models;

namespace CalDesk.Core.Services;

/// <summary>Manages local calendar selection / ordering (display-side).</summary>
public sealed class CalendarService(ICalendarRepository calendars)
{
    public Task<IReadOnlyList<CalendarInfo>> GetAllAsync(CancellationToken ct = default)
        => calendars.GetAllAsync(ct);

    public async Task<IReadOnlyList<CalendarInfo>> GetSelectedAsync(CancellationToken ct = default)
    {
        var all = await calendars.GetAllAsync(ct).ConfigureAwait(false);
        return all.Where(c => c.IsSelected).OrderBy(c => c.SortOrder).ThenBy(c => c.DisplayName).ToList();
    }

    public async Task SetSelectedAsync(Guid id, bool selected, CancellationToken ct = default)
    {
        var cal = await calendars.GetAsync(id, ct).ConfigureAwait(false);
        if (cal is null || cal.IsSelected == selected)
        {
            return;
        }

        cal.IsSelected = selected;
        cal.UpdatedAt = DateTimeOffset.UtcNow;
        await calendars.UpsertAsync(cal, ct).ConfigureAwait(false);
    }
}
