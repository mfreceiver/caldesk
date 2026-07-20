using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CalDesk.App.Diagnostics;
using CalDesk.App.Models;
using CalDesk.App.Services;
using CalDesk.CalDav;
using CalDesk.Core.Models;
using CalDesk.Core.Services;
using CalDesk.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace CalDesk.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly StorageServices _store;
    private readonly SettingsService _settingsService;
    private readonly ICredentialStore _credentials;
    private readonly ICalDavClientFactory _factory;
    private readonly SyncRunner _sync;
    private AppSettings _settings;

    public event Action? RequestClose;
    public event Action<string>? Error;

    // ---- CalDAV account ----
    [ObservableProperty] private string _serverUrl = string.Empty;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;

    /// <summary>Whether Windows Credential Manager already holds a secret for this account.
    /// The password box stays empty on open; this flag only drives the UI hint.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordHint))]
    private bool _hasStoredPassword;

    /// <summary>Hint under the password field: stored vs unset (box left empty keeps the stored secret).</summary>
    public string PasswordHint => HasStoredPassword
        ? "已保存密码（留空保持不变）"
        : "未设置密码";

    [ObservableProperty] private bool _acceptInsecureTls;
    [ObservableProperty] private int _syncIntervalMinutes = 5;

    // ---- desktop window ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLockPositionEnabled))]
    private WindowMode _windowMode = WindowMode.Normal;

    [ObservableProperty] private double _opacity = 0.92;
    [ObservableProperty] private bool _lockedPosition;
    [ObservableProperty] private bool _startWithWindows;

    // ---- defaults ----
    [ObservableProperty] private int _defaultDurationMinutes = 60;
    [ObservableProperty] private int _defaultReminderMinutes = 15;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveAccount), nameof(CanTestConnection))]
    private bool _isBusy;

    [ObservableProperty] private string _statusText = string.Empty;

    /// <summary>Lock-position only has effect in Desktop-bottom mode; disabled otherwise.</summary>
    public bool IsLockPositionEnabled => WindowMode == WindowMode.Desktop;

    public bool CanSaveAccount => !IsBusy;
    public bool CanTestConnection => !IsBusy;

    /// <summary>Calendars discovered on the server (populated after connect). Empty until connected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCalendars))]
    private List<CalendarInfo> _calendars = new();

    public bool HasCalendars => Calendars.Count > 0;

    [ObservableProperty] private CalendarInfo? _selectedCalendar;

    /// <summary>Rows for the "日历管理" panel — one per known calendar. Each row exposes
    /// bindable <see cref="CalendarRow.IsSelected"/> / <see cref="CalendarRow.Color"/>
    /// which the user edits; <see cref="SavePropertiesCommand"/> persists the deltas
    /// via <see cref="SaveCalendarRowsAsync"/> (local DB only, no server sync).</summary>
    public ObservableCollection<CalendarRow> CalendarRows { get; } = new();

    /// <summary>Current S/M/L font tier mirrored from <see cref="AppSettings.FontSize"/>.
    /// Bound to the radio-button group in the Display section; setter applies live + persists.</summary>
    [ObservableProperty] private FontSizeLevel _fontSize;

    /// <summary>Theme mode (FollowSystem/Light/Dark) mirrored from <see cref="AppSettings.Theme"/>.
    /// Bound to the theme radio group (§3.17); setter applies live via <c>App.ApplyTheme</c> + persists.</summary>
    [ObservableProperty] private AppThemeMode _theme = AppThemeMode.Light;

    public SettingsViewModel(StorageServices store, SettingsService settingsService, ICredentialStore credentials, ICalDavClientFactory factory, SyncRunner sync)
    {
        _store = store;
        _settingsService = settingsService;
        _credentials = credentials;
        _factory = factory;
        _sync = sync;
        _settings = settingsService.Current;
    }

    public async Task InitializeAsync()
    {
        var account = (await _store.Accounts.GetAllAsync()).FirstOrDefault();
        ServerUrl = account?.ServerUrl ?? string.Empty;
        Username = account?.Username ?? string.Empty;
        // Never preload the secret into the form (security). Only surface whether one is stored.
        Password = string.Empty;
        HasStoredPassword = account is not null
                            && !string.IsNullOrEmpty(account.CredentialTarget)
                            && await _credentials.ExistsAsync(account.CredentialTarget);

        AcceptInsecureTls = _settings.AcceptInsecureTls;
        SyncIntervalMinutes = _settings.SyncIntervalMinutes;

        WindowMode = _settings.WindowMode;
        Opacity = _settings.Opacity;
        LockedPosition = _settings.LockedPosition;
        StartWithWindows = _settings.StartWithWindows;

        DefaultDurationMinutes = _settings.DefaultDurationMinutes;
        DefaultReminderMinutes = _settings.DefaultReminderMinutes;

        FontSize = _settings.FontSize;
        Theme = _settings.Theme;

        await RefreshCalendarsAsync();
        SelectedCalendar = Calendars.FirstOrDefault(c => c.Id == _settings.DefaultCalendarId)
                           ?? Calendars.FirstOrDefault();
    }

    private async Task RefreshCalendarsAsync()
    {
        var list = await _store.Calendars.GetAllAsync();
        var prev = SelectedCalendar?.Id;
        Calendars = list.OrderBy(c => c.DisplayName).ToList();
        SelectedCalendar = Calendars.FirstOrDefault(c => c.Id == prev) ?? Calendars.FirstOrDefault();

        // Rebuild the management rows from the latest stored state. Snapshot IsSelected/Color
        // at row-build time so SavePropertiesAsync can diff against the stored values.
        CalendarRows.Clear();
        foreach (var c in Calendars)
        {
            CalendarRows.Add(new CalendarRow(c));
        }
    }

    partial void OnFontSizeChanged(FontSizeLevel value)
    {
        // Apply live (swap merged FontSizes dictionary) + persist so the choice survives restart.
        if (Application.Current is App app)
        {
            app.ApplyFontSize(value);
        }

        _settings.FontSize = value;
        _settingsService.Save(_settings);
    }

    /// <summary>§3.17: apply theme live (swap Palette dictionary) + persist so the choice
    /// survives restart. FollowSystem is resolved to the OS theme at apply time.</summary>
    partial void OnThemeChanged(AppThemeMode value)
    {
        if (Application.Current is App app)
        {
            app.ApplyTheme(value);
        }

        _settings.Theme = value;
        _settingsService.Save(_settings);
    }

    /// <summary>Builds the account row from the form fields (without persisting).</summary>
    private async Task<Account> GetFormAccountAsync()
    {
        var account = (await _store.Accounts.GetAllAsync()).FirstOrDefault() ?? new Account();
        account.ServerUrl = ServerUrl.Trim();
        account.Username = Username.Trim();
        account.DisplayName = string.IsNullOrWhiteSpace(Username) ? account.DisplayName : Username.Trim();
        if (string.IsNullOrEmpty(account.CredentialTarget))
        {
            account.CredentialTarget = $"CalDesk:account:{account.Id}";
        }

        return account;
    }

    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    private async Task TestConnectionAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在测试连接…";
        try
        {
            var account = await GetFormAccountAsync();
            // Never persist from "测试连接" — no credential writes, no TLS settings mutation.
            // Form password (if any) and form AcceptInsecureTls apply to this probe client only.
            // Persistence stays in SaveAccountAsync / SaveProperties only.
            await using var client = await CreateClientForDiscoveryAsync(
                account,
                passwordOverride: Password,
                acceptInsecureTls: AcceptInsecureTls);
            var discovered = await client.DiscoverCalendarsAsync();
            StatusText = $"连接成功，发现 {discovered.Count} 个日历";
        }
        catch (Exception ex)
        {
            StatusText = "连接失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveAccount))]
    private async Task SaveAccountAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在保存并同步…";
        try
        {
            var account = await GetFormAccountAsync();
            await _store.Accounts.UpsertAsync(account);

            // Only write when the box has a new value; empty means keep the stored secret.
            if (!string.IsNullOrEmpty(Password))
            {
                await _credentials.SetPasswordAsync(account.CredentialTarget, Password);
                HasStoredPassword = true;
            }

            _settings.AcceptInsecureTls = AcceptInsecureTls;
            _settings.SyncIntervalMinutes = Math.Clamp(SyncIntervalMinutes, 1, 1440);
            _settingsService.Save(_settings);

            // Discover & merge calendars (preserve user-set Color/IsSelected — only backfill missing).
            await using var client = await CreateClientForDiscoveryAsync(account);
            var discovered = await client.DiscoverCalendarsAsync();
            var existing = (await _store.Calendars.GetAllAsync()).ToDictionary(c => c.RemoteUrl);
            foreach (var remote in discovered)
            {
                if (existing.TryGetValue(remote.RemoteUrl, out var ex))
                {
                    ex.DisplayName = remote.DisplayName;
                    // T6-C3: never overwrite a user-chosen color — only adopt the server color
                    // when nothing is stored yet (existing.Color null/empty).
                    if (string.IsNullOrEmpty(ex.Color))
                    {
                        ex.Color = remote.Color;
                    }

                    // SupportsSyncToken/Ctag are server state, always refresh.
                    ex.SupportsSyncToken = remote.SupportsSyncToken;
                    ex.Ctag = remote.Ctag;
                    ex.AccountId = account.Id;
                    // ex.IsSelected intentionally preserved — a user-hidden calendar stays hidden.
                    await _store.Calendars.UpsertAsync(ex);
                }
                else
                {
                    remote.AccountId = account.Id;
                    remote.IsSelected = true;
                    await _store.Calendars.UpsertAsync(remote);
                }
            }

            _sync.ReapplyInterval(_settings);
            await _sync.RunAsync();

            await RefreshCalendarsAsync();
            StatusText = "已连接并同步";
        }
        catch (Exception ex)
        {
            StatusText = "保存失败：" + ex.Message;
            Error?.Invoke(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>§3.14 S4: save calendar rows (local) then non-account properties, broadcast
    /// <see cref="CalendarColorsChangedMessage"/> so open week/month windows refresh color dots
    /// without a server sync, then close.</summary>
    /// <remarks>
    /// Credentials are written only from <see cref="SaveAccountAsync"/>, and only when
    /// <see cref="Password"/> is non-empty. <see cref="TestConnectionAsync"/> never mutates
    /// the credential store. This method never touches <see cref="ICredentialStore"/> so an
    /// empty password box cannot clobber a stored secret.
    /// Order: calendars first, then settings (R13) — calendar failure aborts before settings write.
    /// </remarks>
    [RelayCommand]
    private async Task SavePropertiesAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            // R13: calendars first — failure leaves settings untouched (no partial commit).
            await SaveCalendarRowsAsync();

            _settings.WindowMode = WindowMode;
            _settings.Opacity = Math.Clamp(Opacity, 0.15, 1.0);
            // Lock position only applies in Desktop-bottom mode.
            _settings.LockedPosition = WindowMode == WindowMode.Desktop && LockedPosition;
            _settings.StartWithWindows = StartWithWindows;
            _settings.DefaultDurationMinutes = DefaultDurationMinutes;
            _settings.DefaultReminderMinutes = DefaultReminderMinutes;
            _settings.DefaultCalendarId = SelectedCalendar?.Id;
            _settingsService.Save(_settings);

            StartupHelper.Set(StartWithWindows, Environment.ProcessPath ?? AppExePath());

            // Refresh open week/month windows (local color/selection only — no server sync).
            WeakReferenceMessenger.Default.Send(new CalendarColorsChangedMessage());
            RequestClose?.Invoke();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Diff CalendarRows against stored calendars and upsert changed IsSelected/Color.
    /// Local DB only — does not call <see cref="SyncRunner"/>.</summary>
    private async Task SaveCalendarRowsAsync()
    {
        var stored = (await _store.Calendars.GetAllAsync()).ToDictionary(c => c.Id);
        foreach (var row in CalendarRows)
        {
            if (!stored.TryGetValue(row.Id, out var cur))
            {
                continue;
            }

            if (cur.IsSelected != row.IsSelected || !StringEquals(cur.Color, row.Color))
            {
                await _store.Calendars.UpsertAsync(row.ToCalendarInfo(cur));
            }
        }
    }

    private static bool StringEquals(string? a, string? b)
        => string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();

    private static string AppExePath()
    {
        var module = System.Diagnostics.Process.GetCurrentProcess().MainModule;
        return module?.FileName ?? Environment.ProcessPath ?? "CalDesk.exe";
    }

    /// <summary>Create a CalDAV client for discovery, wiring a step logger into
    /// <c>%AppData%\CalDesk\caldesk.log</c> when the concrete factory is available.
    /// When <paramref name="passwordOverride"/> is non-empty, that secret is used for Basic
    /// auth without writing to <see cref="ICredentialStore"/> (for connection probes).
    /// When <paramref name="acceptInsecureTls"/> is set, TLS policy applies to this client
    /// only and does not mutate <see cref="SettingsService.Current"/>.</summary>
    private async Task<ICalDavClient> CreateClientForDiscoveryAsync(
        Account account,
        string? passwordOverride = null,
        bool? acceptInsecureTls = null)
    {
        if (_factory is CalDavClientFactory concrete)
        {
            if (!string.IsNullOrEmpty(passwordOverride))
            {
                return concrete.CreateWithPassword(account, passwordOverride, StartupLog.Log, acceptInsecureTls);
            }

            if (acceptInsecureTls.HasValue)
            {
                // Probe with form TLS; password from store (empty override means keep stored secret).
                var password = await _credentials.GetPasswordAsync(account.CredentialTarget) ?? string.Empty;
                return concrete.CreateWithPassword(account, password, StartupLog.Log, acceptInsecureTls);
            }

            return await concrete.CreateAsync(account, StartupLog.Log);
        }

        // Interface path has no password / TLS override; use store + factory defaults.
        return await _factory.CreateAsync(account);
    }

    /// <summary>One row in the "日历管理" panel. Wraps a <see cref="CalendarInfo"/> and exposes
    /// the two editable fields (<see cref="IsSelected"/>, <see cref="Color"/>) as bindable
    /// properties. <see cref="ToCalendarInfo"/> produces an updated snapshot for upsert;
    /// the caller passes the existing stored row so server-side fields (RemoteUrl, SyncToken,
    /// etc.) are preserved.</summary>
    public sealed partial class CalendarRow : ObservableObject
    {
        private readonly CalendarInfo _source;

        public CalendarRow(CalendarInfo source)
        {
            _source = source;
            _isSelected = source.IsSelected;
            _color = source.Color;
        }

        public Guid Id => _source.Id;

        public string DisplayName => _source.DisplayName;

        /// <summary>Account id (used for diagnostics / future grouping; not shown directly).</summary>
        public Guid AccountId => _source.AccountId;

        [ObservableProperty] private bool _isSelected;
        [ObservableProperty] private string? _color;

        /// <summary>Build an updated <see cref="CalendarInfo"/> to upsert. Server-managed
        /// fields (RemoteUrl, SyncToken, Ctag, SupportsSyncToken, SortOrder, UpdatedAt) are
        /// carried over from <paramref name="baseline"/>; only IsSelected + Color come from
        /// the row edits (and UpdatedAt is bumped to signal the user touch).</summary>
        public CalendarInfo ToCalendarInfo(CalendarInfo baseline)
        {
            return new CalendarInfo
            {
                Id = baseline.Id,
                AccountId = baseline.AccountId,
                RemoteUrl = baseline.RemoteUrl,
                DisplayName = baseline.DisplayName,
                Color = Color,
                SortOrder = baseline.SortOrder,
                SupportsSyncToken = baseline.SupportsSyncToken,
                Ctag = baseline.Ctag,
                SyncToken = baseline.SyncToken,
                IsSelected = IsSelected,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }
    }
}
