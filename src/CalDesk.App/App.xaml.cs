using System.IO;
using System.Windows;
using CalDesk.App.Diagnostics;
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
    private SystemThemeWatcher? _themeWatcher;

    // Tray icon is a H.NotifyIcon.Wpf TaskbarIcon declared in App.xaml resources.
    // Accessed dynamically to avoid binding the build to its exact API surface.
    private object? _tray;

    /// <summary>True while settings init + modal dialog is in progress; blocks re-entry
    /// (OpenSettings is async void and can otherwise stack multiple dialogs).</summary>
    private bool _settingsOpen;

    private static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CalDesk");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var logPath = StartupLog.Init();
        RegisterGlobalHandlers();
        StartupLog.Log($"OnStartup: begin  log={logPath}");

        try
        {
            Directory.CreateDirectory(AppDataDir);
            StartupLog.Log("OnStartup: AppDataDir ready");

        StartupLog.Log("OnStartup: constructing CalDeskDatabase");
        var db = new CalDeskDatabase(Path.Combine(AppDataDir, "caldesk.db"));
        StartupLog.Log("OnStartup: db.InitializeAsync (await)");
        await db.InitializeAsync();
        StartupLog.Log("OnStartup: DB initialized");
        _store = new StorageServices(db);

        _settingsSvc = new SettingsService();
        _settingsSvc.Load();
        StartupLog.Log($"OnStartup: settings loaded  mode={_settingsSvc.Current.WindowMode}  acceptInsecureTls={_settingsSvc.Current.AcceptInsecureTls}  acctCfgStartWithWindows={_settingsSvc.Current.StartWithWindows}");
        ApplyFontSize(_settingsSvc.Current.FontSize);
        StartupLog.Log($"OnStartup: font size applied  fontSize={_settingsSvc.Current.FontSize}");

        // §3.6: apply persisted theme + wire OS theme watcher (R5 weak-ref + SessionSwitch + Dispatcher).
        ApplyTheme(_settingsSvc.Current.Theme);
        _themeWatcher = new SystemThemeWatcher(Dispatcher);
        _themeWatcher.EffectiveThemeChanged += t =>
        {
            if (_settingsSvc.Current.Theme == AppThemeMode.FollowSystem) ApplyTheme(AppThemeMode.FollowSystem);
        };
        _themeWatcher.Start();
        StartupLog.Log($"OnStartup: theme applied  theme={_settingsSvc.Current.Theme}");

        _creds = new WindowsCredentialStore();
        StartupLog.Log("OnStartup: WindowsCredentialStore created");
        _factory = new CalDavClientFactory(_creds, () => _settingsSvc.Current.AcceptInsecureTls);
        _viewSvc = new CalendarViewService(new IcalRecurrenceExpander());
        _events = new EventService(_store.Events, _store.SyncOperations);
        StartupLog.Log("OnStartup: core services built");

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
        StartupLog.Log("OnStartup: SyncRunner started (first sync ~T+3s)");

        _reminders = new ReminderService(_store, new IcalRecurrenceExpander(), () => _settingsSvc.Current, OnReminder);
        StartupLog.Log("OnStartup: ReminderService created");

        _vm = new WeekViewModel(_store, _viewSvc, _events, _sync,
            onAddNew: date => _ = OpenQuickAddAsync(date, null),
            onEdit: id => _ = OpenQuickAddAsync(null, id));
        StartupLog.Log("OnStartup: WeekViewModel created");

        _week = new WeekWindow(_vm);
        ApplyWindowSettings(_week, _settingsSvc.Current);
        StartupLog.Log("OnStartup: WeekWindow constructed");
        _week.Show();
        StartupLog.Log("OnStartup: WeekWindow.Show done");
        _week.ApplyGadgetStyles();
        ApplyWindowMode(_week, _settingsSvc.Current);
        StartupLog.Log("OnStartup: gadget styles + window mode applied");

        _monthVm = new MonthViewModel(_store, _viewSvc, _sync,
            onAddNew: date => _ = OpenQuickAddAsync(date, null),
            onEdit: id => _ = OpenQuickAddAsync(null, id));
        _month = new MonthWindow(_monthVm);
        _month.BackRequested += BackToWeek;
        _week.MonthRequested += OpenMonth;
        StartupLog.Log("OnStartup: MonthWindow constructed");

            _tray = FindResource("TrayIcon");
            if (_tray is TaskbarIcon tray)
            {
                // Icon lives only in Application.Resources, so it never gets a Loaded event —
                // force it into existence. Efficiency mode off so our timers aren't throttled.
                StartupLog.Log("OnStartup: tray.ForceCreate (native)");
                tray.ForceCreate(enablesEfficiencyMode: false);
                StartupLog.Log("OnStartup: tray created");
            }
            else
            {
                StartupLog.Log("OnStartup: TrayIcon resource NOT found");
            }

            StartupLog.Log("OnStartup: WeekViewModel.NavigateTo");
            _vm.NavigateTo(WeekViewModel.MondayOf(DateTime.Today));
            StartupLog.Log("OnStartup: COMPLETE — window should be live");
        }
        catch (Exception ex)
        {
            StartupLog.Log(ex, "OnStartup: FAILED");
            MessageBox.Show(
                ex.ToString() + "\n\n日志已写入：" + (StartupLog.Path ?? "(none)"),
                "CalDesk 启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    /// <summary>Catch exceptions that escape <see cref="OnStartup"/> — background threads,
    /// fire-and-forget tasks, and the WPF dispatcher — so a silent exit leaves a trail.</summary>
    private void RegisterGlobalHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            StartupLog.Log(args.Exception, $"DispatcherUnhandledException (handled={args.Handled})");
            // Leave Handled=false so the real crash still surfaces for diagnosis.
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception
                     ?? new InvalidOperationException("UnhandledExceptionObject: " + args.ExceptionObject);
            StartupLog.Log(ex, $"AppDomain.UnhandledException (terminating={args.IsTerminating})");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
            StartupLog.Log(args.Exception, "TaskScheduler.UnobservedTaskException");
    }

    /// <summary>Swap the merged FontSizes dictionary for S/M/L (DynamicResource so all bound
    /// FontSize setters update live). Removes any existing FontSizes.* dict first.</summary>
    public void ApplyFontSize(FontSizeLevel level)
    {
        var src = level switch
        {
            FontSizeLevel.Medium => "Themes/FontSizes.M.xaml",
            FontSizeLevel.Large => "Themes/FontSizes.L.xaml",
            _ => "Themes/FontSizes.S.xaml",
        };
        var dict = Resources.MergedDictionaries;
        for (var i = dict.Count - 1; i >= 0; i--)
        {
            if (dict[i].Source?.OriginalString.Contains("FontSizes.") == true)
            {
                dict.RemoveAt(i);
            }
        }

        dict.Add(new ResourceDictionary { Source = new Uri(src, UriKind.Relative) });

        // R4 (§3.6): recompute MonthView bottom limits so 3-line min / 10-line cap track the new font tier.
        foreach (var w in Application.Current.Windows.OfType<Views.MonthWindow>())
        {
            w.ComputeBottomLimits();
        }
    }

    /// <summary>R6 (§3.6): swap the merged Palette dictionary in place (index replacement,
    /// prepare-new-then-replace-old) to avoid an intermediate null lookup that would flash
    /// default-themed pixels. Falls back to Insert(0) if no Palette entry exists yet.</summary>
    public void ApplyTheme(AppThemeMode mode)
    {
        var effective = mode == AppThemeMode.FollowSystem
            ? SystemThemeWatcher.GetEffectiveTheme() : mode;
        var src = effective == AppThemeMode.Dark ? "Themes/Palette.Dark.xaml" : "Themes/Palette.Light.xaml";
        var replacement = new ResourceDictionary { Source = new Uri(src, UriKind.Relative) };

        var dict = Resources.MergedDictionaries;
        int idx = -1;
        for (var i = 0; i < dict.Count; i++)
        {
            var os = dict[i].Source?.OriginalString ?? string.Empty;
            if (os.Contains("/Palette.", StringComparison.Ordinal)
                || os.EndsWith("Palette.Light.xaml", StringComparison.Ordinal)
                || os.EndsWith("Palette.Dark.xaml", StringComparison.Ordinal))
            {
                idx = i;
                break;
            }
        }

        if (idx >= 0) dict[idx] = replacement; else dict.Insert(0, replacement);
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

        // DIU size persistence: restore the user's last resized size (NaN ⇒ keep XAML default).
        if (!double.IsNaN(s.WindowWidth))
        {
            w.Width = s.WindowWidth;
        }

        if (!double.IsNaN(s.WindowHeight))
        {
            w.Height = s.WindowHeight;
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
        // DIU size persistence: restore the user's last resized size (NaN ⇒ keep XAML default).
        if (!double.IsNaN(s.MonthWindowWidth))
        {
            w.Width = s.MonthWindowWidth;
        }

        if (!double.IsNaN(s.MonthWindowHeight))
        {
            w.Height = s.MonthWindowHeight;
        }
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

        var owner = _month is { IsVisible: true } ? (Window?)_month : _week;
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
    private void OnSync(object sender, RoutedEventArgs e) => _ = _sync.RunAsync();
    private void OnSettings(object sender, RoutedEventArgs e) => OpenSettings();
    private void OnAbout(object sender, RoutedEventArgs e) => new AboutWindow { Owner = _week }.ShowDialog();
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

    private async void OpenSettings()
    {
        if (_settingsOpen)
        {
            return;
        }

        _settingsOpen = true;
        try
        {
            var vm = new SettingsViewModel(_store, _settingsSvc, _creds, _factory, _sync);
            var win = new SettingsWindow { DataContext = vm, Owner = _week };
            vm.Error += msg => MessageBox.Show(win, msg, "CalDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
            vm.RequestClose += win.Close;

            // Await init before the dialog so account fields / HasStoredPassword are ready and
            // the user cannot type a password that InitializeAsync would later blank (UI/VM desync).
            // Exceptions surface here instead of fire-and-forget.
            try
            {
                await vm.InitializeAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    win,
                    "加载设置失败：" + ex.Message,
                    "CalDesk",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

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
        finally
        {
            _settingsOpen = false;
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

        // Persist DIU window sizes (week + month). NaN stays NaN until the user resizes once.
        if (_week is { } weekWin)
        {
            s.WindowWidth = weekWin.Width;
            s.WindowHeight = weekWin.Height;
        }

        if (_month is { } monthWin)
        {
            s.MonthWindowWidth = monthWin.Width;
            s.MonthWindowHeight = monthWin.Height;
        }

        _settingsSvc.Save(s);
        _themeWatcher?.Dispose();
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
