using System.IO;
using CalDesk.Core.Models;
using CalDesk.Storage;
using Xunit;

namespace CalDesk.Tests;

/// <summary>Hygiene #9: <c>EventRepository.GetPendingSyncAsync</c> is the pending-sync queue
/// and must include soft-deleted <c>DeletedLocal</c> rows — they are local deletions still
/// awaiting their server-side DELETE. The old query also filtered <c>deleted_at IS NULL</c>,
/// which contradicted the IN-list: MarkDeletedAsync stamps deleted_at AND sets
/// DeletedLocal, so those rows could never be returned.</summary>
public sealed class EventRepositoryPendingSyncTests
{
    [Fact]
    public async Task SoftDeleted_DeletedLocal_Row_Is_Returned_By_PendingSync()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"caldesk-pending-{Guid.NewGuid():N}.db");
        try
        {
            var db = new CalDeskDatabase(tempDb);
            await db.InitializeAsync();
            var store = new StorageServices(db);

            // calendars.account_id has an FK to accounts — seed the account row first.
            var account = new Account { ServerUrl = "https://srv/", Username = "u", CredentialTarget = "t" };
            await store.Accounts.UpsertAsync(account);
            var cal = new CalendarInfo
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                DisplayName = "cal",
                RemoteUrl = "https://srv/cal/",
            };
            await store.Calendars.UpsertAsync(cal);

            var evt = new CalendarEvent
            {
                Id = Guid.NewGuid(),
                CalendarId = cal.Id,
                Uid = "pending-1",
                Summary = "P",
                StartUtc = DateTimeOffset.UtcNow,
                EndUtc = DateTimeOffset.UtcNow.AddHours(1),
                SyncStatus = SyncStatus.Clean,
                RemoteHref = "/cal/pending-1.ics",
                ETag = "\"e1\"",
            };
            await store.Events.UpsertAsync(evt);

            // Soft delete: stamps deleted_at AND sets DeletedLocal (the row under test).
            await store.Events.MarkDeletedAsync(evt.Id);

            var pending = await store.Events.GetPendingSyncAsync();

            // The deletion is queued for push — before the fix this row was excluded by the
            // contradictory deleted_at IS NULL filter and the delete never reached the server.
            var row = Assert.Single(pending);
            Assert.Equal(evt.Id, row.Id);
            Assert.Equal(SyncStatus.DeletedLocal, row.SyncStatus);
        }
        finally
        {
            // Best effort: Sqlite connection pooling keeps the handle open on Windows,
            // where deleting an open file throws (Linux allows it). GUID-named temp
            // files are harmless if they leak. Same convention as SyncEnginePushConcurrencyTests.
            try { File.Delete(tempDb); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Clean_Rows_Are_Not_Pending()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"caldesk-pending-{Guid.NewGuid():N}.db");
        try
        {
            var db = new CalDeskDatabase(tempDb);
            await db.InitializeAsync();
            var store = new StorageServices(db);

            var account = new Account { ServerUrl = "https://srv/", Username = "u", CredentialTarget = "t" };
            await store.Accounts.UpsertAsync(account);
            var cal = new CalendarInfo
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                DisplayName = "cal",
                RemoteUrl = "https://srv/cal/",
            };
            await store.Calendars.UpsertAsync(cal);

            await store.Events.UpsertAsync(new CalendarEvent
            {
                Id = Guid.NewGuid(),
                CalendarId = cal.Id,
                Uid = "clean-1",
                Summary = "C",
                StartUtc = DateTimeOffset.UtcNow,
                EndUtc = DateTimeOffset.UtcNow.AddHours(1),
                SyncStatus = SyncStatus.Clean,
            });

            var pending = await store.Events.GetPendingSyncAsync();
            Assert.Empty(pending);
        }
        finally
        {
            // Best effort — see SoftDeleted test note on Windows SQLite handle retention.
            try { File.Delete(tempDb); } catch { /* best effort */ }
        }
    }
}
