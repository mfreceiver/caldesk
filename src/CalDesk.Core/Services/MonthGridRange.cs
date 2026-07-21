namespace CalDesk.Core.Services;

/// <summary>Month-grid range aligned to Mon–Sun weeks, always rowCount×7 days.
/// Shared by <see cref="CalendarViewService.BuildMonth"/> (Core) and
/// <c>MonthViewModel.LoadAsync</c> (App) so the rendered grid and the queried
/// event range always match. The range is adaptive: normally 5 rows, but extends to
/// 6 rows when the month spans six weeks (e.g., a 31-day month starting Saturday).</summary>
public readonly record struct MonthGridRange(DateOnly Start, DateOnly EndExclusive)
{
    /// <summary>Number of Mon–Sun rows covered by this range (EndExclusive - Start) / 7.</summary>
    public int RowCount => (EndExclusive.DayNumber - Start.DayNumber) / 7;

/// <summary>Build a month grid for <paramref name="year"/>/<paramref name="month"/>.
/// <para><see cref="Start"/> = the Monday of the week containing the first day of the month.</para>
/// <para><see cref="EndExclusive"/> = <see cref="Start"/> + <paramref name="rowCount"/>*7 days.</para>
/// <para>When <paramref name="rowCount"/> is omitted (or &lt; 1), automatically chooses
/// the smallest row count between 5 and 6 that fully covers the month. This ensures
/// the month view uses 5 rows normally and 6 rows only when necessary (e.g.,
/// February never needs 6, but a 31-day month starting on Saturday does).</para></summary>
public static MonthGridRange For(int year, int month, int rowCount = -1)
{
    var first = new DateOnly(year, month, 1);
    var start = first.AddDays(-CalendarDateMath.DaysSinceMonday(first));
    if (rowCount < 1)
    {
        var lastDay = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        var spanDays = lastDay.DayNumber - start.DayNumber + 1;
        var rowsNeeded = (spanDays + 6) / 7; // ceiling division
        rowCount = Math.Max(5, rowsNeeded);
    }
    return new MonthGridRange(start, start.AddDays(rowCount * 7));
}
}
