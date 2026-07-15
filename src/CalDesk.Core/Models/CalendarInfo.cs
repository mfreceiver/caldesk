namespace CalDesk.Core.Models;

/// <summary>A remote calendar collection discovered on an account.</summary>
public sealed class CalendarInfo
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AccountId { get; set; }

    /// <summary>Full URL of the calendar collection, e.g. <c>https://host/mar/work/</c>.</summary>
    public string RemoteUrl { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Calendar color (<c>#rrggbb</c> or <c>#aarrggbb</c>), if provided by the server.</summary>
    public string? Color { get; set; }

    public int SortOrder { get; set; }

    /// <summary>Whether the server advertised a WebDAV sync-token for this calendar.</summary>
    public bool SupportsSyncToken { get; set; }

    /// <summary>Last seen CalendarServer ctag (cheap change probe).</summary>
    public string? Ctag { get; set; }

    /// <summary>Last seen WebDAV sync-token for incremental sync.</summary>
    public string? SyncToken { get; set; }

    /// <summary>Whether the user wants this calendar shown in the week view.</summary>
    public bool IsSelected { get; set; } = true;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
