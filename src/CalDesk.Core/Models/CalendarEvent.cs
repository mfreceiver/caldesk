namespace CalDesk.Core.Models;

/// <summary>Local representation of a calendar event. Stores both denormalized
/// fields (for fast querying / display) and the full raw <see cref="ICalendarData"/>
/// so that unknown / <c>X-</c> properties survive an edit round-trip (NFR-05).</summary>
public sealed class CalendarEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Owning local calendar.</summary>
    public Guid CalendarId { get; set; }

    /// <summary>The stable iCalendar UID of the VEVENT.</summary>
    public string Uid { get; set; } = string.Empty;

    /// <summary>Server resource href, e.g. <c>/mar/work/&lt;uid&gt;.ics</c>.</summary>
    public string? RemoteHref { get; set; }

    /// <summary>Last known ETag from the server (for optimistic concurrency).</summary>
    public string? ETag { get; set; }

    /// <summary>Raw iCalendar object (full VCALENDAR). Preserved on edit.</summary>
    public string ICalendarData { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Location { get; set; }

    public bool IsAllDay { get; set; }

    /// <summary>Start instant in UTC.</summary>
    public DateTimeOffset StartUtc { get; set; }

    /// <summary>End instant in UTC.</summary>
    public DateTimeOffset EndUtc { get; set; }

    /// <summary>TZID of the event (e.g. <c>Asia/Shanghai</c>), or null when floating/UTC.</summary>
    public string? TimeZoneId { get; set; }

    /// <summary>True when DTSTART had neither <c>Z</c> nor TZID (interpreted in local time).</summary>
    public bool IsFloating { get; set; }

    /// <summary>Raw RRULE value(s), semicolon-separated if multiple.</summary>
    public string? RecurrenceRule { get; set; }

    /// <summary>RECURRENCE-ID for a single-occurrence override (exception), else null.</summary>
    public string? RecurrenceId { get; set; }

    public SyncStatus SyncStatus { get; set; } = SyncStatus.Clean;

    public DateTimeOffset? DeletedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsRecurring => !string.IsNullOrWhiteSpace(RecurrenceRule);

    public bool IsExceptionOverride => !string.IsNullOrWhiteSpace(RecurrenceId);
}
