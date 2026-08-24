using System.Windows.Threading;
using Microsoft.Win32;

namespace CalDesk.App.Services;

/// <summary>Detects local-date changes (midnight rollover, wake from sleep, manual
/// clock/timezone edits) and raises <see cref="DayChanged"/> once per new date.
/// <para>Trigger sources: an adaptive <see cref="DispatcherTimer"/> poll — the interval
/// shrinks to ~2s just before each local midnight and is capped at 60s otherwise. The 60s
/// cap backstops clock step-backs / NTP drift / VM snapshot restore, which do NOT raise
/// <c>TimeChanged</c>, so polling stays the only reliable fallback — plus
/// <see cref="SystemEvents.PowerModeChanged"/> (Resume) and
/// <see cref="SystemEvents.TimeChanged"/> for faster detection.</para>
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
        // Use the (priority, dispatcher) ctor — the (interval, priority, tick, dispatcher)
        // overload AUTO-STARTS the timer at construction. Interval set here is a 60s
        // placeholder; Start() recomputes the adaptive interval before starting.
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(60)
        };
        _timer.Tick += (_, _) => OnTick();
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
        UpdateTimerInterval(); // compute adaptive interval before first tick
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
        // Route through OnTick (not bare CheckDayChanged) so the adaptive interval is
        // also recomputed after clock/timezone edits and resume: a same-date jump near
        // midnight could otherwise keep a stale schedule and detect rollover up to 60s late.
        var w = _current;
        if (w is null) return;
        try { w._dispatcher.InvokeAsync(w.OnTick); }
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

    /// <summary>DispatcherTimer tick: check day change, then recompute interval for next tick.</summary>
    private void OnTick()
    {
        CheckDayChanged();
        UpdateTimerInterval();
    }

    /// <summary>Adapt the timer interval so we fire shortly after the next local midnight
    /// (within 2s), capped at 60s. This ensures timely day-change detection near midnight
    /// while keeping the 60s backstop for clock step-backs / NTP drift / VM snapshot restore.</summary>
    private void UpdateTimerInterval()
    {
        // _nowProvider returns DateTime.Today (midnight) in production — not useful for
        // time-of-day arithmetic — so use DateTime.Now (wall-clock local time) directly.
        var now = DateTime.Now;
        var nextMidnight = now.Date.AddDays(1); // 00:00:00 local tomorrow
        var seconds = (nextMidnight - now).TotalSeconds;
        // +2s buffer ensures we fire AFTER midnight, not right at the boundary.
        var next = Math.Min(60.0, Math.Max(2.0, seconds + 2.0));
        _timer.Interval = TimeSpan.FromSeconds(next);
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
