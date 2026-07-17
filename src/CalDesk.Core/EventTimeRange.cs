namespace CalDesk.Core;

/// <summary>Outcome of <see cref="EventTimeRange.TryCompute"/>.</summary>
public enum EventTimeRangeStatus
{
    Ok,
    TimeParseFailed,
    RangeInvalid,
    /// <summary>Local wall-clock time falls in a spring-forward gap (invalid).</summary>
    InvalidLocalTime,
}

/// <summary>Result of computing StartUtc/EndUtc for a QuickAdd save.</summary>
public sealed record EventTimeRangeResult(
    EventTimeRangeStatus Status,
    DateTimeOffset StartUtc = default,
    DateTimeOffset EndUtc = default,
    bool StartTimeInvalid = false,
    bool EndTimeInvalid = false,
    bool RangeInvalid = false,
    /// <summary>
    /// When overnight same-day push applies, the EndDate that was actually used
    /// (start + 1 day). Null when EndDate was not adjusted.
    /// </summary>
    DateOnly? AdjustedEndDate = null,
    string? Message = null);

/// <summary>
/// Pure save time-range computation for QuickAdd.
/// All-day exclusive end = next local midnight after inclusive EndDate (DST-correct).
/// Timed: EndDate &lt; StartDate → reject; same-day end≤start → +1 day overnight;
/// multi-day uses EndDate as-is; spring-forward invalid local times → reject.
/// Ambiguous fall-back times: <see cref="TimeZoneInfo.GetUtcOffset(DateTime)"/> picks one
/// (standard/first on .NET); never throws.
/// </summary>
public static class EventTimeRange
{
    public static EventTimeRangeResult TryCompute(
        bool isAllDay,
        DateOnly startDate,
        DateOnly endDate,
        string? startTimeText,
        string? endTimeText,
        TimeZoneInfo tz)
    {
        if (isAllDay)
        {
            if (endDate < startDate)
            {
                return new EventTimeRangeResult(
                    EventTimeRangeStatus.RangeInvalid,
                    RangeInvalid: true,
                    Message: "结束日期不能早于开始日期");
            }

            // Inclusive EndDate → exclusive DTEND = local midnight of EndDate+1 (not +24h).
            var startUtc = AllDayDate.ToUtcInstant(startDate, tz);
            var endUtc = AllDayDate.ToUtcInstant(endDate.AddDays(1), tz);
            return new EventTimeRangeResult(EventTimeRangeStatus.Ok, startUtc, endUtc);
        }

        var startOk = TimeText.TryParse(startTimeText, out var startTod);
        var endOk = TimeText.TryParse(endTimeText, out var endTod);
        if (!startOk || !endOk)
        {
            return new EventTimeRangeResult(
                EventTimeRangeStatus.TimeParseFailed,
                StartTimeInvalid: !startOk,
                EndTimeInvalid: !endOk,
                Message: "无法解析时间，请使用 0930 或 09:30");
        }

        if (endDate < startDate)
        {
            // Explicit earlier EndDate must not be silently rewritten to overnight.
            return new EventTimeRangeResult(
                EventTimeRangeStatus.RangeInvalid,
                RangeInvalid: true,
                Message: "结束日期不能早于开始日期");
        }

        DateOnly effectiveEndDate = endDate;
        DateOnly? adjustedEndDate = null;

        if (endDate == startDate && endTod.CompareTo(startTod) <= 0)
        {
            // Legitimate same-day overnight (e.g. 22:00 → 06:00).
            effectiveEndDate = startDate.AddDays(1);
            adjustedEndDate = effectiveEndDate;
        }

        var startMapped = TryLocalToUtc(startDate, startTod, tz, out var startUtcTimed, out var startInvalid);
        var endMapped = TryLocalToUtc(effectiveEndDate, endTod, tz, out var endUtcTimed, out var endInvalid);
        if (!startMapped || !endMapped)
        {
            return new EventTimeRangeResult(
                EventTimeRangeStatus.InvalidLocalTime,
                StartTimeInvalid: !startMapped && startInvalid,
                EndTimeInvalid: !endMapped && endInvalid,
                Message: "该本地时间在夏令时切换中无效，请选择其他时间");
        }

        // Multi-day with end still ≤ start should not happen for well-formed times, but guard.
        if (endUtcTimed <= startUtcTimed)
        {
            return new EventTimeRangeResult(
                EventTimeRangeStatus.RangeInvalid,
                RangeInvalid: true,
                EndTimeInvalid: true,
                Message: "结束时间必须晚于开始时间");
        }

        return new EventTimeRangeResult(
            EventTimeRangeStatus.Ok,
            startUtcTimed,
            endUtcTimed,
            AdjustedEndDate: adjustedEndDate);
    }

    /// <summary>
    /// Build a UTC instant from a local civil date+time in <paramref name="tz"/>.
    /// Rejects spring-forward gaps (<see cref="TimeZoneInfo.IsInvalidTime"/>).
    /// Ambiguous fall-back: uses <see cref="TimeZoneInfo.GetUtcOffset(DateTime)"/> (no throw).
    /// </summary>
    public static bool TryLocalToUtc(
        DateOnly date,
        TimeOnly time,
        TimeZoneInfo tz,
        out DateTimeOffset utc,
        out bool isInvalidLocal)
    {
        utc = default;
        isInvalidLocal = false;
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);

        if (tz.IsInvalidTime(local))
        {
            isInvalidLocal = true;
            return false;
        }

        // Ambiguous: GetUtcOffset picks one offset (typically standard time on .NET); never throw.
        utc = new DateTimeOffset(local, tz.GetUtcOffset(local));
        return true;
    }
}
