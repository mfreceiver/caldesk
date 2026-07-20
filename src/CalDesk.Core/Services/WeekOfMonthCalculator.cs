namespace CalDesk.Core.Services;

/// <summary>W8：M月第W周。ISO-8601 周四锚——跨月周归属多数天所在月。
/// 调用方须保证传入周一（WeekStart=Monday）。7/20→7月第4周。</summary>
public static class WeekOfMonthCalculator
{
    public static (int Month, int Week) Calculate(DateOnly monday)
    {
        System.Diagnostics.Debug.Assert(monday.DayOfWeek == DayOfWeek.Monday,
            "WeekOfMonthCalculator expects a Monday; week-start change requires re-deriving the Thursday anchor.");
        var thu = monday.AddDays(3);
        return (thu.Month, ((thu.Day - 1) / 7) + 1);
    }

    public static string Header(DateOnly monday)
    {
        var (m, w) = Calculate(monday);
        return $"{m}月第{w}周";
    }
}
