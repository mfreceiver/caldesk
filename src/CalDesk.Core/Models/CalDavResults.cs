namespace CalDesk.Core.Models;

/// <summary>A remote resource entry returned by calendar-query / sync-collection.</summary>
public sealed record RemoteEventEntry(string Href, string? ETag, string ICalendarData);

/// <summary>Result of a <c>REPORT calendar-query</c>.</summary>
public sealed record CalendarQueryResult(IReadOnlyList<RemoteEventEntry> Entries);

/// <summary>Result of a <c>PUT</c> (create/update). The new ETag may be null if the
/// server does not return one, in which case it must be refetched.</summary>
public sealed record PutResult(string Href, string? ETag);

/// <summary>Result of a WebDAV <c>sync-collection</c> REPORT.</summary>
/// <remarks><paramref name="MissingDataHrefs"/> lists hrefs the server reported as changed
/// (non-404) whose response carried no calendar-data body (truncated response / non-compliant
/// server). Dropping them while adopting <paramref name="NewSyncToken"/> would lose those
/// changes permanently — consumers must refetch them by href (or record an error) before
/// advancing.</remarks>
public sealed record SyncCollectionResult(
    IReadOnlyList<RemoteEventEntry> Changed,
    IReadOnlyList<string> Deleted,
    string? NewSyncToken,
    bool Supported,
    IReadOnlyList<string>? MissingDataHrefs = null);