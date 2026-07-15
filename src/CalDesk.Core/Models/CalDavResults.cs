namespace CalDesk.Core.Models;

/// <summary>A remote resource entry returned by calendar-query / sync-collection.</summary>
public sealed record RemoteEventEntry(string Href, string? ETag, string ICalendarData);

/// <summary>Result of a <c>REPORT calendar-query</c>.</summary>
public sealed record CalendarQueryResult(IReadOnlyList<RemoteEventEntry> Entries);

/// <summary>Result of a <c>PUT</c> (create/update). The new ETag may be null if the
/// server does not return one, in which case it must be refetched.</summary>
public sealed record PutResult(string Href, string? ETag);

/// <summary>Result of a WebDAV <c>sync-collection</c> REPORT.</summary>
public sealed record SyncCollectionResult(
    IReadOnlyList<RemoteEventEntry> Changed,
    IReadOnlyList<string> Deleted,
    string? NewSyncToken,
    bool Supported);
