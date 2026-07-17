using CalDesk.App.Services;
using CalDesk.Core.Models;
using CalDesk.Core.Services;
using CalDesk.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalDesk.App.ViewModels;

public sealed partial class WeekViewModel : ObservableObject
{
    private readonly StorageServices _store;
    private readonly CalendarViewService _viewService;
    private readonly EventService _events;
    private readonly SyncRunner _sync;
    private readonly Action<DateOnly?> _onAddNew;
    private readonly Action<Guid> _onEdit;

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
    }

    /// <summary>Exposes the sync runner so the status bar can bind to its <see cref="SyncRunner.Status"/>.</summary>
    public SyncRunner Sync => _sync;

    public void NavigateTo(DateOnly monday)
    {
        WeekStart = monday;
        var end = monday.AddDays(6);
        WeekHeader = end.Month == monday.Month
            ? $"{monday.Month}月{monday.Day}日—{end.Day}日"
            : $"{monday.Month}月{monday.Day}日—{end.Month}月{end.Day}日";
        _ = LoadAsync();
    }

    public async Task LoadAsync()
    {
        var tz = TimeZoneInfo.Local;
        var rangeStart = LocalMidnightToUtc(WeekStart, tz);
        var rangeEnd = rangeStart.AddDays(7);

        var allCals = await _store.Calendars.GetAllAsync();
        var calMap = allCals.ToDictionary(c => c.Id);
        var selectedIds = allCals.Where(c => c.IsSelected).Select(c => c.Id).ToArray();
        var stored = await _store.Events.GetInRangeAsync(selectedIds, rangeStart, rangeEnd);

        var today = DateOnly.FromDateTime(DateTime.Today);
        Week = _viewService.BuildWeek(stored, calMap, WeekStart, tz, today, DateTimeOffset.UtcNow);
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

    private static DateTimeOffset LocalMidnightToUtc(DateOnly date, TimeZoneInfo tz)
    {
        var local = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, tz.GetUtcOffset(local)).UtcDateTime;
    }
}
