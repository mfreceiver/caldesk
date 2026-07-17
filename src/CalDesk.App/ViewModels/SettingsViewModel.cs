using System.IO;
using CalDesk.App.Diagnostics;
using CalDesk.App.Models;
using CalDesk.App.Services;
using CalDesk.CalDav;
using CalDesk.Core.Models;
using CalDesk.Core.Services;
using CalDesk.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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

        AcceptInsecureTls = _settings.AcceptInsecureTls;
        SyncIntervalMinutes = _settings.SyncIntervalMinutes;

        WindowMode = _settings.WindowMode;
        Opacity = _settings.Opacity;
        LockedPosition = _settings.LockedPosition;
        StartWithWindows = _settings.StartWithWindows;

        DefaultDurationMinutes = _settings.DefaultDurationMinutes;
        DefaultReminderMinutes = _settings.DefaultReminderMinutes;

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
            // Reflect the in-form TLS choice so the factory uses it for this attempt.
            _settings.AcceptInsecureTls = AcceptInsecureTls;

            var account = await GetFormAccountAsync();
            if (!string.IsNullOrEmpty(Password))
            {
                await _credentials.SetPasswordAsync(account.CredentialTarget, Password);
            }

            await using var client = await CreateClientForDiscoveryAsync(account);
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

            if (!string.IsNullOrEmpty(Password))
            {
                await _credentials.SetPasswordAsync(account.CredentialTarget, Password);
            }

            _settings.AcceptInsecureTls = AcceptInsecureTls;
            _settings.SyncIntervalMinutes = Math.Clamp(SyncIntervalMinutes, 1, 1440);
            _settingsService.Save(_settings);

            // Discover & merge calendars (preserve selection / ordering).
            await using var client = await CreateClientForDiscoveryAsync(account);
            var discovered = await client.DiscoverCalendarsAsync();
            var existing = (await _store.Calendars.GetAllAsync()).ToDictionary(c => c.RemoteUrl);
            foreach (var remote in discovered)
            {
                if (existing.TryGetValue(remote.RemoteUrl, out var ex))
                {
                    ex.DisplayName = remote.DisplayName;
                    ex.Color = remote.Color;
                    ex.SupportsSyncToken = remote.SupportsSyncToken;
                    ex.Ctag = remote.Ctag;
                    ex.AccountId = account.Id;
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

    /// <summary>Saves the non-account properties (window, defaults, autostart) and closes.</summary>
    [RelayCommand]
    private void SaveProperties()
    {
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
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();

    private static string AppExePath()
    {
        var module = System.Diagnostics.Process.GetCurrentProcess().MainModule;
        return module?.FileName ?? Environment.ProcessPath ?? "CalDesk.exe";
    }

    /// <summary>Create a CalDAV client for discovery, wiring a step logger into
    /// <c>%AppData%\CalDesk\caldesk.log</c> when the concrete factory is available.</summary>
    private Task<ICalDavClient> CreateClientForDiscoveryAsync(Account account)
    {
        if (_factory is CalDavClientFactory concrete)
        {
            return concrete.CreateAsync(account, StartupLog.Log);
        }

        return _factory.CreateAsync(account);
    }
}
