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
    private Window? _lastShown;
    private SystemThemeWatcher? _themeWatcher;
    private DayChangeWatcher? _dayChangeWatcher;

    // T13/A4+B3: 统一 resize 机制 + resize-session 协调器. 每窗口一个 ResizeSession,
    // 通过 HwndSource hook 监听 WM_ENTERSIZEMOVE / WM_EXITSIZEMOVE 精确检测 resize
    // 边沿; 期间 SizeChanged 走 200ms DispatcherTimer 去抖, 不每像素落盘.
    private ResizeSession? _weekResize;
    private ResizeSession? _monthResize;

    /// <summary>T13/B3: SyncCompleted 在 resize 期间被 defer — 此标志在 resize 中记录
    /// "有未处理的 sync 完成", resize 结束时由 <see cref="OnAnyResizeEnded"/> 合并触发一次.
    /// 不会丢失最后一次 sync 完成事件.</summary>
    private bool _pendingSyncRefresh;

    /// <summary>T10/C1+D6: 抑制 <see cref="ApplyWindowSettings"/> /
    /// <see cref="ApplyMonthWindowSettings"/> 写 Left/Top 时由 WPF 同步触发的
    /// <c>LocationChanged</c> 镜像重入 — 防止"应用设置 → 写 Left/Top → LocationChanged
    /// → 写 settings → 反向污染"反馈环. 仅在 apply 期间为 true.</summary>
    private bool _applyingWindowSettings;

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

        // Date-change watcher: midnight rollover, wake from sleep, manual clock/timezone
        // edits. Raises DayChanged once per new local date → refresh both view models.
        _dayChangeWatcher = new DayChangeWatcher(Dispatcher);
        _dayChangeWatcher.DayChanged += () => DoRefreshAfterSync();
        _dayChangeWatcher.Start();

        _creds = new WindowsCredentialStore();
        StartupLog.Log("OnStartup: WindowsCredentialStore created");
        _factory = new CalDavClientFactory(_creds, () => _settingsSvc.Current.AcceptInsecureTls);
        // Shared parse cache for view + reminder expanders (T6); both must share one instance.
        var recurringCache = new RecurringEventCache();
        _viewSvc = new CalendarViewService(new IcalRecurrenceExpander(recurringCache));
        _events = new EventService(_store.Events, _store.SyncOperations);
        StartupLog.Log("OnStartup: core services built");

        _sync = new SyncRunner(_store, _factory, _settingsSvc.Current);
        _sync.SyncCompleted += (_, _) => _week?.Dispatcher.InvokeAsync(HandleSyncCompleted);
        _sync.Start();
        StartupLog.Log("OnStartup: SyncRunner started (first sync ~T+3s)");

        _reminders = new ReminderService(_store, new IcalRecurrenceExpander(recurringCache), () => _settingsSvc.Current, OnReminder);
        StartupLog.Log("OnStartup: ReminderService created");

        _vm = new WeekViewModel(_store, _viewSvc, _events, _sync,
            onAddNew: date => _ = OpenQuickAddAsync(date, null),
            onEdit: id => _ = OpenQuickAddAsync(null, id));
        StartupLog.Log("OnStartup: WeekViewModel created");

        _week = new WeekWindow(_vm);
        _week.Activated += (_, _) => _lastShown = _week;
        ApplyWindowSettings(_week, _settingsSvc.Current);
        // T10/C1: 订阅 LocationChanged 把实时位置镜像回 settings. 一次性订阅 (窗口
        // 构造后即订阅, 整个 App 生命周期不再变更); 镜像逻辑自身用 _applyingWindowSettings
        // 抑制 apply 触发的同步事件, 避免反馈环.
        _week.LocationChanged += (_, _) => MirrorWeekLocation();
        // Loaded 一次性: 启动阶段 ApplyWindowSettings 的 ClampOntoVisibleScreen 可能因
        // 窗口未 laid-out (ActualWidth=0) 安全 return; Loaded 触发时窗口已有实际尺寸,
        // 再 clamp 一次确保 saved 在断开屏上时窗口被搬到可见区.
        _week.Loaded += OnWeekLoaded;
        // T13/A4+B3: SizeChanged → ResizeSession.ScheduleUpdate. 该方法在 resize session
        // 期间（WM_ENTERSIZEMOVE..WM_EXITSIZEMOVE）只 restart 200ms debounce timer，避免
        // 每像素写 settings.json；debounce 到期 / resize 结束 / 非 resize 的一次性事件
        // 才会走 onCommit 真正 Save。
        _week.SizeChanged += (_, _) => _weekResize?.ScheduleUpdate();
        // ResizeSession 必须在 HWND 存在后创建（OnSourceInitialized 之后）。
        _week.SourceInitialized += (_, _) => AttachWeekResizeSession();
        StartupLog.Log("OnStartup: WeekWindow constructed");
        _week.ApplyGadgetStyles();
        ApplyWindowMode(_week, _settingsSvc.Current);
        StartupLog.Log("OnStartup: gadget styles + window mode applied");

        _monthVm = new MonthViewModel(_store, _viewSvc, _sync,
            onAddNew: date => _ = OpenQuickAddAsync(date, null),
            onEdit: id => _ = OpenQuickAddAsync(null, id));
        _month = new MonthWindow(_monthVm);
        // Apply saved size/lock ONCE at construction; OpenMonth only Show()s afterwards,
        // so tray hide/reopen preserves the user's in-session resize instead of resetting it.
        ApplyMonthWindowSettings(_month, _settingsSvc.Current);
        _month.Activated += (_, _) => _lastShown = _month;
        // T10/C1: 镜像 MonthWindow 实时位置到 settings.MonthWindowLeft/Top.
        _month.LocationChanged += (_, _) => MirrorMonthLocation();
        _month.Loaded += OnMonthLoaded;
        // T13: 同 WeekWindow — SizeChanged 走 ResizeSession.ScheduleUpdate 去抖落盘。
        _month.SizeChanged += (_, _) => _monthResize?.ScheduleUpdate();
        _month.SourceInitialized += (_, _) => AttachMonthResizeSession();
        _month.BackRequested += BackToWeek;
        _week.MonthRequested += OpenMonth;
        StartupLog.Log("OnStartup: MonthWindow constructed");

        if (_settingsSvc.Current.LastViewMode == ViewMode.Month)
        {
            OpenMonth();
        }
        else
        {
            _week.Show();
            _lastShown = _week;
            StartupLog.Log("OnStartup: WeekWindow.Show done");
        }

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

    // --- T13/A4+B3: resize session 协调器 + SyncCompleted defer 合并刷新 ---

    /// <summary>绑定 <see cref="WeekWindow"/> 的 <c>SourceInitialized</c>，HWND 存在后
    /// 立即创建 <see cref="ResizeSession"/> 并订阅 <c>ResizeEnded</c>。幂等：重复调用
    /// 不会重建 session（避免拖动 / 重新 Show 触发重复订阅）。</summary>
    private void AttachWeekResizeSession()
    {
        if (_weekResize != null || _week is null) return;
        _weekResize = new ResizeSession(
            _week,
            onUpdate: UpdateWeekSizeInMemory,
            onCommit: SaveWeekSizeToDisk);
        _weekResize.ResizeEnded += OnAnyResizeEnded;
    }

    /// <summary>绑定 <see cref="MonthWindow"/> 的 <c>SourceInitialized</c>，同上。</summary>
    private void AttachMonthResizeSession()
    {
        if (_monthResize != null || _month is null) return;
        _monthResize = new ResizeSession(
            _month,
            onUpdate: UpdateMonthSizeInMemory,
            onCommit: SaveMonthSizeToDisk);
        _monthResize.ResizeEnded += OnAnyResizeEnded;
    }

    private void UpdateWeekSizeInMemory(double w, double h)
    {
        var c = _settingsSvc.Current;
        c.WindowWidth = w;
        c.WindowHeight = h;
    }

    private void UpdateMonthSizeInMemory(double w, double h)
    {
        var c = _settingsSvc.Current;
        c.MonthWindowWidth = w;
        c.MonthWindowHeight = h;
    }

    private void SaveWeekSizeToDisk(double w, double h)
    {
        var c = _settingsSvc.Current;
        c.WindowWidth = w;
        c.WindowHeight = h;
        _settingsSvc.Save(c);
    }

    private void SaveMonthSizeToDisk(double w, double h)
    {
        var c = _settingsSvc.Current;
        c.MonthWindowWidth = w;
        c.MonthWindowHeight = h;
        _settingsSvc.Save(c);
    }

    /// <summary>任一窗口 resize 结束 (WM_EXITSIZEMOVE) 触发。若 resize 期间收到过
    /// <see cref="SyncCompleted"/> 且被 defer，合并触发一次刷新 — 不丢失最后一次
    /// 同步完成事件，但也不在 resize 中与每像素落盘抢 UI 线程。
    /// <para><b>rev-grok 修复：</b> 另一窗口可能仍在 resize。重新检查
    /// <see cref="AnyResizing"/>；若仍为 true（说明另一窗口 resize session 未结束），
    /// 保留 <c>_pendingSyncRefresh = true</c> 直接 return — 等待另一窗口也结束，
    /// 避免提前刷新导致 sync 期间再被窗口 resize 干扰。</para>
    /// </summary>
    private void OnAnyResizeEnded()
    {
        if (!_pendingSyncRefresh) return;
        // 另一窗口可能仍在 resize (e.g. Week 结束但 Month 还在拖): 重新检查
        // AnyResizing 仍为 true, 保留 pending, 等 Month 也结束再合并触发.
        if (AnyResizing) return;
        _pendingSyncRefresh = false;
        DoRefreshAfterSync();
    }

    /// <summary>UI 线程的 <see cref="SyncRunner.SyncCompleted"/> 入口。resize 期间
    /// 不直接触发 LoadAsync（避免与每像素 SizeChanged 抢线程），仅标记 pending；
    /// 由 <see cref="OnAnyResizeEnded"/> 合并触发一次。</summary>
    private void HandleSyncCompleted()
    {
        if (AnyResizing)
        {
            _pendingSyncRefresh = true;
            return;
        }
        DoRefreshAfterSync();
    }

    /// <summary>任一窗口当前在 user-driven resize 拖动 session 中。</summary>
    private bool AnyResizing =>
        (_weekResize is { IsResizing: true }) || (_monthResize is { IsResizing: true });

    private void DoRefreshAfterSync()
    {
        _ = _vm.LoadAsync();
        if (_monthVm is not null)
        {
            _ = _monthVm.LoadAsync();
        }
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

    private void ApplyWindowSettings(WeekWindow w, AppSettings s)
    {
        _applyingWindowSettings = true;
        try
        {
            w.IsPositionLocked = s.LockedPosition;
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

            // T10/G3: 多屏 + 负坐标 clamp. 启动阶段窗口未 laid-out (ActualWidth=0) 时
            // ClampOntoVisibleScreen 因 zero-size 防御会安全 return; OpenSettings 重应用
            // 时窗口已 shown, 此调用真正生效. 放在 try 内以确保 guard 在 clamp 全程生效.
            WindowBoundsGuard.ClampOntoVisibleScreen(w);
        }
        finally
        {
            _applyingWindowSettings = false;
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

    private void ApplyMonthWindowSettings(MonthWindow w, AppSettings s)
    {
        _applyingWindowSettings = true;
        try
        {
            w.IsPositionLocked = s.LockedPosition;
            // T10/C1+D6: 月窗独立位置 (不再复用周窗坐标). NaN/从未保存过 ⇒ 不赋值,
            // 保留 WPF XAML 默认 (month 窗口由 OpenMonth 决定起点, 后续可拖到任意位置).
            if (!double.IsNaN(s.MonthWindowLeft))
            {
                w.Left = s.MonthWindowLeft;
            }

            if (!double.IsNaN(s.MonthWindowTop))
            {
                w.Top = s.MonthWindowTop;
            }

            // DIU size persistence: restore the user's last resized size (NaN ⇒ keep XAML default).
            if (!double.IsNaN(s.MonthWindowWidth))
            {
                w.Width = s.MonthWindowWidth;
            }

            if (!double.IsNaN(s.MonthWindowHeight))
            {
                w.Height = s.MonthWindowHeight;
            }

            // T10/G3: 月窗也走 WindowBoundsGuard — 若 MonthWindowLeft/Top 落在已断开的
            // 显示器, 把窗口搬到可见区. 放在 try 内以确保 guard 在 clamp 全程生效.
            WindowBoundsGuard.ClampOntoVisibleScreen(w);
        }
        finally
        {
            _applyingWindowSettings = false;
        }
    }

    /// <summary>WeekWindow 的 <c>LocationChanged</c> 镜像 — 把实时位置写入
    /// <see cref="AppSettings.WindowLeft"/><see cref="AppSettings.WindowTop"/> (仅内存,
    /// OnExit 兜底 Save). apply 期间被 <c>_applyingWindowSettings</c> 抑制, 防止
    /// "apply → set Left → LocationChanged → 反写 settings" 反馈环.</summary>
    private void MirrorWeekLocation()
    {
        if (_applyingWindowSettings) return;
        if (_week is null) return;
        var left = _week.Left;
        var top = _week.Top;
        // Defensive: 启动早期 / 隐藏态时 WPF 可能返回 NaN — 不污染 settings.
        if (double.IsNaN(left) || double.IsInfinity(left) ||
            double.IsNaN(top) || double.IsInfinity(top))
        {
            return;
        }
        var c = _settingsSvc.Current;
        c.WindowLeft = left;
        c.WindowTop = top;
    }

    /// <summary>MonthWindow 的 <c>LocationChanged</c> 镜像, 写
    /// <see cref="AppSettings.MonthWindowLeft"/><see cref="AppSettings.MonthWindowTop"/>.</summary>
    private void MirrorMonthLocation()
    {
        if (_applyingWindowSettings) return;
        if (_month is null) return;
        var left = _month.Left;
        var top = _month.Top;
        if (double.IsNaN(left) || double.IsInfinity(left) ||
            double.IsNaN(top) || double.IsInfinity(top))
        {
            return;
        }
        var c = _settingsSvc.Current;
        c.MonthWindowLeft = left;
        c.MonthWindowTop = top;
    }

    /// <summary>WeekWindow Loaded 一次性回调: 此时窗口已 laid-out, 再 clamp 一次确保
    /// saved 位置在断开屏上时窗口被搬到可见区 (覆盖 ApplyWindowSettings 阶段的 zero-size
    /// no-op). 标记 _weekClampedOnLoad 避免重复. 包裹 _applyingWindowSettings 抑制
    /// clamp 触发的 LocationChanged 镜像回写, 因为本次 clamp 是系统恢复的一部分, 不应
    /// 将 clamp 后的位置视为用户手动拖动 — 后续 OnExit 或用户主动拖拽才会持久化.</summary>
    private bool _weekClampedOnLoad;
    private void OnWeekLoaded(object? sender, RoutedEventArgs e)
    {
        if (_weekClampedOnLoad) return;
        _weekClampedOnLoad = true;
        _applyingWindowSettings = true;
        try
        {
            WindowBoundsGuard.ClampOntoVisibleScreen(_week);
        }
        finally
        {
            _applyingWindowSettings = false;
        }
    }

    private bool _monthClampedOnLoad;
    private void OnMonthLoaded(object? sender, RoutedEventArgs e)
    {
        if (_monthClampedOnLoad) return;
        _monthClampedOnLoad = true;
        _applyingWindowSettings = true;
        try
        {
            WindowBoundsGuard.ClampOntoVisibleScreen(_month);
        }
        finally
        {
            _applyingWindowSettings = false;
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

    private void OnShowWindow(object sender, RoutedEventArgs e) => ToggleActiveWindow();

    private void ToggleActiveWindow()
    {
        if (_month.IsVisible)
        {
            _lastShown = _month;
            _month.Hide();
            return;
        }

        if (_week.IsVisible)
        {
            _lastShown = _week;
            _week.Hide();
            return;
        }

        if (_lastShown == _month)
        {
            OpenMonth();
            return;
        }

        var window = _lastShown as WeekWindow ?? _week;
        window.Show();
        window.Activate();
        _lastShown = window;
    }
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

        if (!_month.IsVisible)
        {
            _month.Show();
        }

        _lastShown = _month;

        _month.ApplyGadgetStyles();
        ApplyWindowMode(_month, _settingsSvc.Current);
        _monthVm.NavigateTo(DateOnly.FromDateTime(DateTime.Today));
        _week.Hide();
        _settingsSvc.Current.LastViewMode = ViewMode.Month;
        _settingsSvc.Save(_settingsSvc.Current);
    }

    private void BackToWeek()
    {
        _month?.Hide();
        if (_week is not null && !_week.IsVisible)
        {
            _week.Show();
        }

        _lastShown = _week;

        _settingsSvc.Current.LastViewMode = ViewMode.Week;
        _settingsSvc.Save(_settingsSvc.Current);

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

        // T10/C2: 删 `!s.LockedPosition` 守卫. 锁定状态只影响 *用户能否拖动*; 不应阻止
        // 退出时持久化当前位置. 锁定期间若用户通过拖屏幕外 / OpenSettings 改坐标,
        // 这些值仍需真实保存. 保留 NaN/Infinity 防御 — WPF 未 laid-out 时 Left/Top
        // 可能是 NaN, 此时不动 settings, 让下次启动从最近一次有效值恢复.
        if (_week is { } w)
        {
            var left = w.Left;
            var top = w.Top;
            if (!double.IsNaN(left) && !double.IsInfinity(left) &&
                !double.IsNaN(top) && !double.IsInfinity(top))
            {
                s.WindowLeft = left;
                s.WindowTop = top;
            }
        }

        if (_month is { } mw)
        {
            var left = mw.Left;
            var top = mw.Top;
            if (!double.IsNaN(left) && !double.IsInfinity(left) &&
                !double.IsNaN(top) && !double.IsInfinity(top))
            {
                s.MonthWindowLeft = left;
                s.MonthWindowTop = top;
            }
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
        _dayChangeWatcher?.Dispose();
        _reminders?.Dispose();
        _sync?.Dispose();
        // T13: 释放 ResizeSession — stop debounce timer + RemoveHook HwndSource.
        // 必须在窗口销毁之前; OnExit 时窗口可能已被 GC/进程退出, 但 HwndSource 仍
        // 可访问, RemoveHook 失败有 try/catch 兜底.
        _weekResize?.Dispose();
        _monthResize?.Dispose();
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
