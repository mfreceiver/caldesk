using System.IO;
using CalDesk.CalDav;
using CalDesk.CalDav.Synchronization;
using CalDesk.Core.Models;
using CalDesk.Core.Services;
using CalDesk.Storage;

namespace CalDesk.Tests;

/// <summary>CQ-003 regression: a local edit (or delete) that lands while <c>SyncEngine</c> is
/// inside the PUT round-trip must survive the push write-back. Uses the REAL SQLite stack
/// (temp DB, real repositories — including the outbox op-row reuse — and the real
/// <see cref="EventService"/>) because the in-memory fakes store object references and cannot
/// reproduce the snapshot-clobber; only the server is faked. The fake client's PUT callback
/// fires user mutations mid-PUT, exactly inside the CQ-003 window.</summary>
public sealed class SyncEnginePushConcurrencyTests
{
    private static CalendarInfo Cal() => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = "cal",
        RemoteUrl = "https://srv/cal/",
        SupportsSyncToken = true,
    };

    private static async Task<(Account Account, CalendarInfo Calendar)> SetupAsync(StorageServices store)
    {
        // calendars.account_id has an FK to accounts — seed the account row first.
        var account = new Account { ServerUrl = "https://srv/", Username = "u", CredentialTarget = "t" };
        await store.Accounts.UpsertAsync(account);
        var cal = Cal();
        cal.AccountId = account.Id;
        await store.Calendars.UpsertAsync(cal);
        return (account, cal);
    }

    private static async Task<CalendarEvent> CreateEventAsync(EventService events, Guid calId, string uid, string summary)
    {
        return await events.CreateAsync(new CalendarEvent
        {
            CalendarId = calId,
            Uid = uid,
            Summary = summary,
            StartUtc = new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero),
            EndUtc = new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero),
            ICalendarData = $"BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:{uid}\r\nSUMMARY:{summary}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n",
        });
    }

    // ---- CQ-003 main scenario: edit during PUT survives and is re-pushed -------------

    [Fact]
    public async Task EditDuringPutRoundTrip_Survives_And_IsRepublishedOnNextRun()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"caldesk-cq003-{Guid.NewGuid():N}.db");
        try
        {
            var db = new CalDeskDatabase(tempDb);
            await db.InitializeAsync();
            var store = new StorageServices(db);
            var (_, cal) = await SetupAsync(store);
            var events = new EventService(store.Events, store.SyncOperations, store);

            var created = await CreateEventAsync(events, cal.Id, "push-edit-1", "v1");
            var id = created.Id;

            var client = new FakePushClient();
            client.OnPut = async e =>
            {
                // User edits WHILE the PUT is in flight. Edit an INDEPENDENT copy re-read from
                // storage (as real callers do) — mutating the engine's snapshot object would
                // hide the very clobber this test exists to catch.
                var fresh = await store.Events.GetAsync(e.Id);
                Assert.NotNull(fresh);
                fresh.Summary = "v2";
                await events.UpdateAsync(fresh);
            };

            var engine = new SyncEngine(store.Events, store.Calendars, store.SyncState, store.SyncOperations, client);
            await engine.SyncAsync();

            // (a) The concurrent edit survived the push write-back.
            var after = await store.Events.GetAsync(id);
            Assert.NotNull(after);
            Assert.Equal("v2", after.Summary); // pre-fix: the pre-PUT snapshot "v1" clobbered it
            // (b) The outbox op survived too (its row was REUSED by the edit)…
            var due = await store.SyncOperations.GetDueAsync(10);
            Assert.Contains(due, o => o.EventId == id); // pre-fix: RemoveAsync dropped it
            // …and the row is still dirty, not Clean.
            Assert.NotEqual(SyncStatus.Clean, after.SyncStatus);

            // (c) Second run pushes the new value and converges Clean with an empty outbox.
            client.OnPut = null;
            await engine.SyncAsync();
            Assert.Equal("v2", client.PutSummaries[^1]);
            var final = await store.Events.GetAsync(id);
            Assert.NotNull(final);
            Assert.Equal(SyncStatus.Clean, final.SyncStatus);
            Assert.Empty(await store.SyncOperations.GetDueAsync(10));
        }
        finally
        {
            try { File.Delete(tempDb); } catch { /* best effort */ }
        }
    }

    // ---- delete-during-PUT branches --------------------------------------------------

    [Fact]
    public async Task HardDeleteDuringPut_DoesNotResurrectRow_AndCompensatesOnServer()
    {
        // A never-uploaded event (NewLocal + Create op) is hard-deleted while its first PUT
        // is in flight; the events→outbox FK cascade already dropped the Create op.
        // Pre-fix: the write-back resurrected the row as Clean (user delete silently undone).
        // Post-fix: row stays gone AND the just-uploaded server copy is retracted via a
        // compensating DELETE — otherwise the next pull would re-import it.
        var tempDb = Path.Combine(Path.GetTempPath(), $"caldesk-cq003-{Guid.NewGuid():N}.db");
        try
        {
            var db = new CalDeskDatabase(tempDb);
            await db.InitializeAsync();
            var store = new StorageServices(db);
            var (_, cal) = await SetupAsync(store);
            var events = new EventService(store.Events, store.SyncOperations, store);

            var created = await CreateEventAsync(events, cal.Id, "push-harddel-1", "doomed");
            var id = created.Id;

            var client = new FakePushClient
            {
                OnPut = async e => await events.DeleteAsync(e.Id), // NewLocal → hard delete + FK cascade
            };
            var engine = new SyncEngine(store.Events, store.Calendars, store.SyncState, store.SyncOperations, client);
            await engine.SyncAsync();

            Assert.Null(await store.Events.GetAsync(id)); // pre-fix: resurrected as Clean
            Assert.Empty(await store.SyncOperations.GetDueAsync(10));
            Assert.Contains(client.DeletedHrefs, h => h.EndsWith("/push-harddel-1.ics", StringComparison.Ordinal));
        }
        finally
        {
            try { File.Delete(tempDb); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task SoftDeleteDuringPut_KeepsRowAndDeleteOp_ForNextRunPush()
    {
        // An already-uploaded event (Clean) is edited (ModifiedLocal + Update op), then
        // soft-deleted while the update PUT is in flight: the row becomes DeletedLocal and
        // the SAME op row is reused as Delete. The push write-back must not mark the row
        // Clean nor remove the op — the deletion has to reach the server on the next run.
        var tempDb = Path.Combine(Path.GetTempPath(), $"caldesk-cq003-{Guid.NewGuid():N}.db");
        try
        {
            var db = new CalDeskDatabase(tempDb);
            await db.InitializeAsync();
            var store = new StorageServices(db);
            var (_, cal) = await SetupAsync(store);
            var events = new EventService(store.Events, store.SyncOperations, store);

            var created = await CreateEventAsync(events, cal.Id, "push-softdel-1", "v1");
            var id = created.Id;

            var client = new FakePushClient();
            var engine = new SyncEngine(store.Events, store.Calendars, store.SyncState, store.SyncOperations, client);

            // Round 1: plain push, event converges to Clean with href/etag.
            await engine.SyncAsync();
            var clean = await store.Events.GetAsync(id);
            Assert.NotNull(clean);
            Assert.Equal(SyncStatus.Clean, clean.SyncStatus);
            Assert.NotNull(clean.RemoteHref);
            Assert.NotNull(clean.ETag);

            // Round 2: edit, then soft-delete while the update PUT is in flight.
            clean.Summary = "v2";
            await events.UpdateAsync(clean);
            client.OnPut = async e => await events.DeleteAsync(e.Id); // ModifiedLocal → soft delete, op reused as Delete
            await engine.SyncAsync();

            var after = await store.Events.GetAsync(id);
            Assert.NotNull(after); // row kept (soft delete is pending, not applied)
            Assert.Equal(SyncStatus.DeletedLocal, after.SyncStatus); // pre-fix: snapshot wrote Clean
            var due = await store.SyncOperations.GetDueAsync(10);
            Assert.Contains(due, o => o.EventId == id && o.Kind == SyncOperationKind.Delete); // pre-fix: op removed

            // Round 3: the deletion is pushed; server gets DELETE, row hard-deleted locally.
            client.OnPut = null;
            var r3 = await engine.SyncAsync();
            Assert.True(r3.Pushed >= 1);
            Assert.Contains(client.DeletedHrefs, h => h.EndsWith("/push-softdel-1.ics", StringComparison.Ordinal));
            Assert.Null(await store.Events.GetAsync(id));
            Assert.Empty(await store.SyncOperations.GetDueAsync(10));
        }
        finally
        {
            try { File.Delete(tempDb); } catch { /* best effort */ }
        }
    }

    // ========================================================================
    // Minimal CalDAV client fake: push paths only (pull returns an empty,
    // token-supported collection). OnPut runs INSIDE PutEventAsync — the CQ-003 window.
    // ========================================================================

    private sealed class FakePushClient : ICalDavClient
    {
        public Func<CalendarEvent, Task>? OnPut { get; set; }

        public List<string> PutSummaries { get; } = new();

        public List<string> DeletedHrefs { get; } = new();

        public Account Account { get; init; } = new() { ServerUrl = "https://srv/" };

        public Task<IReadOnlyList<CalendarInfo>> DiscoverCalendarsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CalendarInfo>>(Array.Empty<CalendarInfo>());

        public Task<CalendarQueryResult> QueryEventsAsync(CalendarInfo calendar, DateTimeOffset s, DateTimeOffset e, CancellationToken ct = default)
            => Task.FromResult(new CalendarQueryResult(Array.Empty<RemoteEventEntry>()));

        public Task<PutResult> PutEventAsync(CalendarInfo calendar, CalendarEvent evt, string? ifMatchEtag, CancellationToken ct = default)
        {
            PutSummaries.Add(evt.Summary);
            var cb = OnPut;
            if (cb is not null)
            {
                OnPut = null; // one-shot injection
                cb(evt).GetAwaiter().GetResult(); // no sync context under xunit; runs mid-PUT
            }

            return Task.FromResult(new PutResult($"/cal/{evt.Uid}.ics", $"\"e{PutSummaries.Count}\""));
        }

        public Task DeleteEventAsync(CalendarInfo calendar, string href, string? ifMatchEtag, CancellationToken ct = default)
        {
            DeletedHrefs.Add(href);
            return Task.CompletedTask;
        }

        public Task<RemoteEventEntry?> GetEventAsync(CalendarInfo calendar, string href, CancellationToken ct = default)
            => Task.FromResult<RemoteEventEntry?>(null); // pull paths are not exercised here

        public Task<SyncCollectionResult> SyncCollectionAsync(CalendarInfo calendar, string? syncToken, CancellationToken ct = default)
            => Task.FromResult(new SyncCollectionResult(
                Array.Empty<RemoteEventEntry>(),
                Array.Empty<string>(),
                $"#t{syncToken?.Length ?? 0}",
                Supported: true));

        public Task<CalendarInfo> RefreshCalendarAsync(CalendarInfo calendar, CancellationToken ct = default)
            => Task.FromResult(calendar);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
