using System.Windows;
using System.Windows.Threading;
using CalDesk.App.Models;
using Microsoft.Win32;

namespace CalDesk.App.Services;

/// <summary>Watches OS per-app theme (HKCU\...\Personalize\AppsUseLightTheme).
/// R5: SystemEvents fires on its OWN worker thread (NOT UI) — must hop to UI thread.
/// Uses weak static target + SessionSwitch (unlock/RDP) + explicit Dispose unsubscribe.</summary>
public sealed class SystemThemeWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private bool _started;
    private static WeakReference<SystemThemeWatcher>? _current;

    public SystemThemeWatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public event Action<AppThemeMode>? EffectiveThemeChanged;

    public void Start()
    {
        if (_started) return;
        _current = new WeakReference<SystemThemeWatcher>(this);
        SystemEvents.UserPreferenceChanged += StaticPreferenceChanged;
        SystemEvents.SessionSwitch += StaticSessionSwitch;
        _started = true;
    }

    public void Dispose()
    {
        if (!_started) return;
        SystemEvents.UserPreferenceChanged -= StaticPreferenceChanged;
        SystemEvents.SessionSwitch -= StaticSessionSwitch;
        _started = false;
    }

    private static void StaticPreferenceChanged(object? s, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color) Notify();
    }

    private static void StaticSessionSwitch(object? s, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect
            or SessionSwitchReason.RemoteConnect) Notify();
    }

    private static void Notify()
    {
        if (_current?.TryGetTarget(out var w) != true) return;
        var dispatcher = w._dispatcher;
        dispatcher.BeginInvoke(DispatcherPriority.DataBind,
            new Action(() => w.EffectiveThemeChanged?.Invoke(SystemThemeWatcher.GetEffectiveTheme())));
    }

    public static AppThemeMode GetEffectiveTheme()
    {
        try
        {
            var v = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return v is int i && i == 0 ? AppThemeMode.Dark : AppThemeMode.Light;
        }
        catch { return AppThemeMode.Light; }
    }
}
