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

public sealed partial class WeekViewModel : ObservableObject, IRecipient<CalendarColorsChangedMessage>, IDisposable
{
    private readonly StorageServices _store;
    private readonly CalendarViewService _viewService;
    private readonly EventService _events;
    private readonly SyncRunner _sync;
    private readonly Action<DateOnly?> _onAddNew;
    private readonly Action<Guid> _onEdit;

    /// <summary>
    /// Bumped on each <see cref="NavigateTo"/> so an in-flight <see cref="LoadAsync"/> from an older
    /// navigation cannot overwrite <see cref="Week"/> after a newer week was selected.
    /// </summary>
    private int _navVersion;

    [ObservableProperty]
    private string _weekHeader = string.Empty;

    [ObservableProperty]
    private WeekView? _week;

    [ObservableProperty]
    private DateOnly _weekStart;

    public WeekViewModel(
        StorageServices store,
        CalendarViewService viewService,
        EventService events,
        SyncRunner sync,
        Action<DateOnly?> onAddNew,
        Action<Guid> onEdit)
    {
        _store = store;
        _viewService = viewService;
        _events = events;
        _sync = sync;
        _onAddNew = onAddNew;
        _onEdit = onEdit;
        WeakReferenceMessenger.Default.Register(this);
    }

    /// <summary>Exposes the sync runner so the status bar can bind to its <see cref="SyncRunner.Status"/>.</summary>
    public SyncRunner Sync => _sync;

    public void NavigateTo(DateOnly monday)
    {
        var v = Interlocked.Increment(ref _navVersion);
        WeekStart = monday;
        WeekHeader = WeekOfMonthCalculator.Header(monday);
        _ = LoadAsync(v);
    }

    /// <summary>Reload for the current week (refresh/delete). Abandons if navigation raced ahead.</summary>
    public Task LoadAsync() => LoadAsync(Volatile.Read(ref _navVersion));

    public async Task LoadAsync(int v)
    {
        var tz = TimeZoneInfo.Local;
        // Capture WeekStart at the entry (before any await) and reuse this snapshot for both the
        // storage-range bounds AND the BuildWeek call below. If a newer NavigateTo swaps WeekStart
        // during an await, this load's nav-check fails and aborts without committing — but in the
        // meantime BuildWeek must not mix "new WeekStart" with "stored events fetched for old
        // WeekStart" (which the previous late-capture form risked).
        var weekStart = WeekStart;
        // Local-midnight bounds (not rangeStart.AddDays(7)) so DST weeks are 167/169h, matching BuildWeek.
        var (rangeStart, rangeEnd) = LocalDateTimeRange.WeekStorageRange(weekStart, tz);

        var allCals = await _store.Calendars.GetAllAsync();
        var calMap = allCals.ToDictionary(c => c.Id);
        var selectedIds = allCals.Where(c => c.IsSelected).Select(c => c.Id).ToArray();
        var stored = await _store.Events.GetInRangeAsync(selectedIds, rangeStart, rangeEnd);

        // T4: capture all BuildWeek inputs on the UI thread, then run the pure compute off-thread.
        // BuildWeek is pure data (POCO WeekView, no WPF objects, no shared mutable state — T4-C4).
        var today = DateOnly.FromDateTime(DateTime.Today);
        var nowUtc = DateTimeOffset.UtcNow;
        var built = await Task.Run(() => _viewService.BuildWeek(stored, calMap, weekStart, tz, today, nowUtc));
        // Only the latest navigation may commit Week; header was already set synchronously in NavigateTo.
        if (v != Volatile.Read(ref _navVersion))
            return;
        Week = built;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await _sync.RunAsync();
        await LoadAsync();
    }

    [RelayCommand]
    private void AddNew(DateOnly? date) => _onAddNew(date ?? DateOnly.FromDateTime(DateTime.Today));

    [RelayCommand]
    private void Edit(Guid eventId) => _onEdit(eventId);

    [RelayCommand]
    private void PrevWeek() => NavigateTo(WeekStart.AddDays(-7));

    [RelayCommand]
    private void NextWeek() => NavigateTo(WeekStart.AddDays(7));

    [RelayCommand]
    private void Today() => NavigateTo(MondayOf(DateTime.Today));

    [RelayCommand]
    private async Task DeleteAsync(Guid eventId)
    {
        await _events.DeleteAsync(eventId);
        // Status must reflect outbox immediately even if a sync is already running (RunAsync no-ops).
        await _sync.RefreshPendingStatusAsync();
        _ = _sync.RunAsync();
        await LoadAsync();
    }

    public static DateOnly MondayOf(DateTime date)
    {
        var d = DateOnly.FromDateTime(date);
        var diff = (7 + (d.DayOfWeek - DayOfWeek.Monday)) % 7;
        return d.AddDays(-diff);
    }

    /// <summary>R12：日历颜色/选择本地变更后刷新色点（无服务器同步）。
    /// 递增 nav 版本以弃用任何在飞的 LoadAsync，再起一次新加载。</summary>
    public void Receive(CalendarColorsChangedMessage message)
    {
        Interlocked.Increment(ref _navVersion);
        _ = LoadAsync();
    }

    public void Dispose() => WeakReferenceMessenger.Default.Unregister<CalendarColorsChangedMessage>(this);
}
