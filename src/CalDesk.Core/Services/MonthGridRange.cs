namespace CalDesk.Core.Services;

/// <summary>Month-grid range aligned to Mon–Sun weeks, always rowCount×7 days.
/// Shared by <see cref="CalendarViewService.BuildMonth"/> (Core) and
/// <c>MonthViewModel.LoadAsync</c> (App) so the rendered grid and the queried
/// event range always match — the renderer paints 6 rows (42 cells) while the
/// old ViewModel query computed a variable 5–6 row range via a different formula.</summary>
public readonly record struct MonthGridRange(DateOnly Start, DateOnly EndExclusive)
{
    /// <summary>Number of Mon–Sun rows covered by this range (EndExclusive - Start) / 7.</summary>
    public int RowCount => (EndExclusive.DayNumber - Start.DayNumber) / 7;

    /// <summary>Build a month grid for <paramref name="year"/>/<paramref name="month"/>.
    /// <para><see cref="Start"/> = the Monday of the week containing the first day of the month.</para>
    /// <para><see cref="EndExclusive"/> = <see cref="Start"/> + <paramref name="rowCount"/>*7 days.</para>
    /// Defaults to <paramref name="rowCount"/> = 6 so the month surface stays stable
    /// (prevents an empty UniformGrid row from becoming a large unpainted gap).</summary>
    public static MonthGridRange For(int year, int month, int rowCount = 6)
    {
        var first = new DateOnly(year, month, 1);
        var start = first.AddDays(-CalendarDateMath.DaysSinceMonday(first));
        return new MonthGridRange(start, start.AddDays(rowCount * 7));
    }
}
