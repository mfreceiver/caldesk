using System.IO;
using CalDesk.App.Models;
using CalDesk.App.Services;
using CalDesk.CalDav;
using CalDesk.Core.Models;
using CalDesk.Core.Services;
using CalDesk.Storage;

namespace CalDesk.App.Tests;

/// <summary>Everything real except the server: real SQLite (temp file, WAL — same as the app),
/// real repositories, real CalDavClientFactory/CalDavClient and the real
/// <see cref="SyncRunner"/> under test. Only the far end is a <see cref="FakeCalDavServer"/>.</summary>
public sealed class AppSyncHarness : IDisposable
{
    private readonly string _dbPath;

    public StorageServices Store { get; }

    public Account Account { get; }

    public CalendarInfo Calendar { get; }

    public SyncRunner Runner { get; }

    public AppSyncHarness(FakeCalDavServer server)
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"caldesk-apptest-{Guid.NewGuid():N}.db");
        var database = new CalDeskDatabase(_dbPath);
        database.InitializeAsync().GetAwaiter().GetResult();
        Store = new StorageServices(database);

        Account = new Account
        {
            ServerUrl = server.CalendarUrl,
            Username = "test-user",
            CredentialTarget = "apptest",
        };
        Store.Accounts.UpsertAsync(Account).GetAwaiter().GetResult();

        // Upserted directly instead of going through discovery: SupportsSyncToken=true pins
        // SyncEngine to the WebDAV sync-collection (REPORT) path — the exact path involved
        // in the CQ-002 race and the cheapest pull path for the CQ-001 round-trip.
        Calendar = new CalendarInfo
        {
            AccountId = Account.Id,
            RemoteUrl = server.CalendarUrl,
            DisplayName = "Fake calendar",
            SupportsSyncToken = true,
        };
        Store.Calendars.UpsertAsync(Calendar).GetAwaiter().GetResult();

        Runner = new SyncRunner(Store, new CalDavClientFactory(new TestCredentialStore()), new AppSettings());
    }

    /// <summary>Stored (non-deleted) events of the harness calendar inside 2026 — the seeded
    /// events all have DTSTART 2026-09-01, recurring ones expand into the window too.</summary>
    public Task<IReadOnlyList<CalendarEvent>> EventsAsync()
        => Store.Events.GetInRangeAsync(
            [Calendar.Id],
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        Runner.Dispose();
        try { File.Delete(_dbPath); } catch { /* best effort cleanup */ }
    }
}

/// <summary>Constant-password credential store; the fake server ignores auth entirely.</summary>
public sealed class TestCredentialStore : ICredentialStore
{
    public Task<string?> GetPasswordAsync(string target, CancellationToken ct = default)
        => Task.FromResult<string?>("test-pass");

    public Task SetPasswordAsync(string target, string password, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task DeleteAsync(string target, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> ExistsAsync(string target, CancellationToken ct = default)
        => Task.FromResult(true);
}
