using System.IO;
using System.Windows;
using CalDesk.App.Models;
using CalDesk.App.Services;
using CalDesk.App.ViewModels;
using CalDesk.App.Views;
using CalDesk.App.Windowing;
using CalDesk.CalDav;
using CalDesk.CalDav.Recurrence;
using CalDesk.Core.Services;
using CalDesk.Storage;
using H.NotifyIcon;

namespace CalDesk.App;

public partial class App : Application
{
    private StorageServices _store = null!;
    private SettingsService _settingsSvc = null!;
    private WindowsCredentialStore _creds = null!;
    private CalDavClientFactory _factory = null!;
    private CalendarViewService _viewSvc = null!;
    private EventService _events = null!;
    private SyncRunner _sync = null!;
    private ReminderService _reminders = null!;
    private WeekViewModel _vm = null!;
    private WeekWindow _week = null!;
    private MonthViewModel _monthVm = null!;
    private MonthWindow _month = null!;

    // Tray icon is a H.NotifyIcon.Wpf TaskbarIcon declared in App.xaml resources.
    // Accessed dynamically to avoid binding the build to its exact API surface.
    private object? _tray;

    private static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CalDesk");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            Directory.CreateDirectory(AppDataDir);

        var db = new CalDeskDatabase(Path.Combine(AppDataDir, "caldesk.db"));
        await db.InitializeAsync();
        _store = new StorageServices(db);

        _settingsSvc = new SettingsService();
        _settingsSvc.Load();

        _creds = new WindowsCredentialStore();
        _factory = new CalDavClientFactory(_creds, () => _settingsSvc.Current.AcceptInsecureTls);
        _viewSvc = new CalendarViewService(new IcalRecurrenceExpander());
        _events = new EventService(_store.Events, _store.SyncOperations);

        _sync = new SyncRunner(_store, _factory, _settingsSvc.Current);
        _sync.SyncCompleted += (_, _) => _week?.Dispatcher.InvokeAsync(() =>
        {
            _ = _vm.LoadAsync();
            if (_monthVm is not null)
            {
                _ = _monthVm.LoadAsync();
            }
        });
        _sync.Start();

        _reminders = new ReminderService(_store, new IcalRecurrenceExpander(), () => _settingsSvc.Current, OnReminder);

        _vm = new WeekViewModel(_store, _viewSvc, _events, _sync,
            onAddNew: date => _ = OpenQuickAddAsync(date, null),
            onEdit: id => _ = OpenQuickAddAsync(null, id));

        _week = new WeekWindow(_vm);
        ApplyWindowSettings(_week, _settingsSvc.Current);
        _week.Show();
        _week.ApplyGadgetStyles();
        ApplyWindowMode(_week, _settingsSvc.Current);

        _monthVm = new MonthViewModel(_store, _viewSvc, _sync,
            onAddNew: date => _ = OpenQuickAddAsync(date, null),
            onEdit: id => _ = OpenQuickAddAsync(null, id));
        _month = new MonthWindow(_monthVm);
        _month.BackRequested += BackToWeek;
        _week.MonthRequested += OpenMonth;

            _tray = FindResource("TrayIcon");
            if (_tray is TaskbarIcon tray)
            {
                // Icon lives only in Application.Resources, so it never gets a Loaded event —
                // force it into existence. Efficiency mode off so our timers aren't throttled.
                tray.ForceCreate(enablesEfficiencyMode: false);
            }

            _vm.NavigateTo(WeekViewModel.MondayOf(DateTime.Today));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "CalDesk 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private static void ApplyWindowSettings(WeekWindow w, AppSettings s)
    {
        w.IsPositionLocked = s.LockedPosition;
        w.Opacity = Math.Clamp(s.Opacity, 0.15, 1.0);
        if (!double.IsNaN(s.WindowLeft))
        {
            w.Left = s.WindowLeft;
        }

        if (!double.IsNaN(s.WindowTop))
        {
            w.Top = s.WindowTop;
        }
    }

    private static void ApplyWindowMode(Window w, AppSettings s)
    {
        switch (s.WindowMode)
        {
            case WindowMode.Topmost:
                w.SetTopmost(true);
                break;
            case WindowMode.Desktop:
                w.SendToDesktop();
                break;
            default:
                w.SetTopmost(false);
                break;
        }
    }

    private static void ApplyMonthWindowSettings(MonthWindow w, AppSettings s)
    {
        w.IsPositionLocked = s.LockedPosition;
        w.Opacity = Math.Clamp(s.Opacity, 0.15, 1.0);
        // The month window has no persisted position of its own; it opens near the week window.
    }

    private async Task OpenQuickAddAsync(DateOnly? date, Guid? editId)
    {
        var calendars = await _store.Calendars.GetAllAsync();
        var settings = _settingsSvc.Current;
        var defaultCal = calendars.FirstOrDefault(c => c.Id == settings.DefaultCalendarId) ?? calendars.FirstOrDefault();
        var vm = new QuickAddViewModel(_events, _settingsSvc, settings, _sync, calendars.ToList());

        if (editId is Guid id && await _store.Events.GetAsync(id) is { } existing)
        {
            vm.InitializeForEdit(existing);
        }
        else
        {
            vm.InitializeForCreate(date ?? DateOnly.FromDateTime(DateTime.Today), defaultCal);
        }

        var owner = _month is { IsVisible: true } ? _month : _week;
        var win = new QuickAddWindow { DataContext = vm, Owner = owner };
        vm.RequestClose += win.Close;
        win.ShowDialog();
        await _vm.LoadAsync();
        if (_monthVm is not null)
        {
            await _monthVm.LoadAsync();
        }
    }

    private void OnReminder(ReminderService.Notification n)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (_tray is TaskbarIcon tray)
                {
                    tray.ShowNotification(n.Title, n.Body);
                }
            }
            catch
            {
                // Notification API differs across versions; never crash.
            }
        });
    }

    private void OnShowWindow(object sender, RoutedEventArgs e) => ActivateWeek();
    private void OnShowMonth(object sender, RoutedEventArgs e) => OpenMonth();
    private void OnQuickAdd(object sender, RoutedEventArgs e) => _ = OpenQuickAddAsync(DateOnly.FromDateTime(DateTime.Today), null);
    private void OnSync(object sender, RoutedEventArgs e) => _ = _sync.RunAsync();
    private void OnSettings(object sender, RoutedEventArgs e) => OpenSettings();
    private void OnExit(object sender, RoutedEventArgs e) => Shutdown();

    private void ActivateWeek()
    {
        if (_week is null)
        {
            return;
        }

        if (!_week.IsVisible)
        {
            _week.Show();
        }

        _week.Activate();
    }

    private void OpenMonth()
    {
        if (_month is null)
        {
            return;
        }

        ApplyMonthWindowSettings(_month, _settingsSvc.Current);
        if (!_month.IsVisible)
        {
            _month.Show();
        }

        _month.ApplyGadgetStyles();
        ApplyWindowMode(_month, _settingsSvc.Current);
        _monthVm.NavigateTo(DateOnly.FromDateTime(DateTime.Today));
        _week.Hide();
    }

    private void BackToWeek()
    {
        _month?.Hide();
        if (_week is not null && !_week.IsVisible)
        {
            _week.Show();
        }

        _week?.Activate();
    }

    private void OpenSettings()
    {
        var vm = new SettingsViewModel(_store, _settingsSvc, _creds, _factory, _sync);
        var win = new SettingsWindow { DataContext = vm, Owner = _week };
        vm.Error += msg => MessageBox.Show(win, msg, "CalDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
        _ = vm.InitializeAsync();
        vm.RequestClose += win.Close;
        win.ShowDialog();

        var s = _settingsSvc.Current;
        ApplyWindowSettings(_week, s);
        ApplyWindowMode(_week, s);
        if (_month is not null)
        {
            ApplyMonthWindowSettings(_month, s);
            ApplyWindowMode(_month, s);
        }

        _sync.ReapplyInterval(s);
        _ = _vm.LoadAsync();
        if (_monthVm is not null)
        {
            _ = _monthVm.LoadAsync();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var s = _settingsSvc.Current;
        if (_week is { } w && !s.LockedPosition)
        {
            s.WindowLeft = w.Left;
            s.WindowTop = w.Top;
        }

        _settingsSvc.Save(s);
        _reminders?.Dispose();
        _sync?.Dispose();
        try
        {
            (_tray as TaskbarIcon)?.Dispose();
        }
        catch
        {
            // ignore
        }

        base.OnExit(e);
    }
}
