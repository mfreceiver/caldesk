using CalDesk.App.Models;
using CalDesk.App.Services;
using CalDesk.Core;
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

    /// <summary>An existing rule that none of the creatable presets can represent
    /// (e.g. INTERVAL&gt;1, COUNT/UNTIL, YEARLY, BYMONTHDAY). Editing keeps the stored rule
    /// untouched instead of silently destroying it (CQ-001). Never offered when creating —
    /// a new event has no rule to preserve.</summary>
    Custom,
}

public sealed partial class QuickAddViewModel : ObservableObject
{
    private readonly EventService _events;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly SyncRunner _sync;
    private CalendarEvent? _editing;

    /// <summary>Once the user edits end date/time, stop auto-deriving end from start.</summary>
    private bool _endUserEdited;

    /// <summary>Suppress treating programmatic end updates as user edits.</summary>
    private bool _suppressEndUserTracking;

    /// <summary>True while InitializeFor* assigns fields (skip auto-end / normalize noise).</summary>
    private bool _isInitializing;

    public event Action? RequestClose;

    public List<CalendarInfo> Calendars { get; }

    private static readonly RecurrencePick[] CreateRepeatOptions =
        { RecurrencePick.None, RecurrencePick.Daily, RecurrencePick.Weekly, RecurrencePick.Monthly };

    // Full set for editing: additionally offers Custom ("keep the existing rule as-is").
    private static readonly RecurrencePick[] EditRepeatOptions = Enum.GetValues<RecurrencePick>();

    /// <summary>Repeat choices for the combo box. Editing includes
    /// <see cref="RecurrencePick.Custom"/> for rules no preset can represent; creating must
    /// not offer it (there is no existing rule to keep).</summary>
    public RecurrencePick[] RepeatOptions => IsEditing ? EditRepeatOptions : CreateRepeatOptions;

    [ObservableProperty] private string _title = string.Empty;

    [ObservableProperty] private DateTime _startDate = DateTime.Today;
    [ObservableProperty] private string _startTime = "09:00";
    [ObservableProperty] private DateTime _endDate = DateTime.Today;
    [ObservableProperty] private string _endTime = "10:00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTime))]
    private bool _isAllDay;

    public bool HasTime => !IsAllDay;

    [ObservableProperty] private string _location = string.Empty;
    [ObservableProperty] private int _reminderMinutes;
    [ObservableProperty] private RecurrencePick _repeat = RecurrencePick.None;
    [ObservableProperty] private CalendarInfo? _selectedCalendar;
    [ObservableProperty] private bool _hasAdvanced;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepeatOptions))]
    private bool _isEditing;

    /// <summary>User-visible validation message; null when ok.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => !string.IsNullOrEmpty(Error);

    [ObservableProperty] private bool _calendarInvalid;
    [ObservableProperty] private bool _startTimeInvalid;
    [ObservableProperty] private bool _endTimeInvalid;
    [ObservableProperty] private bool _rangeInvalid;

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
        _endUserEdited = false;
        _isInitializing = true;
        ClearValidation();

        try
        {
            StartDate = dateHint.ToDateTime(TimeOnly.MinValue);
            var now = DateTime.Now;
            var hour = now.Minute >= 30 ? now.Hour + 1 : now.Hour;
            var minute = now.Minute >= 30 ? 0 : 30;
            if (hour >= 24)
            {
                hour = 23;
                minute = 0;
            }

            StartTime = $"{hour:D2}:{minute:D2}";
            ReminderMinutes = _settings.DefaultReminderMinutes;
            SelectedCalendar = defaultCalendar ?? Calendars.FirstOrDefault();
            IsAllDay = false;
            Location = string.Empty;
            Title = string.Empty;
            Repeat = RecurrencePick.None;
            HasAdvanced = false;
        }
        finally
        {
            _isInitializing = false;
        }

        ApplyDefaultEndFromStart();
    }

    public void InitializeForEdit(CalendarEvent evt)
    {
        _editing = evt;
        IsEditing = true;
        _endUserEdited = true; // never clobber an existing event's end via start auto-inference
        _isInitializing = true;
        ClearValidation();

        try
        {
            var tz = TimeZoneInfo.Local;
            var startLocal = TimeZoneInfo.ConvertTime(evt.StartUtc, tz);
            var endLocal = TimeZoneInfo.ConvertTime(evt.EndUtc, tz);
            Title = evt.Summary;
            IsAllDay = evt.IsAllDay;

            StartDate = startLocal.Date;
            StartTime = startLocal.ToString("HH:mm");

            if (evt.IsAllDay)
            {
                // All-day DTEND is exclusive midnight → inclusive EndDate is the previous local day.
                var inclusiveEnd = endLocal.Date.AddDays(-1);
                EndDate = inclusiveEnd < StartDate ? StartDate : inclusiveEnd;
                EndTime = "00:00";
            }
            else
            {
                EndDate = endLocal.Date;
                EndTime = endLocal.ToString("HH:mm");
            }

            Location = evt.Location ?? string.Empty;
            ReminderMinutes = ReminderService.ParseTriggerMinutes(evt) ?? _settings.DefaultReminderMinutes;
            var rule = evt.RecurrenceRule;
            // Exact preset matches only; any other non-blank rule is one this dialog cannot
            // represent (INTERVAL=2, COUNT/UNTIL, YEARLY, BYMONTHDAY, …). Show it as Custom
            // and keep the stored rule verbatim on save — rewriting it from our subset would
            // silently destroy the parts we cannot model (CQ-001).
            Repeat = rule == "FREQ=DAILY;INTERVAL=1" ? RecurrencePick.Daily
                   : rule == "FREQ=WEEKLY;INTERVAL=1;BYDAY=" + DayOfWeekCode(startLocal.DayOfWeek) ? RecurrencePick.Weekly
                   : rule == "FREQ=MONTHLY;INTERVAL=1" ? RecurrencePick.Monthly
                   : string.IsNullOrWhiteSpace(rule) ? RecurrencePick.None
                   : RecurrencePick.Custom;
            SelectedCalendar = Calendars.FirstOrDefault(c => c.Id == evt.CalendarId);
            // Expand advanced when editing a recurring event so repeat is visible.
            HasAdvanced = Repeat != RecurrencePick.None;
        }
        finally
        {
            _isInitializing = false;
        }
    }

    partial void OnStartDateChanged(DateTime value)
    {
        if (_isInitializing)
        {
            return;
        }

        ClearValidation();
        if (!_endUserEdited)
        {
            ApplyDefaultEndFromStart();
        }
    }

    partial void OnStartTimeChanged(string value)
    {
        if (_isInitializing)
        {
            return;
        }

        ClearValidation();
        // Do not normalize here: bindings use PropertyChanged so mid-keystroke rewrite would steal input.
        if (!_endUserEdited)
        {
            ApplyDefaultEndFromStart();
        }
    }

    partial void OnEndDateChanged(DateTime value)
    {
        if (_isInitializing || _suppressEndUserTracking)
        {
            return;
        }

        ClearValidation();
        _endUserEdited = true;
    }

    partial void OnEndTimeChanged(string value)
    {
        if (_isInitializing)
        {
            return;
        }

        ClearValidation();
        if (!_suppressEndUserTracking)
        {
            _endUserEdited = true;
        }
    }

    partial void OnIsAllDayChanged(bool value)
    {
        if (_isInitializing)
        {
            return;
        }

        ClearValidation();
        if (value)
        {
            // Spec: toggling all-day on defaults to a single inclusive day.
            _suppressEndUserTracking = true;
            EndDate = StartDate.Date;
            _suppressEndUserTracking = false;
            // Not a user "end edit" for timed auto-follow after they turn all-day off again.
            _endUserEdited = false;
        }
    }

    partial void OnSelectedCalendarChanged(CalendarInfo? value)
    {
        if (_isInitializing)
        {
            return;
        }

        ClearValidation();
    }

    private void ApplyDefaultEndFromStart()
    {
        if (!TimeText.TryParse(StartTime, out var startTod))
        {
            return;
        }

        var duration = Math.Max(15, _settings.DefaultDurationMinutes);
        var startLocal = StartDate.Date.Add(startTod.ToTimeSpan());
        var endLocal = startLocal.AddMinutes(duration);

        _suppressEndUserTracking = true;
        EndDate = endLocal.Date;
        EndTime = TimeText.Format(TimeOnly.FromDateTime(endLocal));
        _suppressEndUserTracking = false;
    }

    [RelayCommand]
    private void More() => HasAdvanced = !HasAdvanced;

    [RelayCommand]
    private async Task SaveAsync()
    {
        ClearValidation();

        if (SelectedCalendar is null)
        {
            SetValidation(
                "请选择日历",
                calendarInvalid: true);
            return;
        }

        var tz = TimeZoneInfo.Local;
        var startDateOnly = DateOnly.FromDateTime(StartDate);
        var endDateOnly = DateOnly.FromDateTime(EndDate);

        // Normalize display when times parse (before range rules that may adjust EndDate).
        if (!IsAllDay)
        {
            if (TimeText.TryParse(StartTime, out var sNorm))
            {
                StartTime = TimeText.Format(sNorm);
            }

            if (TimeText.TryParse(EndTime, out var eNorm))
            {
                _suppressEndUserTracking = true;
                EndTime = TimeText.Format(eNorm);
                _suppressEndUserTracking = false;
            }
        }

        var range = EventTimeRange.TryCompute(
            IsAllDay,
            startDateOnly,
            endDateOnly,
            IsAllDay ? null : StartTime,
            IsAllDay ? null : EndTime,
            tz);

        if (range.Status != EventTimeRangeStatus.Ok)
        {
            SetValidation(
                range.Message ?? "时间范围无效",
                startTimeInvalid: range.StartTimeInvalid,
                endTimeInvalid: range.EndTimeInvalid,
                rangeInvalid: range.RangeInvalid || range.Status == EventTimeRangeStatus.RangeInvalid);
            return;
        }

        // Overnight same-day push: surface the EndDate that was actually saved.
        if (range.AdjustedEndDate is { } adjustedEnd)
        {
            _suppressEndUserTracking = true;
            EndDate = adjustedEnd.ToDateTime(TimeOnly.MinValue);
            _suppressEndUserTracking = false;
        }

        var startUtc = range.StartUtc;
        var endUtc = range.EndUtc;

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
            RecurrencePick.Weekly => $"FREQ=WEEKLY;INTERVAL=1;BYDAY={DayOfWeekCode(startDateOnly.DayOfWeek)}",
            RecurrencePick.Monthly => "FREQ=MONTHLY;INTERVAL=1",
            RecurrencePick.Custom => evt.RecurrenceRule, // keep the server rule untouched (CQ-001)
            _ => null, // None: the user explicitly chose "does not repeat"
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

    private void ClearValidation()
    {
        Error = null;
        CalendarInvalid = false;
        StartTimeInvalid = false;
        EndTimeInvalid = false;
        RangeInvalid = false;
    }

    private void SetValidation(
        string message,
        bool calendarInvalid = false,
        bool startTimeInvalid = false,
        bool endTimeInvalid = false,
        bool rangeInvalid = false)
    {
        Error = message;
        CalendarInvalid = calendarInvalid;
        StartTimeInvalid = startTimeInvalid;
        EndTimeInvalid = endTimeInvalid;
        RangeInvalid = rangeInvalid;
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
}
