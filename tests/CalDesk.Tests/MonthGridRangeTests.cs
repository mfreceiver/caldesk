using CalDesk.Core.Services;
using Xunit;

namespace CalDesk.Tests;

public class MonthGridRangeTests
{
    [Fact]
    public void AlwaysSixRows() =>
        Assert.Equal(42, MonthGridRange.For(2025, 6).EndExclusive.DayNumber - MonthGridRange.For(2025, 6).Start.DayNumber);

    [Fact]
    public void StartsOnMonday()
    {
        var g = MonthGridRange.For(2025, 6); // 2025-06-01 is Sunday → grid starts Mon 2025-05-26
        Assert.Equal(DayOfWeek.Monday, g.Start.DayOfWeek);
        Assert.Equal(new DateOnly(2025, 5, 26), g.Start);
    }

    // Boundary: February (short month — naturally 4 rows, must still pad to 6).
    [Fact]
    public void FebruaryAlwaysPadsToSixRows()
    {
        var g = MonthGridRange.For(2025, 2); // 2025-02-01 is Saturday
        Assert.Equal(42, g.EndExclusive.DayNumber - g.Start.DayNumber);
        Assert.Equal(DayOfWeek.Monday, g.Start.DayOfWeek);
        // Sat → DaysSinceMonday = 5 → grid starts Mon 2025-01-27
        Assert.Equal(new DateOnly(2025, 1, 27), g.Start);
    }

    // Boundary: December grid spills into next year (EndExclusive in following January).
    [Fact]
    public void DecemberEndExclusiveSpillsIntoNextYear()
    {
        var g = MonthGridRange.For(2025, 12); // 2025-12-01 is Monday
        Assert.Equal(42, g.EndExclusive.DayNumber - g.Start.DayNumber);
        Assert.Equal(DayOfWeek.Monday, g.Start.DayOfWeek);
        Assert.Equal(new DateOnly(2025, 12, 1), g.Start);
        Assert.Equal(new DateOnly(2026, 1, 12), g.EndExclusive);
        Assert.Equal(2026, g.EndExclusive.Year);
    }

    // Boundary: January grid start can spill into previous year.
    [Fact]
    public void JanuaryStartCanSpillIntoPreviousYear()
    {
        // 2026-01-01 is Thursday → DaysSinceMonday = 3 → grid starts Mon 2025-12-29.
        var g = MonthGridRange.For(2026, 1);
        Assert.Equal(new DateOnly(2025, 12, 29), g.Start);
        Assert.Equal(2025, g.Start.Year);
        Assert.Equal(DayOfWeek.Monday, g.Start.DayOfWeek);
    }

    // rowCount override: caller can request 5 (or other) rows when six isn't desired.
    [Fact]
    public void CustomRowCountProducesNRowsTimes7()
    {
        var g = MonthGridRange.For(2025, 6, rowCount: 5);
        Assert.Equal(35, g.EndExclusive.DayNumber - g.Start.DayNumber);
        Assert.Equal(5, g.RowCount);
    }

    [Fact]
    public void DefaultRowCountIsSix() => Assert.Equal(6, MonthGridRange.For(2025, 6).RowCount);
}
