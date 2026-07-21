using CalDesk.Core.Services;
using Xunit;

namespace CalDesk.Tests;

public class MonthGridRangeTests
{
    // 2025-06-01 is Sunday → needs 6 rows (36‑day span from May 26 to Jun 30).
    [Fact]
    public void June2025NeedsSixRows()
    {
        var g = MonthGridRange.For(2025, 6);
        Assert.Equal(42, g.EndExclusive.DayNumber - g.Start.DayNumber);
        Assert.Equal(6, g.RowCount);
    }

    [Fact]
    public void StartsOnMonday()
    {
        var g = MonthGridRange.For(2025, 6); // 2025-06-01 is Sunday → grid starts Mon 2025-05-26
        Assert.Equal(DayOfWeek.Monday, g.Start.DayOfWeek);
        Assert.Equal(new DateOnly(2025, 5, 26), g.Start);
    }

    // Boundary: February 2025 (short month) — only 5 rows needed.
    [Fact]
    public void February2025NeedsFiveRows()
    {
        var g = MonthGridRange.For(2025, 2); // 2025-02-01 is Saturday
        // Span: Jan 27 (Mon) → Feb 28 (Sat) = 33 days → ceil(33/7)=5
        Assert.Equal(5, g.RowCount);
        Assert.Equal(35, g.EndExclusive.DayNumber - g.Start.DayNumber);
        Assert.Equal(DayOfWeek.Monday, g.Start.DayOfWeek);
        // Sat → DaysSinceMonday = 5 → grid starts Mon 2025-01-27
        Assert.Equal(new DateOnly(2025, 1, 27), g.Start);
    }

    // Boundary: December 2025 — 5 rows, spills into Jan 2026.
    [Fact]
    public void December2025NeedsFiveRows()
    {
        var g = MonthGridRange.For(2025, 12); // 2025-12-01 is Monday
        // Span: Dec 1 (Mon) → Dec 31 (Wed) = 31 days → ceil(31/7)=5
        Assert.Equal(5, g.RowCount);
        Assert.Equal(35, g.EndExclusive.DayNumber - g.Start.DayNumber);
        Assert.Equal(DayOfWeek.Monday, g.Start.DayOfWeek);
        Assert.Equal(new DateOnly(2025, 12, 1), g.Start);
        Assert.Equal(new DateOnly(2026, 1, 5), g.EndExclusive);
        Assert.Equal(2026, g.EndExclusive.Year);
    }

    // Boundary: January 2026 — 5 rows, start spills into Dec 2025.
    [Fact]
    public void January2026NeedsFiveRows()
    {
        // 2026-01-01 is Thursday → DaysSinceMonday = 3 → grid starts Mon 2025-12-29.
        var g = MonthGridRange.For(2026, 1);
        // Span: Dec 29 (Mon) → Jan 31 (Sat) = 34 days → ceil(34/7)=5
        Assert.Equal(5, g.RowCount);
        Assert.Equal(35, g.EndExclusive.DayNumber - g.Start.DayNumber);
        Assert.Equal(new DateOnly(2025, 12, 29), g.Start);
        Assert.Equal(2025, g.Start.Year);
        Assert.Equal(DayOfWeek.Monday, g.Start.DayOfWeek);
        Assert.Equal(new DateOnly(2026, 2, 2), g.EndExclusive); // Dec 29 + 35 = Feb 2
    }

    // rowCount override: caller can request 5 (or other) rows when six isn't desired.
    [Fact]
    public void CustomRowCountProducesNRowsTimes7()
    {
        var g = MonthGridRange.For(2025, 6, rowCount: 5);
        Assert.Equal(35, g.EndExclusive.DayNumber - g.Start.DayNumber);
        Assert.Equal(5, g.RowCount);
    }

    // 31‑day month starting on Saturday → needs 6 rows.
    // 2026-08-01 is Saturday.
    [Fact]
    public void August2026NeedsSixRows()
    {
        var g = MonthGridRange.For(2026, 8);
        // Span: Jul 27 (Mon) → Aug 31 (Mon) = 36 days → ceil(36/7)=6
        Assert.Equal(6, g.RowCount);
        Assert.Equal(42, g.EndExclusive.DayNumber - g.Start.DayNumber);
        Assert.Equal(DayOfWeek.Monday, g.Start.DayOfWeek);
        Assert.Equal(new DateOnly(2026, 7, 27), g.Start);
        Assert.Equal(new DateOnly(2026, 9, 7), g.EndExclusive); // Jul 27 + 42 = Sep 7
    }
}
