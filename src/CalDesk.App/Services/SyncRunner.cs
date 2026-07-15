using System.ComponentModel;
using CalDesk.App.Models;
using CalDesk.CalDav;
using CalDesk.CalDav.Synchronization;
using CalDesk.Core.Services;
using CalDesk.Storage;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CalDesk.App.Services;

/// <summary>Drives periodic + manual sync against the configured account and exposes status.</summary>
public sealed partial class SyncRunner : ObservableObject, IDisposable
{
    private readonly StorageServices _store;
    private readonly ICalDavClientFactory _factory;
    private TimeSpan _interval;
    private Timer? _timer;
    private int _running;

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

    public async Task RunAsync()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var accounts = await _store.Accounts.GetAllAsync();
            if (accounts.Count == 0)
            {
                Status = "未配置账户";
                return;
            }

            Status = "同步中…";
            var account = accounts[0];

            await using var client = await _factory.CreateAsync(account);

            var engine = new SyncEngine(_store.Events, _store.Calendars, _store.SyncState, _store.SyncOperations, client);
            LastReport = await engine.SyncAsync();

            Status = LastReport.HasConflicts
                ? $"有 {LastReport.ConflictEventIds.Count} 个冲突待处理"
                : LastReport.Errors.Count > 0
                    ? "同步出错（已记录）"
                    : $"已同步 · 拉 {LastReport.Pulled} · 推 {LastReport.Pushed}";

            SyncCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Status = "同步失败：" + ex.Message;
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
