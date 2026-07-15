using CalDesk.Core.Models;

namespace CalDesk.Core.Services;

/// <summary>CalDAV / WebDAV client bound to a single account. Implementations live in CalDesk.CalDav.</summary>
public interface ICalDavClient : IAsyncDisposable
{
    Account Account { get; }

    /// <summary>Discover all calendar collections reachable from the account.
    /// Tries well-known / principal / home-set, then falls back to a user-provided full calendar URL.</summary>
    Task<IReadOnlyList<CalendarInfo>> DiscoverCalendarsAsync(CancellationToken ct = default);

    /// <summary>Query VEVENTs in <c>[startUtc, endUtc)</c> via <c>REPORT calendar-query</c>.</summary>
    Task<CalendarQueryResult> QueryEventsAsync(
        CalendarInfo calendar,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken ct = default);

    /// <summary>Create or update a remote event via <c>PUT</c>.
    /// Pass the known ETag in <paramref name="ifMatchEtag"/> for updates (optimistic concurrency),
    /// or null with <c>If-None-Match: *</c> semantics for creates.</summary>
    Task<PutResult> PutEventAsync(
        CalendarInfo calendar,
        CalendarEvent evt,
        string? ifMatchEtag,
        CancellationToken ct = default);

    /// <summary>Delete a remote event via <c>DELETE</c> with <c>If-Match</c>.</summary>
    Task DeleteEventAsync(
        CalendarInfo calendar,
        string href,
        string ifMatchEtag,
        CancellationToken ct = default);

    /// <summary>Incremental sync via WebDAV <c>sync-collection</c>. When unsupported by the
    /// server, returns <see cref="SyncCollectionResult.Supported"/> = false.</summary>
    Task<SyncCollectionResult> SyncCollectionAsync(
        CalendarInfo calendar,
        string? syncToken,
        CancellationToken ct = default);

    /// <summary>Refresh calendar-level metadata (ctag, sync-token, display name, color).</summary>
    Task<CalendarInfo> RefreshCalendarAsync(CalendarInfo calendar, CancellationToken ct = default);
}

/// <summary>Creates a configured, per-account <see cref="ICalDavClient"/> (resolves the password
/// from <see cref="ICredentialStore"/>). The returned client is owned by the caller.</summary>
public interface ICalDavClientFactory
{
    Task<ICalDavClient> CreateAsync(Account account, CancellationToken ct = default);
}
