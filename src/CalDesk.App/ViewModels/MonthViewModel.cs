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

        var rangeStart = LocalMidnightToUtc(gridStart, tz);
        var rangeEnd = LocalMidnightToUtc(gridEndExclusive, tz);

        var allCals = await _store.Calendars.GetAllAsync();
        var calMap = allCals.ToDictionary(c => c.Id);
        var selectedIds = allCals.Where(c => c.IsSelected).Select(c => c.Id).ToArray();
        var stored = await _store.Events.GetInRangeAsync(selectedIds, rangeStart, rangeEnd);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var built = _viewService.BuildMonth(stored, calMap, year, monthNumber, tz, today, DateTimeOffset.UtcNow);
        // Only the latest navigation may commit Month; header was already set synchronously in NavigateTo.
        if (v != Volatile.Read(ref _navVersion))
            return;
        Month = built;
        // Refresh the selected day inline, gated by nav version so a rapid Prev/Next cannot
        // leave a stale bottom panel from an older month (the user-click SelectDay command
        // path stays unconditional). Fall back to today when SelectedDate is in another month.
        var sel = SelectedDate is { } d && d.Year == year && d.Month == monthNumber ? d : today;
        var selRangeStart = LocalMidnightToUtc(sel, tz);
        var selRangeEnd = LocalMidnightToUtc(sel.AddDays(1), tz);
        var selStored = await _store.Events.GetInRangeAsync(selectedIds, selRangeStart, selRangeEnd);
        if (v != Volatile.Read(ref _navVersion))
            return;
        SelectedDate = sel;
        SelectedDay = _viewService.BuildDay(selStored, calMap, sel, tz, today, DateTimeOffset.UtcNow);
    }

    /// <summary>Selects a single day and builds its detail (all-day + timed) for the bottom panel.
    /// Safe to call at any time — uses a 1-day range around <paramref name="date"/>.</summary>
    [RelayCommand]
    private async Task SelectDay(DateOnly date)
    {
        SelectedDate = date;
        var tz = TimeZoneInfo.Local;
        var rangeStart = LocalMidnightToUtc(date, tz);
        var rangeEnd = LocalMidnightToUtc(date.AddDays(1), tz);
        var allCals = await _store.Calendars.GetAllAsync();
        var calMap = allCals.ToDictionary(c => c.Id);
        var selectedIds = allCals.Where(c => c.IsSelected).Select(c => c.Id).ToArray();
        var stored = await _store.Events.GetInRangeAsync(selectedIds, rangeStart, rangeEnd);
        var today = DateOnly.FromDateTime(DateTime.Today);
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

    private static DateTimeOffset LocalMidnightToUtc(DateOnly date, TimeZoneInfo tz)
    {
        var local = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, tz.GetUtcOffset(local)).UtcDateTime;
    }
}
