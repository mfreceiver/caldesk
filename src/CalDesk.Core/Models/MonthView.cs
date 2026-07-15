namespace CalDesk.Core.Models;

/// <summary>A built month-view: a 6-row × 7-col grid aligned to weeks (Mon–Sun),
/// with each cell carrying a compact event preview for calendar-grid rendering.</summary>
public sealed class MonthView
{
    public required int Year { get; init; }

    public required int Month { get; init; }

    /// <summary>Localized title, e.g. "2026年7月".</summary>
    public required string Title { get; init; }

    /// <summary>Weekday headers (周一..周日), shared with the week view.</summary>
    public required IReadOnlyList<string> WeekdayHeaders { get; init; }

    public required IReadOnlyList<MonthWeek> Weeks { get; init; }

    public bool HasAny => Weeks.Any(w => w.Days.Any(d => d.OccurrenceCount > 0));
}

/// <summary>One grid row of 7 days.</summary>
public sealed class MonthWeek
{
    public required IReadOnlyList<MonthDay> Days { get; init; }
}

/// <summary>One cell of the month grid.</summary>
public sealed class MonthDay
{
    public required DateOnly Date { get; init; }

    /// <summary>Day-of-month number (1..31).</summary>
    public required int Day { get; init; }

    /// <summary>False for days spilling over from the previous/next month (render greyed).</summary>
    public required bool IsInMonth { get; init; }

    public bool IsToday { get; init; }

    public bool IsWeekend { get; init; }

    public bool IsEmpty => OccurrenceCount == 0;

    public required int OccurrenceCount { get; init; }

    /// <summary>Compact preview rows for the cell (all-day first, then timed, capped at <see cref="MonthViewOptions.PreviewLimit"/>).</summary>
    public required IReadOnlyList<MonthOccurrence> Preview { get; init; }
}

/// <summary>A single compact row inside a month cell.</summary>
public sealed class MonthOccurrence
{
    public required Guid EventId { get; init; }

    public required string Summary { get; init; }

    /// <summary>"全天" for all-day, else "HH:mm" in the display time zone.</summary>
    public required string TimeLabel { get; init; }

    public bool IsAllDay { get; init; }

    public string? Color { get; init; }
}

/// <summary>Tuning knobs for month-view rendering.</summary>
public sealed class MonthViewOptions
{
    /// <summary>Max preview rows shown inside a single cell; extras are summarized by <see cref="MonthDay.OccurrenceCount"/>.</summary>
    public int PreviewLimit { get; init; } = 3;

    public static MonthViewOptions Default { get; } = new();
}
