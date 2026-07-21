using CalDesk.Core.Models;

namespace CalDesk.Core.Services;

// Local persistence contracts. Implementations live in CalDesk.Storage (SQLite).

public interface IAccountRepository
{
    Task<IReadOnlyList<Account>> GetAllAsync(CancellationToken ct = default);
    Task<Account?> GetAsync(Guid id, CancellationToken ct = default);
    Task UpsertAsync(Account account, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface ICalendarRepository
{
    Task<IReadOnlyList<CalendarInfo>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CalendarInfo>> GetByAccountAsync(Guid accountId, CancellationToken ct = default);
    Task<CalendarInfo?> GetAsync(Guid id, CancellationToken ct = default);
    Task<CalendarInfo?> FindByRemoteUrlAsync(string remoteUrl, CancellationToken ct = default);
    Task UpsertAsync(CalendarInfo calendar, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task ClearAllSyncTokensAsync(CancellationToken ct = default);
}

public interface IEventRepository
{
    Task<CalendarEvent?> GetAsync(Guid id, CancellationToken ct = default);
    Task<CalendarEvent?> FindByUidAsync(Guid calendarId, string uid, CancellationToken ct = default);
    Task<CalendarEvent?> FindByRemoteHrefAsync(string href, CancellationToken ct = default);

    /// <summary>Events (non-deleted) in the given calendars overlapping <c>[startUtc, endUtc)</c>,
    /// including recurring events whose series might produce occurrences in the range. Full-weight:
    /// selects all columns including <see cref="CalendarEvent.ICalendarData"/>. Required by
    /// ReminderService (ParseTriggerMinutes reads VALARM on every event regardless of recurrence,
    /// and recurrence expansion needs the raw iCalendar payload).</summary>
    Task<IReadOnlyList<CalendarEvent>> GetInRangeAsync(
        IReadOnlyCollection<Guid> calendarIds,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken ct = default);

    /// <summary>T7: lightweight display-path query — same WHERE/ORDER as
    /// <see cref="GetInRangeAsync"/> but omits the <c>icalendar_data</c> column from the SELECT
    /// (T7-C1). Returned events have <see cref="CalendarEvent.ICalendarData"/> = "" — callers that
    /// need the raw payload for recurrence expansion must backfill via
    /// <see cref="GetRecurringDataAsync"/>. ReminderService MUST NOT use this method
    /// (it needs ICalendarData for every event).</summary>
    Task<IReadOnlyList<CalendarEvent>> GetInRangeLightweightAsync(
        IReadOnlyCollection<Guid> calendarIds,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken ct = default);

    /// <summary>T7: batch fetch of <c>icalendar_data</c> by event ID. Used by the display path
    /// to backfill the recurring subset after a <see cref="GetInRangeLightweightAsync"/> call.
    /// Returns a dictionary keyed by event ID; events not present in storage are absent from the
    /// dictionary. Empty <paramref name="ids"/> short-circuits to an empty dictionary (no SQL).</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetRecurringDataAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken ct = default);

    Task<IReadOnlyList<CalendarEvent>> GetPendingSyncAsync(CancellationToken ct = default);

    Task UpsertAsync(CalendarEvent evt, CancellationToken ct = default);
    Task MarkDeletedAsync(Guid id, CancellationToken ct = default);
    Task HardDeleteAsync(Guid id, CancellationToken ct = default);
    Task HardDeleteAllAsync(CancellationToken ct = default);
}

public interface ISyncStateRepository
{
    Task UpsertSyncTokenAsync(Guid calendarId, string? syncToken, CancellationToken ct = default);
    Task<string?> GetSyncTokenAsync(Guid calendarId, CancellationToken ct = default);
    Task SaveCtagAsync(Guid calendarId, string? ctag, CancellationToken ct = default);
    Task<string?> GetCtagAsync(Guid calendarId, CancellationToken ct = default);
    Task ClearAllAsync(CancellationToken ct = default);
}

public interface ISyncOperationRepository
{
    Task EnqueueAsync(SyncOperation op, CancellationToken ct = default);
    Task<IReadOnlyList<SyncOperation>> GetDueAsync(int limit, CancellationToken ct = default);
    Task MarkAttemptedAsync(long id, string? error, DateTimeOffset? nextAttemptAt, CancellationToken ct = default);
    Task RemoveAsync(long id, CancellationToken ct = default);

    /// <summary>Counts of pending outbox rows grouped by operation kind (Create/Update/Delete).</summary>
    Task<PendingSyncCounts> GetPendingCountsAsync(CancellationToken ct = default);
    Task ClearAllAsync(CancellationToken ct = default);
}
