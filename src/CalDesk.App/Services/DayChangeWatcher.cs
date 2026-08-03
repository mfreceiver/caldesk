using System.Windows.Threading;
using Microsoft.Win32;

namespace CalDesk.App.Services;

/// <summary>Detects local-date changes (midnight rollover, wake from sleep, manual
/// clock/timezone edits) and raises <see cref="DayChanged"/> once per new date.
/// <para>Trigger sources: a 60s <see cref="DispatcherTimer"/> poll (primary fallback —
/// NTP big-step and VM snapshot restore do NOT raise <c>TimeChanged</c>, so polling is
/// the only reliable backstop) plus <see cref="SystemEvents.PowerModeChanged"/> (Resume)
/// and <see cref="SystemEvents.TimeChanged"/> for faster detection.</para>
/// <para>R5: all state (<c>_lastKnownToday</c>) lives on the UI thread only —
/// DispatcherTimer ticks on the UI thread; SystemEvents fires on its OWN worker thread
/// (see <c>SystemThemeWatcher</c> R5 note) and hops in via <see cref="Dispatcher"/>.
/// No volatile/Interlocked needed.</para></summary>
public sealed class DayChangeWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<DateTime> _nowProvider;
    private readonly DispatcherTimer _timer;
    private DateOnly _lastKnownToday;
    private bool _started;

    // Static SystemEvents handlers need a static target. Cleared in Dispose.
    private static DayChangeWatcher? _current;

    /// <param name="nowProvider">Injectable clock for tests; defaults to <c>DateTime.Today</c>.</param>
    public DayChangeWatcher(Dispatcher dispatcher, Func<DateTime>? nowProvider = null)
    {
        _dispatcher = dispatcher;
        _nowProvider = nowProvider ?? (() => DateTime.Today);
        _timer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background,
            (_, _) => CheckDayChanged(), dispatcher);
        _lastKnownToday = DateOnly.FromDateTime(_nowProvider());
    }

    /// <summary>Raised exactly once when the local date advances past the last known date.
    /// Invoked on the UI thread.</summary>
    public event Action? DayChanged;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _current = this;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.TimeChanged += OnTimeChanged;
        _timer.Start();
    }

    private static void OnPowerModeChanged(object? s, PowerModeChangedEventArgs e)
    {
        // Wake may span multiple days; only Resume is interesting (Suspend fires before sleep).
        if (e.Mode == PowerModes.Resume) Hop();
    }

    private static void OnTimeChanged(object? s, EventArgs e) => Hop(); // clock / timezone edits

    private static void Hop()
    {
        // SystemEvents fires on its own worker thread — marshal into the UI thread.
        var w = _current;
        if (w is null) return;
        try { w._dispatcher.InvokeAsync(w.CheckDayChanged); }
        catch { /* dispatcher shutting down during exit */ }
    }

    private void CheckDayChanged()
    {
        // Runs on the UI thread only → _lastKnownToday has a single reader/writer.
        var today = DateOnly.FromDateTime(_nowProvider());
        if (today == _lastKnownToday) return;
        _lastKnownToday = today;
        try { DayChanged?.Invoke(); }
        catch { /* best-effort; never crash the host (cf. ReminderService) */ }
    }

    public void Dispose()
    {
        if (!_started) return;
        _started = false;
        _timer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.TimeChanged -= OnTimeChanged;
        _current = null;
    }
}
