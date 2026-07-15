namespace CalDesk.Core.Models;

/// <summary>A concrete occurrence of a (possibly recurring) event within a display range.</summary>
public sealed class EventOccurrence
{
    public required CalendarEvent Source { get; init; }

    public required CalendarInfo Calendar { get; init; }

    /// <summary>Start instant of this occurrence in UTC.</summary>
    public required DateTimeOffset StartUtc { get; init; }

    /// <summary>End instant of this occurrence in UTC.</summary>
    public required DateTimeOffset EndUtc { get; init; }

    public bool IsAllDay => Source.IsAllDay;

    public bool IsException => Source.IsExceptionOverride;

    public string Summary => Source.Summary;

    public string? Location => Source.Location;

    public string? Description => Source.Description;

    public string? Color => Calendar.Color;
}
