using System.IO;
using CalDesk.App.Models;
using CalDesk.App.Services;
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

    [ObservableProperty] private string _serverUrl = string.Empty;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private WindowMode _windowMode = WindowMode.Normal;
    [ObservableProperty] private double _opacity = 0.92;
    [ObservableProperty] private bool _lockedPosition;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private int _defaultDurationMinutes = 60;
    [ObservableProperty] private int _defaultReminderMinutes = 15;
    [ObservableProperty] private int _syncIntervalMinutes = 5;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isBusy;

    public bool CanSave => !IsBusy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public WindowMode[] WindowModes { get; } = Enum.GetValues<WindowMode>();

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

        WindowMode = _settings.WindowMode;
        Opacity = _settings.Opacity;
        LockedPosition = _settings.LockedPosition;
        StartWithWindows = _settings.StartWithWindows;
        DefaultDurationMinutes = _settings.DefaultDurationMinutes;
        DefaultReminderMinutes = _settings.DefaultReminderMinutes;
        SyncIntervalMinutes = _settings.SyncIntervalMinutes;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在连接并同步…";
        try
        {
            var account = (await _store.Accounts.GetAllAsync()).FirstOrDefault() ?? new Account();
            account.ServerUrl = ServerUrl.Trim();
            account.Username = Username.Trim();
            account.CredentialTarget = $"CalDesk:account:{account.Id}";
            account.DisplayName = Username.Trim();
            await _store.Accounts.UpsertAsync(account);

            if (!string.IsNullOrEmpty(Password))
            {
                await _credentials.SetPasswordAsync(account.CredentialTarget, Password);
            }

            _settings.WindowMode = WindowMode;
            _settings.Opacity = Math.Clamp(Opacity, 0.15, 1.0);
            _settings.LockedPosition = LockedPosition;
            _settings.StartWithWindows = StartWithWindows;
            _settings.DefaultDurationMinutes = DefaultDurationMinutes;
            _settings.DefaultReminderMinutes = DefaultReminderMinutes;
            _settings.SyncIntervalMinutes = Math.Clamp(SyncIntervalMinutes, 1, 1440);
            _settingsService.Save(_settings);

            StartupHelper.Set(StartWithWindows, Environment.ProcessPath ?? AppExePath());

            // Discover & merge calendars (preserve user selection / ordering).
            await using var client = await _factory.CreateAsync(account);
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

            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex.Message);
        }
        finally
        {
            IsBusy = false;
            StatusText = string.Empty;
        }
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();

    private static string AppExePath()
    {
        var module = System.Diagnostics.Process.GetCurrentProcess().MainModule;
        return module?.FileName ?? Environment.ProcessPath ?? "CalDesk.exe";
    }
}
