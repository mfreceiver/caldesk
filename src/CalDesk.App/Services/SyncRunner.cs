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
    /// cross-thread PropertyChanged.</summary>
    private void SetStatus(string value)
    {
        if (_uiContext is not null)
        {
            _uiContext.Post(_ => Status = value, null);
        }
        else
        {
            Status = value;
        }
    }

    public async Task RunAsync()
    {
        StartupLog.Log("SyncRunner.RunAsync: enter");
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            StartupLog.Log("SyncRunner.RunAsync: already running, skip");
            return;
        }

        try
        {
            var accounts = await _store.Accounts.GetAllAsync();
            StartupLog.Log($"SyncRunner.RunAsync: {accounts.Count} account(s)");
            if (accounts.Count == 0)
            {
                SetStatus("未配置账户");
                StartupLog.Log("SyncRunner.RunAsync: no accounts, idle");
                return;
            }

            SetStatus("同步中…");
            var account = accounts[0];

            StartupLog.Log("SyncRunner.RunAsync: creating CalDAV client");
            await using var client = await _factory.CreateAsync(account);
            StartupLog.Log("SyncRunner.RunAsync: client created, syncing");

            var engine = new SyncEngine(_store.Events, _store.Calendars, _store.SyncState, _store.SyncOperations, client);
            LastReport = await engine.SyncAsync();
            StartupLog.Log($"SyncRunner.RunAsync: done  pulled={LastReport.Pulled} pushed={LastReport.Pushed} errors={LastReport.Errors.Count}");

            SetStatus(LastReport.HasConflicts
                ? $"有 {LastReport.ConflictEventIds.Count} 个冲突待处理"
                : LastReport.Errors.Count > 0
                    ? "同步出错（已记录）"
                    : $"已同步 · 拉 {LastReport.Pulled} · 推 {LastReport.Pushed}");

            SyncCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StartupLog.Log(ex, "SyncRunner.RunAsync: failed");
            SetStatus("同步失败：" + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
