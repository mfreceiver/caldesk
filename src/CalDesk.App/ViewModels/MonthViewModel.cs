using System.Threading;
using CalDesk.App.Models;
using CalDesk.App.Services;
using CalDesk.Core.Models;
using CalDesk.Core.Services;
using CalDesk.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace CalDesk.App.ViewModels;

/// <summary>View model for the month-grid window. Mirrors <see cref="WeekViewModel"/> but builds
/// a <see cref="MonthView"/> via <see cref="CalendarViewService.BuildMonth"/>.</summary>
public sealed partial class MonthViewModel : ObservableObject, IRecipient<CalendarColorsChangedMessage>, IDisposable
{
    private readonly StorageServices _store;
    private readonly CalendarViewService _viewService;
    private readonly SyncRunner _sync;
    private readonly Action<DateOnly?> _onAddNew;
    private readonly Action<Guid> _onEdit;

    /// <summary>
    /// Bumped on each <see cref="NavigateTo"/> so an in-flight <see cref="LoadAsync"/> from an older
    /// navigation cannot overwrite <see cref="Month"/> after a newer month was selected.
    /// </summary>
    private int _navVersion;

    /// <summary>Cached result of the most recent SUCCESSFUL <see cref="LoadAsync"/> expansion, so
    /// <see cref="SelectDay"/> can rebuild a day's detail without re-querying when the day is inside
    /// the cached expansion's range (T3 reuse). Not cleared at the start of a load — if a newer load
    /// races ahead, the older load's commit is gated by <c>_navVersion</c> and never overwrites this
    /// field, so stale data cannot leak. Task T5 will add <c>_selectionVersion</c> to also guard
    /// SelectDay against reading a half-updated cache during a LoadAsync swap.</summary>
    private ExpandedCalendarRange? _currentExpanded;

    /// <summary>Half-open <c>[Start, End)</c> bounds of <see cref="_currentExpanded"/>. Zero (default)
    /// when no expansion is cached, in which case <see cref="SelectDay"/> always takes the slow path.</summary>
    private DateOnly _expandedRangeStart;
    private DateOnly _expandedRangeEndExclusive;

    [ObservableProperty]
    private string _monthHeader = string.Empty;

    [ObservableProperty]
    private MonthView? _month;

    [ObservableProperty]
    private DateOnly? _selectedDate;

    [ObservableProperty]
    private WeekDay? _selectedDay;

    [ObservableProperty]
    private int _year;

    [ObservableProperty]
    private int _monthNumber;

    public MonthViewModel(
        StorageServices store,
        CalendarViewService viewService,
        SyncRunner sync,
        Action<DateOnly?> onAddNew,
        Action<Guid> onEdit)
    {
        _store = store;
        _viewService = viewService;
        _sync = sync;
        _onAddNew = onAddNew;
        _onEdit = onEdit;
        WeakReferenceMessenger.Default.Register(this);
    }

    /// <summary>Exposes the sync runner so the status bar can bind to <see cref="SyncRunner.Status"/>.</summary>
    public SyncRunner Sync => _sync;

    /// <summary>Mon..Sun headers, shared with the week view.</summary>
    public IReadOnlyList<string> WeekdayHeaders => CalendarViewService.Headers;

    public void Receive(CalendarColorsChangedMessage message)
    {
        Interlocked.Increment(ref _navVersion);
        _ = LoadAsync();
    }

    public void Dispose() => WeakReferenceMessenger.Default.Unregister<CalendarColorsChangedMessage>(this);

    public void NavigateTo(DateOnly anchor)
    {
        var v = Interlocked.Increment(ref _navVersion);
        var first = new DateOnly(anchor.Year, anchor.Month, 1);
        Year = first.Year;
        MonthNumber = first.Month;
        MonthHeader = $"{first.Year}年{first.Month}月";
        _ = LoadAsync(v);
    }

    /// <summary>Reload for the current month (refresh). Abandons if navigation raced ahead.</summary>
    public Task LoadAsync() => LoadAsync(Volatile.Read(ref _navVersion));

    public async Task LoadAsync(int v)
    {
        if (MonthNumber < 1)
        {
            return;
        }

        var tz = TimeZoneInfo.Local;
        // Capture nav targets before awaits so a concurrent NavigateTo cannot poison this load's build.
        var year = Year;
        var monthNumber = MonthNumber;

        // 月视图网格范围统一用 MonthGridRange.For（固定 6 行 42 天），与 CalendarViewService.BuildMonth 共用同一计算。
        var grid = MonthGridRange.For(year, monthNumber);
        var gridStart = grid.Start;
        var gridEndExclusive = grid.EndExclusive;

        var rangeStart = CalendarDateMath.LocalMidnightToUtc(gridStart, tz);
        var rangeEnd = CalendarDateMath.LocalMidnightToUtc(gridEndExclusive, tz);

        // T3: single query + single expansion for the whole month grid. The selected-day detail
        // below reuses the same ExpandedCalendarRange — no second GetInRangeAsync, no second expand.
        var allCals = await _store.Calendars.GetAllAsync();
        var calMap = allCals.ToDictionary(c => c.Id);
        var selectedIds = allCals.Where(c => c.IsSelected).Select(c => c.Id).ToArray();
        var stored = await _store.Events.GetInRangeAsync(selectedIds, rangeStart, rangeEnd);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var nowUtc = DateTimeOffset.UtcNow;
        var expanded = _viewService.ExpandRange(stored, calMap, gridStart, gridEndExclusive, tz, nowUtc);
        var built = _viewService.BuildMonthFromExpanded(expanded, year, monthNumber, today);
        // Only the latest navigation may commit Month; header was already set synchronously in NavigateTo.
        if (v != Volatile.Read(ref _navVersion))
            return;
        Month = built;

        // Cache the expansion so SelectDay can rebuild other days in this month without re-querying.
        _currentExpanded = expanded;
        _expandedRangeStart = gridStart;
        _expandedRangeEndExclusive = gridEndExclusive;

        // Refresh the selected day inline, gated by nav version so a rapid Prev/Next cannot
        // leave a stale bottom panel from an older month (the user-click SelectDay command
        // path stays unconditional). Fall back to today when SelectedDate is in another month.
        var sel = SelectedDate is { } d && d.Year == year && d.Month == monthNumber ? d : today;
        if (v != Volatile.Read(ref _navVersion))
            return;
        SelectedDate = sel;
        // When sel falls inside the month grid, reuse the single expansion for the bottom panel.
        // When sel falls OUTSIDE the grid (e.g. browsing a far-past month with no SelectedDate →
        // sel=today, which is outside the viewed month), set SelectedDay = null so MonthWindow.xaml's
        // `SelectedDay == {x:Null}` DataTrigger shows the null-state placeholder instead of an empty
        // day body (T3: this is the intended behaviour change from deleting the 2nd GetInRangeAsync —
        // bottom panel belongs to the viewed month, not silently to "today").
        if (sel >= gridStart && sel < gridEndExclusive)
        {
            SelectedDay = _viewService.BuildDayFromExpanded(expanded, sel, today, nowUtc);
        }
        else
        {
            SelectedDay = null;
        }
    }

    /// <summary>Selects a single day and builds its detail (all-day + timed) for the bottom panel.
    /// T3: when the day is covered by the cached <see cref="_currentExpanded"/> (the common case —
    /// user clicks a day in the currently viewed month), rebuild synchronously from the cache with
    /// no query and no expansion. Otherwise fall back to a 1-day query + BuildDay. Task T5
    /// will add <c>_selectionVersion</c> race protection; this T3 step is the minimal reuse change.</summary>
    [RelayCommand]
    private async Task SelectDay(DateOnly date)
    {
        SelectedDate = date;
        var today = DateOnly.FromDateTime(DateTime.Today);

        // Fast path: the cached expansion covers this day — reuse without re-querying.
        if (_currentExpanded is not null && date >= _expandedRangeStart && date < _expandedRangeEndExclusive)
        {
            SelectedDay = _viewService.BuildDayFromExpanded(_currentExpanded, date, today, DateTimeOffset.UtcNow);
            return;
        }

        // Slow path: day is outside the cached expansion (e.g. day in another month, or no cache yet).
        var tz = TimeZoneInfo.Local;
        var rangeStart = CalendarDateMath.LocalMidnightToUtc(date, tz);
        var rangeEnd = CalendarDateMath.LocalMidnightToUtc(date.AddDays(1), tz);
        var allCals = await _store.Calendars.GetAllAsync();
        var calMap = allCals.ToDictionary(c => c.Id);
        var selectedIds = allCals.Where(c => c.IsSelected).Select(c => c.Id).ToArray();
        var stored = await _store.Events.GetInRangeAsync(selectedIds, rangeStart, rangeEnd);
        SelectedDay = _viewService.BuildDay(stored, calMap, date, tz, today, DateTimeOffset.UtcNow);
    }

    [RelayCommand]
    private void AddNew(DateOnly? date) => _onAddNew(date ?? DateOnly.FromDateTime(DateTime.Today));

    [RelayCommand]
    private void Edit(Guid eventId) => _onEdit(eventId);

    [RelayCommand]
    private void PrevMonth() => NavigateTo(new DateOnly(Year, MonthNumber, 1).AddMonths(-1));

    [RelayCommand]
    private void NextMonth() => NavigateTo(new DateOnly(Year, MonthNumber, 1).AddMonths(1));

    [RelayCommand]
    private void Today() => NavigateTo(DateOnly.FromDateTime(DateTime.Today));

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await _sync.RunAsync();
        await LoadAsync();
    }
}
