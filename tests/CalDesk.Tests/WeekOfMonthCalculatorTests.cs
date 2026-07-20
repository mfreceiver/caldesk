using CalDesk.Core.Services;
using Xunit;

namespace CalDesk.Tests;

/// <summary>W8 单测矩阵（§3.10）。周四锚 + W=((thu.Day-1)/7)+1。</summary>
public sealed class WeekOfMonthCalculatorTests
{
    [Theory]
    [InlineData("2026-06-29", 7, 1)]   // 跨月周：7/2 周四 → 7月第1周
    [InlineData("2026-07-06", 7, 2)]   // 7/9 周四
    [InlineData("2026-07-13", 7, 3)]   // 7/16 周四
    [InlineData("2026-07-20", 7, 4)]   // 7/23 周四（今天）
    [InlineData("2026-07-27", 7, 5)]   // 7/30 周四
    [InlineData("2025-12-29", 1, 1)]   // 跨年周：2026-01-01 周四 → 1月第1周
    [InlineData("2026-08-31", 9, 1)]   // 跨月周：9/3 周四 → 9月第1周
    [InlineData("2026-06-01", 6, 1)]   // 6/4 周四 → 6月第1周
    public void Calculate_ThursdayAnchor(string mondayIso, int expectedMonth, int expectedWeek)
    {
        var monday = DateOnly.Parse(mondayIso);
        Assert.Equal(DayOfWeek.Monday, monday.DayOfWeek);

        var (month, week) = WeekOfMonthCalculator.Calculate(monday);
        Assert.Equal(expectedMonth, month);
        Assert.Equal(expectedWeek, week);
    }

    [Theory]
    [InlineData("2026-06-29", "7月第1周")]
    [InlineData("2026-07-06", "7月第2周")]
    [InlineData("2026-07-13", "7月第3周")]
    [InlineData("2026-07-20", "7月第4周")]
    [InlineData("2026-07-27", "7月第5周")]
    [InlineData("2025-12-29", "1月第1周")]
    [InlineData("2026-08-31", "9月第1周")]
    [InlineData("2026-06-01", "6月第1周")]
    public void Header_ReturnsLocalizedText(string mondayIso, string expected)
    {
        var monday = DateOnly.Parse(mondayIso);
        Assert.Equal(expected, WeekOfMonthCalculator.Header(monday));
    }
}
