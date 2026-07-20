using System.ComponentModel;
using System.Threading;
using CalDesk.App.Diagnostics;
using CalDesk.App.Models;
using CalDesk.CalDav;
using CalDesk.CalDav.Synchronization;
using CalDesk.Core.Services;
using CalDesk.Storage;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CalDesk.App.Services;

/// <summary>Drives periodic + manual sync against the configured account and exposes status.
/// Status updates are marshaled to the UI thread (the Timer callback runs on the thread pool).</summary>
public sealed partial class SyncRunner : ObservableObject, IDisposable
{
    private readonly StorageServices _store;
    private readonly ICalDavClientFactory _factory;
    private TimeSpan _interval;
    private Timer? _timer;
    private int _running;
    /// <summary>Bumped when sync takes or releases status ownership; invalidates in-flight
    /// <see cref="RefreshPendingStatusAsync"/> and exit reapply snapshots.</summary>
    private int _statusGen;
    private DateTimeOffset? _lastSyncAt;

    // Captured on the UI thread at construction; null under tests (status updates just run inline).
    private readonly SynchronizationContext? _uiContext;

    [ObservableProperty]
    private string _status = "未同步";

    [ObservableProperty]
    private SyncReport? _lastReport;

    public event EventHandler? SyncCompleted;

    public SyncRunner(StorageServices store, ICalDavClientFactory factory, AppSettings settings)
    {
        _store = store;
        _factory = factory;
        _interval = TimeSpan.FromMinutes(Math.Max(1, settings.SyncIntervalMinutes));
        _uiContext = SynchronizationContext.Current;
    }

    /// <summary>Recompute status from last successful sync time + pending outbox counts.
    /// Safe after local create/edit/delete.</summary>
    /// <remarks>
    /// Race model (status ownership):
    /// <list type="bullet">
    /// <item>While <c>_running != 0</c>, sync owns <see cref="Status"/> (<c>同步中…</c> then exit text). Refresh no-ops.</item>
    /// <item>TOCTOU (refresh): between the early <c>_running</c> check and <see cref="SetStatus"/>, <see cref="RunAsync"/> may CAS into
    /// running. Snapshot <c>_statusGen</c>, await counts, then write only if still not running <b>and</b> gen unchanged
    /// (a completed sync between query and write also bumps gen). Otherwise abort — sync exit reapplies pending.
    /// The same gen is passed into <see cref="SetStatus"/> so a stale UI <c>Post</c> cannot apply after a later gen bump.</item>
    /// <item>Exit window: a local mutation during the exit status query sees <c>_running == 1</c> and skips refresh.
    /// <see cref="RunAsync"/> clears <c>_running</c>, bumps gen, then does one more pending reapply so the last write sees current outbox.</item>
    /// <item>Exit reapply TOCTOU: after release, a slow reapply from sync A must not overwrite sync B's final status.
    /// Reapply snapshots gen before await, then writes only if still not running (when <c>abortIfRunning</c>) <b>and</b>
    /// gen unchanged — same write guard as refresh (including inside the marshaled assign). Start/finish of
    /// <see cref="RunAsync"/> both increment gen so older snapshots die.</item>
    /// </list>
    /// </remarks>
    public async Task RefreshPendingStatusAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _running) != 0)
            return;

        var gen = Volatile.Read(ref _statusGen);
        var counts = await _store.SyncOperations.GetPendingCountsAsync(ct).ConfigureAwait(false);

        // Write-time guard: sync started or finished (gen bump) during the query → do not clobber.
        if (Volatile.Read(ref _running) != 0)
            return;
        if (gen != Volatile.Read(ref _statusGen))
            return;

        SetStatus(SyncStatusText.Format(_lastSyncAt?.ToLocalTime(), counts), requiredGen: gen);
    }

    /// <summary>Resting status: last successful sync time + pending counts.
    /// Used on sync exit paths that already own <c>_running</c>, or after release with <paramref name="abortIfRunning"/>.
    /// Always snapshots <c>_statusGen</c>; aborts the write if gen advanced (newer sync owns status).
    /// When <paramref name="abortIfRunning"/>, also aborts if another run started.</summary>
    private async Task ApplyRestingStatusAsync(CancellationToken ct = default, bool abortIfRunning = false)
    {
        var gen = Volatile.Read(ref _statusGen);
        var counts = await _store.SyncOperations.GetPendingCountsAsync(ct).ConfigureAwait(false);
        if (abortIfRunning && Volatile.Read(ref _running) != 0)
            return;
        if (gen != Volatile.Read(ref _statusGen))
            return;
        SetStatus(SyncStatusText.Format(_lastSyncAt?.ToLocalTime(), counts), requiredGen: gen);
    }

    /// <summary>Set a non-resting head (error/conflict/failure) and append pending counts if any.
    /// Same gen write guard as <see cref="ApplyRestingStatusAsync"/> / <see cref="RefreshPendingStatusAsync"/>.</summary>
    private async Task SetStatusWithPendingAsync(string head, CancellationToken ct = default, bool abortIfRunning = false)
    {
        var gen = Volatile.Read(ref _statusGen);
        var counts = await _store.SyncOperations.GetPendingCountsAsync(ct).ConfigureAwait(false);
        if (abortIfRunning && Volatile.Read(ref _running) != 0)
            return;
        if (gen != Volatile.Read(ref _statusGen))
            return;
        SetStatus(SyncStatusText.WithPendingSuffix(head, counts), requiredGen: gen);
    }

    public void Start()
    {
        _timer?.Dispose();
        _timer = new Timer(_ => _ = RunAsync(), null, TimeSpan.FromSeconds(3), _interval);
    }

    public void ReapplyInterval(AppSettings settings)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, settings.SyncIntervalMinutes));
        if (interval != _interval)
        {
            _interval = interval;
            _timer?.Change(TimeSpan.FromSeconds(3), interval);
        }
    }

    /// <summary>Set <see cref="Status"/> on the UI thread so WPF binding never sees a
    /// cross-thread PropertyChanged. When <paramref name="requiredGen"/> is set, the assign
    /// is skipped if <c>_statusGen</c> advanced after the caller snapshot — closing the race
    /// where guards ran before <see cref="SynchronizationContext.Post"/> but a stale callback
    /// still applied later (e.g. resting refresh clobbering <c>同步中…</c>).</summary>
    private void SetStatus(string value, int? requiredGen = null)
    {
        void apply()
        {
            if (requiredGen is int g && g != Volatile.Read(ref _statusGen))
                return;
            Status = value;
        }

        if (_uiContext is not null)
            _uiContext.Post(_ => apply(), null);
        else
            apply();
    }

    public async Task RunAsync()
    {
        StartupLog.Log("SyncRunner.RunAsync: enter");
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            StartupLog.Log("SyncRunner.RunAsync: already running, skip");
            return;
        }

        // null head => resting format after release; non-null => error/conflict head + pending.
        string? releaseStatusHead = null;
        var reapplyPendingAfterRelease = false;

        try
        {
            // Invalidate in-flight RefreshPendingStatusAsync snapshots; we own Status now.
            var runGen = Interlocked.Increment(ref _statusGen);

            var accounts = await _store.Accounts.GetAllAsync();
            StartupLog.Log($"SyncRunner.RunAsync: {accounts.Count} account(s)");
            if (accounts.Count == 0)
            {
                // Write after finally gen-bump via reapply — a Post with runGen would be
                // aborted by the release increment and leave Status stuck.
                releaseStatusHead = "未配置账户";
                reapplyPendingAfterRelease = true;
                StartupLog.Log("SyncRunner.RunAsync: no accounts, idle");
                return;
            }

            // Pass runGen so this Post always wins over stale resting/pending Posts from before the bump.
            SetStatus("同步中…", requiredGen: runGen);
            var account = accounts[0];

            StartupLog.Log("SyncRunner.RunAsync: creating CalDAV client");
            await using var client = await _factory.CreateAsync(account);
            StartupLog.Log("SyncRunner.RunAsync: client created, syncing");

            var engine = new SyncEngine(_store.Events, _store.Calendars, _store.SyncState, _store.SyncOperations, client);
            LastReport = await engine.SyncAsync();
            StartupLog.Log($"SyncRunner.RunAsync: done  pulled={LastReport.Pulled} pushed={LastReport.Pushed} errors={LastReport.Errors.Count}");

            if (LastReport.HasConflicts)
            {
                releaseStatusHead = $"有 {LastReport.ConflictEventIds.Count} 个冲突待处理";
                await SetStatusWithPendingAsync(releaseStatusHead);
                reapplyPendingAfterRelease = true;
            }
            else if (LastReport.Errors.Count > 0)
            {
                releaseStatusHead = "同步出错（已记录）";
                await SetStatusWithPendingAsync(releaseStatusHead);
                reapplyPendingAfterRelease = true;
            }
            else
            {
                _lastSyncAt = DateTimeOffset.UtcNow;
                // Still inside _running=1; gen-guarded apply so resting status is written before release.
                await ApplyRestingStatusAsync();
                releaseStatusHead = null;
                reapplyPendingAfterRelease = true;
            }

            SyncCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StartupLog.Log(ex, "SyncRunner.RunAsync: failed");
            releaseStatusHead = "同步失败：" + ex.Message;
            reapplyPendingAfterRelease = true;
            try
            {
                await SetStatusWithPendingAsync(releaseStatusHead);
            }
            catch
            {
                // Still mid-run; pass current gen so a later finally bump aborts a stale Post.
                SetStatus(releaseStatusHead, requiredGen: Volatile.Read(ref _statusGen));
            }
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            // Invalidate refresh + any prior exit-reapply snapshots that spanned this run; then
            // re-read outbox so mutations that arrived while we still held _running (and thus
            // skipped Refresh) are visible. A later sync's gen bumps abort this reapply if slow.
            Interlocked.Increment(ref _statusGen);

            if (reapplyPendingAfterRelease)
            {
                try
                {
                    if (releaseStatusHead is { } head)
                        await SetStatusWithPendingAsync(head, abortIfRunning: true).ConfigureAwait(false);
                    else
                        await ApplyRestingStatusAsync(abortIfRunning: true).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort status only; sync outcome already recorded.
                }
            }
        }
    }

    /// <summary>Wipe all local events and sync state (keeps account configuration and calendar
    /// colors), then trigger a full re-pull. This recovers from one-time conversion bugs such
    /// as historical TZID values that were stored before the conversion fix.</summary>
    public async Task FullResyncAsync(CancellationToken ct = default)
    {
        await _store.Events.HardDeleteAllAsync(ct).ConfigureAwait(false);
        await _store.Calendars.ClearAllSyncTokensAsync(ct).ConfigureAwait(false);
        await _store.SyncState.ClearAllAsync(ct).ConfigureAwait(false);
        await _store.SyncOperations.ClearAllAsync(ct).ConfigureAwait(false);
        await RunAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
