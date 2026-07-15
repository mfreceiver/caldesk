using CalDesk.App.Services;
using CalDesk.Core.Models;
using CalDesk.Core.Services;
using CalDesk.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalDesk.App.ViewModels;

/// <summary>View model for the month-grid window. Mirrors <see cref="WeekViewModel"/> but builds
/// a <see cref="MonthView"/> via <see cref="CalendarViewService.BuildMonth"/>.</summary>
public sealed partial class MonthViewModel : ObservableObject
{
    private readonly StorageServices _store;
    private readonly CalendarViewService _viewService;
    private readonly SyncRunner _sync;
    private readonly Action<DateOnly?> _onAddNew;
    private readonly Action<Guid> _onEdit;

    [ObservableProperty]
    private string _monthHeader = string.Empty;

    [ObservableProperty]
    private MonthView? _month;

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
    }

    /// <summary>Exposes the sync runner so the status bar can bind to <see cref="SyncRunner.Status"/>.</summary>
    public SyncRunner Sync => _sync;

    /// <summary>Mon..Sun headers, shared with the week view.</summary>
    public IReadOnlyList<string> WeekdayHeaders => CalendarViewService.Headers;

    public void NavigateTo(DateOnly anchor)
    {
        var first = new DateOnly(anchor.Year, anchor.Month, 1);
        Year = first.Year;
        MonthNumber = first.Month;
        MonthHeader = $"{first.Year}年{first.Month}月";
        _ = LoadAsync();
    }

    public async Task LoadAsync()
    {
        if (MonthNumber < 1)
        {
            return;
        }

        var tz = TimeZoneInfo.Local;
        var first = new DateOnly(Year, MonthNumber, 1);
        var last = new DateOnly(Year, MonthNumber, DateTime.DaysInMonth(Year, MonthNumber));

        // Fetch across the whole aligned grid so spillover days and recurring series are covered.
        var gridStart = first.AddDays(-DaysSinceMonday(first));
        var gridEndExclusive = last.AddDays(-DaysSinceMonday(last)).AddDays(7);

        var rangeStart = LocalMidnightToUtc(gridStart, tz);
        var rangeEnd = LocalMidnightToUtc(gridEndExclusive, tz);

        var allCals = await _store.Calendars.GetAllAsync();
        var calMap = allCals.ToDictionary(c => c.Id);
        var selectedIds = allCals.Where(c => c.IsSelected).Select(c => c.Id).ToArray();
        var stored = await _store.Events.GetInRangeAsync(selectedIds, rangeStart, rangeEnd);

        var today = DateOnly.FromDateTime(DateTime.Today);
        Month = _viewService.BuildMonth(stored, calMap, Year, MonthNumber, tz, today, DateTimeOffset.UtcNow);
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

    private static int DaysSinceMonday(DateOnly d) => ((int)d.DayOfWeek + 6) % 7;

    private static DateTimeOffset LocalMidnightToUtc(DateOnly date, TimeZoneInfo tz)
    {
        var local = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, tz.GetUtcOffset(local)).UtcDateTime;
    }
}
