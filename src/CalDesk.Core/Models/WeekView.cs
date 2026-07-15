namespace CalDesk.Core.Models;

/// <summary>A built week-view: seven days, each with grouped occurrences.</summary>
public sealed class WeekView
{
    public required IReadOnlyList<WeekDay> Days { get; init; }

    /// <summary>Whether the view contains anything to show (false => empty-state "暂无事项").</summary>
    public bool HasAny => Days.Any(d => d.AllDay.Count + d.Timed.Count > 0);
}

public sealed class WeekDay
{
    public required DateOnly Date { get; init; }

    /// <summary>Display header, e.g. "周一 13".</summary>
    public required string Header { get; init; }

    public bool IsToday { get; init; }

    public bool IsEmpty => AllDay.Count == 0 && Timed.Count == 0;

    /// <summary>All-day occurrences (shown first / above timed).</summary>
    public required IReadOnlyList<OccurrenceItem> AllDay { get; init; }

    public required IReadOnlyList<OccurrenceItem> Timed { get; init; }
}

/// <summary>A display row for a single occurrence within a day.</summary>
public sealed class OccurrenceItem
{
    public required Guid EventId { get; init; }

    public required string Summary { get; init; }

    /// <summary>"全天" for all-day, else "HH:mm" in the display time zone.</summary>
    public required string TimeLabel { get; init; }

    public bool IsAllDay { get; init; }

    /// <summary>Currently in progress (highlighted per G2-10).</summary>
    public bool IsOngoing { get; init; }

    public string? Color { get; init; }

    public DateTimeOffset StartUtc { get; init; }

    public DateTimeOffset EndUtc { get; init; }
}
