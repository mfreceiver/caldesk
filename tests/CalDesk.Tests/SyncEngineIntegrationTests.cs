using CalDesk.CalDav;
using CalDesk.CalDav.Synchronization;
using CalDesk.Core.Models;
using CalDesk.Core.Services;
using CalDesk.Storage;
using Xunit;

namespace CalDesk.Tests;

/// <summary>End-to-end: local-first write → push (PUT) → pull → delete (DELETE) against the
/// real server and a real SQLite DB. Self-cleaning. Gated on CALDAV_TEST_* env.</summary>
public sealed class SyncEngineIntegrationTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("CALDAV_TEST_URL");
    private static readonly string? User = Environment.GetEnvironmentVariable("CALDAV_TEST_USER");
    private static readonly string? Pass = Environment.GetEnvironmentVariable("CALDAV_TEST_PASS");
    private static bool Configured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(User) && Pass is not null;

    private sealed class EnvCredStore : ICredentialStore
    {
        private readonly string _p;
        public EnvCredStore(string p) => _p = p;
        public Task<string?> GetPasswordAsync(string target, CancellationToken ct = default) => Task.FromResult<string?>(_p);
        public Task SetPasswordAsync(string target, string password, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string target, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task LocalFirst_CreatePushPullDelete_RoundTrip()
    {
        if (!Configured) return;

        var tempDb = Path.Combine(Path.GetTempPath(), $"caldesk-sync-{Guid.NewGuid():N}.db");
        try
        {
            var db = new CalDeskDatabase(tempDb);
            await db.InitializeAsync();
            var store = new StorageServices(db);

            var account = new Account { ServerUrl = Url!, Username = User!, CredentialTarget = "t" };
            await store.Accounts.UpsertAsync(account);

            await using var client = await new CalDavClientFactory(new EnvCredStore(Pass!)).CreateAsync(account);
            var calendars = await client.DiscoverCalendarsAsync();
            var work = calendars.First(c => c.RemoteUrl.Contains("/work/", StringComparison.OrdinalIgnoreCase));
            work.AccountId = account.Id;
            await store.Calendars.UpsertAsync(work);

            var events = store.Events;
            var sync = new SyncEngine(events, store.Calendars, store.SyncState, store.SyncOperations, client);
            var eventSvc = new EventService(events, store.SyncOperations);

            // 1) initial pull -> work calendar already has events
            var r0 = await sync.SyncAsync();
            var inRange = await events.GetInRangeAsync(new[] { work.Id }, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(30));
            Assert.True(inRange.Count > 0, "initial pull should fetch existing work events");

            // 2) create locally (NewLocal) then push
            var t = DateTimeOffset.UtcNow.AddDays(1);
            var start = new DateTimeOffset(t.Year, t.Month, t.Day, 10, 0, 0, TimeSpan.Zero);
            var created = await eventSvc.CreateAsync(new CalendarEvent
            {
                CalendarId = work.Id,
                Summary = "CalDesk SelfTest (delete me)",
                StartUtc = start,
                EndUtc = start.AddHours(1),
            });

            Assert.Equal(SyncStatus.NewLocal, created.SyncStatus);
            var r1 = await sync.SyncAsync();
            Assert.True(r1.Pushed >= 1, "create should be pushed");

            var pushed = await events.GetAsync(created.Id);
            Assert.NotNull(pushed);
            Assert.Equal(SyncStatus.Clean, pushed!.SyncStatus);
            Assert.False(string.IsNullOrEmpty(pushed.RemoteHref));
            Assert.False(string.IsNullOrEmpty(pushed.ETag));

            // 3) server now holds the event (query by range around its start)
            var q = await client.QueryEventsAsync(work, start.AddHours(-1), start.AddHours(2));
            Assert.Contains(q.Entries, e => e.Href == pushed.RemoteHref);

            // 4) delete locally then push -> gone locally and remotely
            await eventSvc.DeleteAsync(created.Id);
            var r2 = await sync.SyncAsync();
            Assert.True(r2.Pushed >= 1, "delete should be pushed");

            var gone = await events.GetAsync(created.Id);
            Assert.Null(gone); // hard-deleted after successful remote DELETE

            var q2 = await client.QueryEventsAsync(work, start.AddHours(-1), start.AddHours(2));
            Assert.DoesNotContain(q2.Entries, e => e.Href == pushed.RemoteHref);
        }
        finally
        {
            if (File.Exists(tempDb)) File.Delete(tempDb);
        }
    }
}
