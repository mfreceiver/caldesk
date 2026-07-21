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

    /// <summary>Saturday or Sunday — drives the weekend shade trigger in the week view.</summary>
    public bool IsWeekend { get; init; }

    /// <summary>0=Monday..6=Sunday, matching <see cref="CalendarDateMath.DaysSinceMonday"/>.</summary>
    public required int WeekIndex { get; init; }

    public bool IsEmpty => AllDay.Count == 0 && Timed.Count == 0;

    /// <summary>All-day occurrences (shown first / above timed).</summary>
    public required IReadOnlyList<OccurrenceItem> AllDay { get; init; }

    public required IReadOnlyList<OccurrenceItem> Timed { get; init; }

    /// <summary>All occurrences in display order: all-day first, then timed.</summary>
    public IReadOnlyList<OccurrenceItem> AllEvents { get; init; } = Array.Empty<OccurrenceItem>();

    public IReadOnlyList<OccurrenceItem> LeftEvents { get; init; } = Array.Empty<OccurrenceItem>();

    public IReadOnlyList<OccurrenceItem> RightEvents { get; init; } = Array.Empty<OccurrenceItem>();
}

/// <summary>A display row for a single occurrence within a day.</summary>
public sealed class OccurrenceItem
{
    public required Guid EventId { get; init; }

    public required string Summary { get; init; }

    /// <summary>"全天" for all-day, else "HH:mm" in the display time zone.</summary>
    public required string TimeLabel { get; init; }

    /// <summary>Display range in the display time zone, used by month day details.</summary>
    public required string TimeRangeLabel { get; init; }

    public bool IsAllDay { get; init; }

    /// <summary>Currently in progress (highlighted per G2-10).</summary>
    public bool IsOngoing { get; init; }

    public string? Color { get; init; }

    public DateTimeOffset StartUtc { get; init; }

    public DateTimeOffset EndUtc { get; init; }
}
