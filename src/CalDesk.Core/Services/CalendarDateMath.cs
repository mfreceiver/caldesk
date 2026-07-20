namespace CalDesk.Core.Services;

/// <summary>Shared date arithmetic used by both <see cref="CalendarViewService"/> (Core)
/// and the App view models, so that week/month grid ranges are computed identically
/// on both sides (fixes the 35-day query vs 42-day render mismatch).</summary>
public static class CalendarDateMath
{
    /// <summary>0 for Monday .. 6 for Sunday.</summary>
    public static int DaysSinceMonday(DateOnly date) => ((int)date.DayOfWeek + 6) % 7;
}
