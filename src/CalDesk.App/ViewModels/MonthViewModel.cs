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
    /// field, so stale data cannot leak. Additionally, <see cref="LoadAsync"/>'s commit block bumps
    /// <c>_selectionVersion</c> so any in-flight <see cref="SelectDay"/> that captured the PREVIOUS
    /// cache fails its version check and returns without committing a stale day body.</summary>
    private ExpandedCalendarRange? _currentExpanded;

    /// <summary>Half-open <c>[Start, End)</c> bounds of <see cref="_currentExpanded"/>. Zero (default)
    /// when no expansion is cached, in which case <see cref="SelectDay"/> always takes the slow path.</summary>
    private DateOnly _expandedRangeStart;
    private DateOnly _expandedRangeEndExclusive;

    /// <summary>
    /// Bumped on every <see cref="SelectDay"/> invocation so an in-flight SelectDay from an older
    /// click cannot overwrite <see cref="SelectedDay"/> after a newer day was clicked. Mirrors the
    /// <c>_navVersion</c> pattern used by <see cref="LoadAsync"/> (T5 race protection).
    /// </summary>
    private int _selectionVersion;

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

        // 月视图网格范围统一用 MonthGridRange.For（自动选择 5 或 6 行），与 CalendarViewService.BuildMonth 共用同一计算。
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
        // T7: two-phase display-path load — lightweight columns (no icalendar_data) + on-demand
        // recurring backfill, centralised in EventRepositoryDisplayExtensions.GetInRangeForDisplayAsync.
        var stored = await _store.Events.GetInRangeForDisplayAsync(selectedIds, rangeStart, rangeEnd);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var nowUtc = DateTimeOffset.UtcNow;
        // Capture SelectedDate on the UI thread so the background compute below can't race with a
        // concurrent SelectDay/LoadAsync modifying it.
        var selectedDate = SelectedDate;

        // T4: move the compute-heavy ExpandRange + BuildMonthFromExpanded + SelectedDay build off the
        // UI thread into a single Task.Run. CalendarViewService.Build* is pure data (POCOs, no WPF
        // objects, no shared mutable state — T4-C4) so this is safe. Returns the built MonthView,
        // the selected day's WeekDay (or null when sel is outside the month grid — T3 fix #1), the
        // expanded range (for the cache fields below), and the resolved `sel` (for SelectedDate commit).
        var (built, selDay, expanded, sel) = await Task.Run(() =>
        {
            var expanded = _viewService.ExpandRange(stored, calMap, gridStart, gridEndExclusive, tz, nowUtc);
            var month = _viewService.BuildMonthFromExpanded(expanded, year, monthNumber, today);
            var sel = selectedDate is { } d && d.Year == year && d.Month == monthNumber ? d : today;
            // When sel falls inside the month grid, reuse the single expansion for the bottom panel.
            // When sel falls OUTSIDE the grid (e.g. browsing a far-past month with no SelectedDate →
            // sel=today, which is outside the viewed month), return null so MonthWindow.xaml's
            // `SelectedDay == {x:Null}` DataTrigger shows the null-state placeholder (T3 fix #1).
            WeekDay? selDay = sel >= gridStart && sel < gridEndExclusive
                ? _viewService.BuildDayFromExpanded(expanded, sel, today, nowUtc)
                : null;
            return (month, selDay, expanded, sel);
        });
        // Only the latest navigation may commit; header was already set synchronously in NavigateTo.
        if (v != Volatile.Read(ref _navVersion))
            return;

        // Bump _selectionVersion to invalidate any in-flight SelectDay continuations (T5 fix):
        // a SelectDay that captured the PREVIOUS month's _currentExpanded cache would otherwise
        // commit a stale day body on top of the new month we are about to publish here. After this
        // bump, those continuations fail their `ver != Volatile.Read(ref _selectionVersion)` check
        // and return without committing.
        Interlocked.Increment(ref _selectionVersion);

        // UI-thread commit block: assign observable properties + cache the expansion for SelectDay.
        Month = built;
        _currentExpanded = expanded;
        _expandedRangeStart = gridStart;
        _expandedRangeEndExclusive = gridEndExclusive;
        SelectedDate = sel;
        SelectedDay = selDay;
    }

    /// <summary>Selects a single day and builds its detail (all-day + timed) for the bottom panel.
    /// T3: when the day is covered by the cached <see cref="_currentExpanded"/> (the common case —
    /// user clicks a day in the currently viewed month), rebuild from the cache with no query and no
    /// expansion (T5-C2). Otherwise fall back to a 1-day query + BuildDay. T5: every commit is gated
    /// by <c>_selectionVersion</c> so rapid day-clicks only commit the latest (T5-C1).</summary>
    [RelayCommand]
    private async Task SelectDay(DateOnly date)
    {
        var ver = Interlocked.Increment(ref _selectionVersion);
        SelectedDate = date;

        // Snapshot the cache fields on the UI thread (avoids reading a half-updated cache). This
        // snapshot alone is not sufficient race protection — LoadAsync's commit block additionally
        // bumps _selectionVersion so any in-flight SelectDay whose snapshot came from the previous
        // month fails the post-await version check and returns without committing.
        var cache = _currentExpanded;
        var cacheStart = _expandedRangeStart;
        var cacheEnd = _expandedRangeEndExclusive;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var nowUtc = DateTimeOffset.UtcNow;

        // Fast path: cached expansion covers this day — pure compute on background, no query.
        if (cache is not null && date >= cacheStart && date < cacheEnd)
        {
            var day = await Task.Run(() =>
                _viewService.BuildDayFromExpanded(cache, date, today, nowUtc));
            if (ver != Volatile.Read(ref _selectionVersion))
                return;
            SelectedDay = day;
            return;
        }

        // Slow path: day is outside the cached expansion (e.g. day in another month, or no cache yet).
        var tz = TimeZoneInfo.Local;
        var rangeStart = CalendarDateMath.LocalMidnightToUtc(date, tz);
        var rangeEnd = CalendarDateMath.LocalMidnightToUtc(date.AddDays(1), tz);
        var allCals = await _store.Calendars.GetAllAsync();
        var calMap = allCals.ToDictionary(c => c.Id);
        var selectedIds = allCals.Where(c => c.IsSelected).Select(c => c.Id).ToArray();
        // T7: two-phase display-path load (slow path typically fetches 0-1 recurring events; same
        // helper as LoadAsync for consistency).
        var stored = await _store.Events.GetInRangeForDisplayAsync(selectedIds, rangeStart, rangeEnd);
        var built = await Task.Run(() => _viewService.BuildDay(stored, calMap, date, tz, today, nowUtc));
        if (ver != Volatile.Read(ref _selectionVersion))
            return;
        SelectedDay = built;
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
