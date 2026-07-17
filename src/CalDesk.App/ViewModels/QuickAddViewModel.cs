using CalDesk.App.Models;
using CalDesk.App.Services;
using CalDesk.Core.Models;
using CalDesk.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalDesk.App.ViewModels;

public enum RecurrencePick
{
    None,
    Daily,
    Weekly,
    Monthly,
}

public sealed partial class QuickAddViewModel : ObservableObject
{
    private readonly EventService _events;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly SyncRunner _sync;
    private CalendarEvent? _editing;

    public event Action? RequestClose;

    public List<CalendarInfo> Calendars { get; }
    public RecurrencePick[] RepeatOptions { get; } = Enum.GetValues<RecurrencePick>();

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private DateTime _date = DateTime.Today;
    [ObservableProperty] private string _startText = "09:00";
    [ObservableProperty] private string _endText = "10:00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTime))]
    private bool _isAllDay;

    public bool HasTime => !IsAllDay;

    [ObservableProperty] private string _location = string.Empty;
    [ObservableProperty] private int _reminderMinutes;
    [ObservableProperty] private RecurrencePick _repeat = RecurrencePick.None;
    [ObservableProperty] private CalendarInfo? _selectedCalendar;
    [ObservableProperty] private bool _hasAdvanced;

    [ObservableProperty] private bool _isEditing;

    public QuickAddViewModel(EventService events, SettingsService settingsService, AppSettings settings, SyncRunner sync, List<CalendarInfo> calendars)
    {
        _events = events;
        _settingsService = settingsService;
        _settings = settings;
        _sync = sync;
        Calendars = calendars;
    }

    public void InitializeForCreate(DateOnly dateHint, CalendarInfo? defaultCalendar)
    {
        _editing = null;
        IsEditing = false;
        Date = dateHint.ToDateTime(TimeOnly.MinValue);
        var now = DateTime.Now;
        var hour = now.Minute >= 30 ? now.Hour + 1 : now.Hour;
        var minute = now.Minute >= 30 ? 0 : 30;
        if (hour >= 24)
        {
            hour = 23;
            minute = 0;
        }

        StartText = $"{hour:D2}:{minute:D2}";
        var endMin = minute + Math.Max(15, _settings.DefaultDurationMinutes);
        EndText = $"{(hour + endMin / 60) % 24:D2}:{endMin % 60:D2}";
        ReminderMinutes = _settings.DefaultReminderMinutes;
        SelectedCalendar = defaultCalendar ?? Calendars.FirstOrDefault();
    }

    public void InitializeForEdit(CalendarEvent evt)
    {
        _editing = evt;
        IsEditing = true;
        var tz = TimeZoneInfo.Local;
        var startLocal = TimeZoneInfo.ConvertTime(evt.StartUtc, tz);
        var endLocal = TimeZoneInfo.ConvertTime(evt.EndUtc, tz);
        Title = evt.Summary;
        Date = startLocal.Date;
        IsAllDay = evt.IsAllDay;
        StartText = startLocal.ToString("HH:mm");
        EndText = endLocal.ToString("HH:mm");
        Location = evt.Location ?? string.Empty;
        ReminderMinutes = ReminderService.ParseTriggerMinutes(evt) ?? _settings.DefaultReminderMinutes;
        var rule = evt.RecurrenceRule;
        Repeat = rule == "FREQ=DAILY;INTERVAL=1" ? RecurrencePick.Daily
               : rule == "FREQ=WEEKLY;INTERVAL=1;BYDAY=" + DayOfWeekCode(startLocal.DayOfWeek) ? RecurrencePick.Weekly
               : rule == "FREQ=MONTHLY;INTERVAL=1" ? RecurrencePick.Monthly
               : RecurrencePick.None;
        SelectedCalendar = Calendars.FirstOrDefault(c => c.Id == evt.CalendarId);
    }

    [RelayCommand]
    private void More() => HasAdvanced = !HasAdvanced;

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (SelectedCalendar is null)
        {
            return;
        }

        var tz = TimeZoneInfo.Local;
        var dateOnly = DateOnly.FromDateTime(Date);
        DateTimeOffset startUtc, endUtc;

        if (IsAllDay)
        {
            startUtc = LocalMidnightToUtc(dateOnly, tz);
            endUtc = startUtc.AddDays(1);
        }
        else
        {
            if (!TimeOnly.TryParse(StartText, out var s) || !TimeOnly.TryParse(EndText, out var e))
            {
                return;
            }

            var sLocal = dateOnly.ToDateTime(s, DateTimeKind.Unspecified);
            // End clock at/before start → overnight event (ends the following day).
            var endDateOnly = e <= s ? dateOnly.AddDays(1) : dateOnly;
            var eLocal = endDateOnly.ToDateTime(e, DateTimeKind.Unspecified);
            startUtc = new DateTimeOffset(sLocal, tz.GetUtcOffset(sLocal)).UtcDateTime;
            endUtc = new DateTimeOffset(eLocal, tz.GetUtcOffset(eLocal)).UtcDateTime;
        }

        var evt = _editing ?? new CalendarEvent();
        evt.CalendarId = SelectedCalendar.Id;
        evt.Summary = string.IsNullOrWhiteSpace(Title) ? "(无标题)" : Title;
        evt.Location = string.IsNullOrWhiteSpace(Location) ? null : Location;
        evt.IsAllDay = IsAllDay;
        evt.StartUtc = startUtc;
        evt.EndUtc = endUtc;
        evt.TimeZoneId = IsAllDay ? null : tz.Id;
        evt.IsFloating = false;
        evt.RecurrenceRule = Repeat switch
        {
            RecurrencePick.Daily => "FREQ=DAILY;INTERVAL=1",
            RecurrencePick.Weekly => $"FREQ=WEEKLY;INTERVAL=1;BYDAY={DayOfWeekCode(dateOnly.DayOfWeek)}",
            RecurrencePick.Monthly => "FREQ=MONTHLY;INTERVAL=1",
            _ => null,
        };

        if (_editing is null)
        {
            await _events.CreateAsync(evt);
        }
        else
        {
            await _events.UpdateAsync(evt);
        }

        if (ReminderMinutes != _settings.DefaultReminderMinutes)
        {
            _settings.DefaultReminderMinutes = ReminderMinutes;
            _settingsService.Save(_settings);
        }

        // Status must reflect outbox immediately even if a sync is already running (RunAsync no-ops).
        await _sync.RefreshPendingStatusAsync();
        _ = _sync.RunAsync();
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke();

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (_editing is not null)
        {
            await _events.DeleteAsync(_editing.Id);
            await _sync.RefreshPendingStatusAsync();
            _ = _sync.RunAsync();
            RequestClose?.Invoke();
        }
    }

    private static string DayOfWeekCode(DayOfWeek d) => d switch
    {
        DayOfWeek.Sunday => "SU",
        DayOfWeek.Monday => "MO",
        DayOfWeek.Tuesday => "TU",
        DayOfWeek.Wednesday => "WE",
        DayOfWeek.Thursday => "TH",
        DayOfWeek.Friday => "FR",
        DayOfWeek.Saturday => "SA",
        _ => "MO",
    };

    private static DateTimeOffset LocalMidnightToUtc(DateOnly date, TimeZoneInfo tz)
    {
        var local = date.ToDateTime(new TimeOnly(0, 0), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, tz.GetUtcOffset(local)).UtcDateTime;
    }
}
