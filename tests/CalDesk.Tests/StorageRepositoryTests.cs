using System.Text;
using CalDesk.Core.Models;
using CalDesk.Core.Services;
using CalDesk.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CalDesk.Tests;

/// <summary>Tests for the SQLite-backed repositories in CalDesk.Storage: schema (WAL, foreign
/// keys, migrations, indexes, uniqueness), repository round-trips, and the EventService
/// transactionality + outbox-coalescing behaviors. Uses a temp file so that each repository
/// call (which opens its own connection) operates on the same persistent in-process database.</summary>
public sealed class StorageRepositoryTests
{
    // ---- helpers -------------------------------------------------------------

    private static async Task<(CalDeskDatabase db, StorageServices svc, string path)> SetupAsync()
    {
        var path = Path.GetTempFileName();
        File.Delete(path);
        var db = new CalDeskDatabase(path);
        await db.InitializeAsync();
        return (db, new StorageServices(db), path);
    }

    private static async Task<(CalDeskDatabase db, StorageServices svc, string path, Guid accountId, Guid calendarId)> SetupWithCalendarAsync()
    {
        var (db, svc, path) = await SetupAsync();
        var accountId = Guid.NewGuid();
        await svc.Accounts.UpsertAsync(new Account
        {
            Id = accountId,
            ServerUrl = "https://example.caldav/",
            Username = "mar",
            CredentialTarget = $"CalDesk:account:{accountId}",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var calendarId = Guid.NewGuid();
        await svc.Calendars.UpsertAsync(new CalendarInfo
        {
            Id = calendarId,
            AccountId = accountId,
            RemoteUrl = $"https://example.caldav/user/{calendarId:N}/",
            DisplayName = "Work",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        return (db, svc, path, accountId, calendarId);
    }

    private static void CleanupDb(string path)
    {
        // Windows: the connection pool can hold the temp .db open; clear pools before delete
        // and retry a few times (file-in-use during teardown otherwise fails the run).
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
        foreach (var p in new[] { path, path + "-wal", path + "-shm" })
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try { if (File.Exists(p)) File.Delete(p); break; }
                catch (IOException) { System.Threading.Thread.Sleep(50); }
            }
        }
    }

    private static CalendarEvent MakeEvent(Guid calendarId, string? href = null) => new()
    {
        Id = Guid.NewGuid(),
        CalendarId = calendarId,
        Uid = Guid.NewGuid().ToString("N"),
        RemoteHref = href,
        Summary = "Standup",
        StartUtc = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero),
        EndUtc = new DateTimeOffset(2026, 7, 15, 11, 0, 0, TimeSpan.Zero),
        ICalendarData = "",
    };

    private static async Task<int> GetUserVersionAsync(CalDeskDatabase db)
    {
        await using var conn = db.CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task SetUserVersionAsync(CalDeskDatabase db, int version)
    {
        await using var conn = db.CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {version};";
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Fake outbox that always fails on Enqueue, to prove EventService rolls back the
    /// event write when the outbox write throws.</summary>
    private sealed class ThrowingSyncOperations : ISyncOperationRepository
    {
        public Task EnqueueAsync(SyncOperation op, CancellationToken ct = default)
            => throw new InvalidOperationException("simulated enqueue failure");
        public Task<IReadOnlyList<SyncOperation>> GetDueAsync(int limit, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task MarkAttemptedAsync(long id, string? error, DateTimeOffset? nextAttemptAt, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task RemoveAsync(long id, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<PendingSyncCounts> GetPendingCountsAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    // ---- 0. original end-to-end round trip -----------------------------------

    [Fact]
    public async Task Calendar_And_Event_Round_Trip_Through_Repositories()
    {
        var dbPath = Path.GetTempFileName();
        try
        {
            File.Delete(dbPath);

            var database = new CalDeskDatabase(dbPath);
            await database.InitializeAsync();
            var services = new StorageServices(database);

            var accountId = Guid.NewGuid();
            await services.Accounts.UpsertAsync(new Account
            {
                Id = accountId,
                ServerUrl = "https://example.caldav/",
                Username = "mar",
                CredentialTarget = $"CalDesk:account:{accountId}",
                DisplayName = "Personal",
                CreatedAt = DateTimeOffset.UtcNow,
            });

            var calendarId = Guid.NewGuid();
            var calendar = new CalendarInfo
            {
                Id = calendarId,
                AccountId = accountId,
                RemoteUrl = "https://example.caldav/user/work/",
                DisplayName = "Work",
                Color = "#aabbcc",
                SortOrder = 1,
                SupportsSyncToken = true,
                IsSelected = true,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await services.Calendars.UpsertAsync(calendar);

            var readCal = await services.Calendars.GetAsync(calendarId);
            Assert.NotNull(readCal);
            Assert.Equal(calendar.RemoteUrl, readCal!.RemoteUrl);
            Assert.Equal("Work", readCal.DisplayName);

            var rangeStart = new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);
            var rangeEnd = new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero);

            var evtStart = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);
            var evtEnd = new DateTimeOffset(2026, 7, 15, 11, 0, 0, TimeSpan.Zero);

            var evt = new CalendarEvent
            {
                Id = Guid.NewGuid(),
                CalendarId = calendarId,
                Uid = "evt-1@example",
                RemoteHref = "/user/work/evt-1.ics",
                ETag = "\"v1\"",
                ICalendarData = "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n",
                Summary = "Standup",
                Description = "Daily team sync",
                Location = "Room 42",
                IsAllDay = false,
                StartUtc = evtStart,
                EndUtc = evtEnd,
                TimeZoneId = "Asia/Shanghai",
                IsFloating = false,
                RecurrenceRule = null,
                SyncStatus = SyncStatus.NewLocal,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await services.Events.UpsertAsync(evt);

            var recurringStart = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
            var recurringEnd = new DateTimeOffset(2026, 1, 1, 9, 30, 0, TimeSpan.Zero);
            var recurring = new CalendarEvent
            {
                Id = Guid.NewGuid(),
                CalendarId = calendarId,
                Uid = "evt-recur@example",
                RemoteHref = "/user/work/evt-recur.ics",
                ETag = null,
                ICalendarData = "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n",
                Summary = "Weekly review",
                Description = null,
                Location = null,
                IsAllDay = true,
                StartUtc = recurringStart,
                EndUtc = recurringEnd,
                TimeZoneId = null,
                IsFloating = true,
                RecurrenceRule = "FREQ=WEEKLY;BYDAY=MO",
                SyncStatus = SyncStatus.Clean,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await services.Events.UpsertAsync(recurring);

            var fetched = await services.Events.GetAsync(evt.Id);
            Assert.NotNull(fetched);
            Assert.Equal(evt.Id, fetched!.Id);
            Assert.Equal("Standup", fetched.Summary);
            Assert.Equal(SyncStatus.NewLocal, fetched.SyncStatus);
            Assert.False(fetched.IsAllDay);
            Assert.False(fetched.IsFloating);
            Assert.Equal("Daily team sync", fetched.Description);
            Assert.Equal(evtStart, fetched.StartUtc);
            Assert.Equal(evtEnd, fetched.EndUtc);
            Assert.Equal("Room 42", fetched.Location);
            Assert.Equal("\"v1\"", fetched.ETag);
            Assert.Equal("/user/work/evt-1.ics", fetched.RemoteHref);

            var inRange = await services.Events.GetInRangeAsync(new[] { calendarId }, rangeStart, rangeEnd);
            Assert.Equal(2, inRange.Count);
            Assert.Equal(recurring.Id, inRange[0].Id);
            Assert.Equal(evt.Id, inRange[1].Id);

            var fetchedRecur = inRange[0];
            Assert.Null(fetchedRecur.Description);
            Assert.True(fetchedRecur.IsAllDay);
            Assert.True(fetchedRecur.IsFloating);
            Assert.Equal("FREQ=WEEKLY;BYDAY=MO", fetchedRecur.RecurrenceRule);
            Assert.True(fetchedRecur.IsRecurring);

            var empty = await services.Events.GetInRangeAsync(Array.Empty<Guid>(), rangeStart, rangeEnd);
            Assert.Empty(empty);

            var byUid = await services.Events.FindByUidAsync(calendarId, "evt-1@example");
            Assert.NotNull(byUid);
            Assert.Equal(evt.Id, byUid!.Id);

            var byHref = await services.Events.FindByRemoteHrefAsync("/user/work/evt-recur.ics");
            Assert.NotNull(byHref);
            Assert.Equal(recurring.Id, byHref!.Id);

            await services.SyncState.UpsertSyncTokenAsync(calendarId, "token-1");
            Assert.Equal("token-1", await services.SyncState.GetSyncTokenAsync(calendarId));
            Assert.Null(await services.SyncState.GetCtagAsync(calendarId));

            await services.SyncState.SaveCtagAsync(calendarId, "ctag-1");
            Assert.Equal("ctag-1", await services.SyncState.GetCtagAsync(calendarId));
            Assert.Equal("token-1", await services.SyncState.GetSyncTokenAsync(calendarId));

            await services.SyncState.UpsertSyncTokenAsync(calendarId, "token-2");
            Assert.Equal("token-2", await services.SyncState.GetSyncTokenAsync(calendarId));
            Assert.Equal("ctag-1", await services.SyncState.GetCtagAsync(calendarId));

            var op = new SyncOperation
            {
                EventId = evt.Id,
                Kind = SyncOperationKind.Create,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            await services.SyncOperations.EnqueueAsync(op);
            Assert.True(op.Id > 0);

            var due = await services.SyncOperations.GetDueAsync(limit: 10);
            Assert.Single(due);
            Assert.Equal(op.Id, due[0].Id);
            Assert.Equal(SyncOperationKind.Create, due[0].Kind);
            Assert.Equal(evt.Id, due[0].EventId);

            var backoff = DateTimeOffset.UtcNow.AddHours(1);
            await services.SyncOperations.MarkAttemptedAsync(op.Id, "boom", backoff);
            var dueAfterBackoff = await services.SyncOperations.GetDueAsync(limit: 10);
            Assert.Empty(dueAfterBackoff);

            await services.Events.MarkDeletedAsync(evt.Id);
            var afterDelete = await services.Events.GetInRangeAsync(new[] { calendarId }, rangeStart, rangeEnd);
            Assert.Single(afterDelete);
            Assert.Equal(recurring.Id, afterDelete[0].Id);

            await services.Events.HardDeleteAsync(recurring.Id);
            var afterHardDelete = await services.Events.GetInRangeAsync(new[] { calendarId }, rangeStart, rangeEnd);
            Assert.Empty(afterHardDelete);
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    // ---- 1. WAL ---------------------------------------------------------------

    [Fact]
    public async Task Initialize_SetsJournalModeToWal()
    {
        var (db, _, path) = await SetupAsync();
        try
        {
            await using var conn = db.CreateConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode;";
            var mode = (string)(await cmd.ExecuteScalarAsync() ?? string.Empty);

            Assert.Equal("wal", mode.ToLowerInvariant());
        }
        finally
        {
            CleanupDb(path);
        }
    }

    // ---- 2. foreign keys + cascade --------------------------------------------

    [Fact]
    public async Task DeletingCalendar_CascadesToItsEvents()
    {
        var (_, svc, path, _, calendarId) = await SetupWithCalendarAsync();
        try
        {
            var evt = MakeEvent(calendarId, href: "/work/a.ics");
            await svc.Events.UpsertAsync(evt);
            Assert.NotNull(await svc.Events.GetAsync(evt.Id));

            await svc.Calendars.DeleteAsync(calendarId);

            // FK ON DELETE CASCADE removes the event rows belonging to the calendar.
            Assert.Null(await svc.Events.GetAsync(evt.Id));
        }
        finally
        {
            CleanupDb(path);
        }
    }

    [Fact]
    public async Task DeletingEvent_CascadesToItsOutboxOps()
    {
        var (_, svc, path, _, calendarId) = await SetupWithCalendarAsync();
        try
        {
            var evt = MakeEvent(calendarId, href: "/work/b.ics");
            await svc.Events.UpsertAsync(evt);
            await svc.SyncOperations.EnqueueAsync(new SyncOperation { EventId = evt.Id, Kind = SyncOperationKind.Create });
            Assert.Single(await svc.SyncOperations.GetDueAsync(100));

            await svc.Events.HardDeleteAsync(evt.Id);

            Assert.Empty(await svc.SyncOperations.GetDueAsync(100));
        }
        finally
        {
            CleanupDb(path);
        }
    }

    // ---- 3. versioned migrations ---------------------------------------------

    [Fact]
    public async Task Migrations_AdvanceUserVersion_AndAreIdempotent()
    {
        var path = Path.GetTempFileName();
        File.Delete(path);
        try
        {
            var db = new CalDeskDatabase(path);
            await db.InitializeAsync();
            Assert.Equal(1, await GetUserVersionAsync(db));

            // Idempotent: re-running init leaves the version unchanged and does not error.
            await db.InitializeAsync();
            Assert.Equal(1, await GetUserVersionAsync(db));

            // Simulate an older DB: reset the version to 0 and re-run; it migrates forward.
            await SetUserVersionAsync(db, 0);
            await db.InitializeAsync();
            Assert.Equal(1, await GetUserVersionAsync(db));

            // And forward-migrated schema is usable (round-trip an account).
            var svc = new StorageServices(db);
            var id = Guid.NewGuid();
            await svc.Accounts.UpsertAsync(new Account { Id = id, ServerUrl = "u", Username = "n", CredentialTarget = "c" });
            Assert.Equal("n", (await svc.Accounts.GetAsync(id))!.Username);
        }
        finally
        {
            CleanupDb(path);
        }
    }

    // ---- 4. composite indexes -------------------------------------------------

    [Fact]
    public async Task RangeQuery_UsesAnIndex()
    {
        var (_, svc, path, _, calendarId) = await SetupWithCalendarAsync();
        try
        {
            for (var i = 0; i < 5; i++)
            {
                var e = MakeEvent(calendarId, href: $"/work/r{i}.ics");
                e.StartUtc = new DateTimeOffset(2026, 7, 15, 8 + i, 0, 0, TimeSpan.Zero);
                e.EndUtc = e.StartUtc.AddHours(1);
                await svc.Events.UpsertAsync(e);
            }

            await using var conn = svc.Database.CreateConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                EXPLAIN QUERY PLAN
                SELECT id FROM events
                WHERE deleted_at IS NULL AND sync_status != 'DeletedLocal'
                  AND calendar_id IN (@id0)
                  AND ( (recurrence_rule IS NULL AND end_utc > @start AND start_utc < @end)
                        OR (recurrence_rule IS NOT NULL AND start_utc < @end) )
                ORDER BY start_utc
                """;
            cmd.Parameters.AddWithValue("@id0", calendarId.ToString());
            cmd.Parameters.AddWithValue("@start", new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero).ToString("O"));
            cmd.Parameters.AddWithValue("@end", new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero).ToString("O"));

            var plan = new StringBuilder();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                plan.AppendLine(reader.GetString(3));

            Assert.Contains("USING INDEX", plan.ToString());
        }
        finally
        {
            CleanupDb(path);
        }
    }

    // ---- 5. uniqueness --------------------------------------------------------

    [Fact]
    public async Task DuplicateCalendarAndRemoteHref_IsRejected()
    {
        var (_, svc, path, _, calendarId) = await SetupWithCalendarAsync();
        try
        {
            var first = MakeEvent(calendarId, href: "/work/dup.ics");
            await svc.Events.UpsertAsync(first);

            var second = MakeEvent(calendarId, href: "/work/dup.ics"); // same (calendar_id, remote_href), new id
            await Assert.ThrowsAsync<SqliteException>(() => svc.Events.UpsertAsync(second));
        }
        finally
        {
            CleanupDb(path);
        }
    }

    [Fact]
    public async Task NullRemoteHref_DoesNotConflict()
    {
        // Events that have never been uploaded (null href) must never clash under the partial index.
        var (_, svc, path, _, calendarId) = await SetupWithCalendarAsync();
        try
        {
            var a = MakeEvent(calendarId, href: null);
            var b = MakeEvent(calendarId, href: null);
            await svc.Events.UpsertAsync(a);
            await svc.Events.UpsertAsync(b);
            Assert.NotNull(await svc.Events.GetAsync(a.Id));
            Assert.NotNull(await svc.Events.GetAsync(b.Id));
        }
        finally
        {
            CleanupDb(path);
        }
    }

    // ---- 5b. pending outbox counts -------------------------------------------

    [Fact]
    public async Task GetPendingCounts_GroupsByKind()
    {
        // Distinct event ids so Enqueue does not coalesce rows of the same kind.
        var (_, svc, path, _, calendarId) = await SetupWithCalendarAsync();
        try
        {
            async Task EnqueueKind(SyncOperationKind kind)
            {
                var evt = MakeEvent(calendarId, href: kind == SyncOperationKind.Create ? null : $"/work/{Guid.NewGuid():N}.ics");
                evt.SyncStatus = SyncStatus.Clean;
                await svc.Events.UpsertAsync(evt);
                await svc.SyncOperations.EnqueueAsync(new SyncOperation
                {
                    EventId = evt.Id,
                    Kind = kind,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }

            await EnqueueKind(SyncOperationKind.Create);
            await EnqueueKind(SyncOperationKind.Create);
            await EnqueueKind(SyncOperationKind.Update);
            await EnqueueKind(SyncOperationKind.Update);
            await EnqueueKind(SyncOperationKind.Update);
            await EnqueueKind(SyncOperationKind.Delete);

            var counts = await svc.SyncOperations.GetPendingCountsAsync();
            Assert.Equal(new PendingSyncCounts(Create: 2, Update: 3, Delete: 1), counts);
        }
        finally
        {
            CleanupDb(path);
        }
    }

    // ---- 6. atomic event + outbox write --------------------------------------

    [Fact]
    public async Task Create_WhenEnqueueFails_RollsBackEvent()
    {
        var (_, svc, path, _, calendarId) = await SetupWithCalendarAsync();
        try
        {
            var eventSvc = new EventService(svc.Events, new ThrowingSyncOperations(), svc);
            var evt = MakeEvent(calendarId, href: null);

            await Assert.ThrowsAsync<InvalidOperationException>(() => eventSvc.CreateAsync(evt));

            // The event write must have been rolled back with the failed enqueue.
            Assert.Null(await svc.Events.GetAsync(evt.Id));
        }
        finally
        {
            CleanupDb(path);
        }
    }

    // ---- 7. outbox coalescing -------------------------------------------------

    private async Task<List<SyncOperation>> OpsForAsync(StorageServices svc, Guid eventId)
    {
        var due = await svc.SyncOperations.GetDueAsync(100);
        return due.Where(o => o.EventId == eventId).ToList();
    }

    [Fact]
    public async Task Coalesce_CreateThenUpdate_KeepsSingleCreateOp()
    {
        var (_, svc, path, _, calendarId) = await SetupWithCalendarAsync();
        try
        {
            var eventSvc = new EventService(svc.Events, svc.SyncOperations, svc);
            var evt = MakeEvent(calendarId, href: null);
            await eventSvc.CreateAsync(evt);
            await eventSvc.UpdateAsync(evt);

            // Event stays NewLocal (never uploaded) and only one Create op exists.
            var fetched = await svc.Events.GetAsync(evt.Id);
            Assert.NotNull(fetched);
            Assert.Equal(SyncStatus.NewLocal, fetched!.SyncStatus);

            var ops = await OpsForAsync(svc, evt.Id);
            Assert.Single(ops);
            Assert.Equal(SyncOperationKind.Create, ops[0].Kind);
        }
        finally
        {
            CleanupDb(path);
        }
    }

    [Fact]
    public async Task Coalesce_CreateThenDelete_RemovesEventAndDropsOp()
    {
        var (_, svc, path, _, calendarId) = await SetupWithCalendarAsync();
        try
        {
            var eventSvc = new EventService(svc.Events, svc.SyncOperations, svc);
            var evt = MakeEvent(calendarId, href: null);
            await eventSvc.CreateAsync(evt);
            await eventSvc.DeleteAsync(evt.Id);

            // Never uploaded → hard-deleted, and no pending op remains.
            Assert.Null(await svc.Events.GetAsync(evt.Id));
            Assert.Empty(await OpsForAsync(svc, evt.Id));
        }
        finally
        {
            CleanupDb(path);
        }
    }

    [Fact]
    public async Task Coalesce_UpdateThenUpdate_KeepsSingleUpdateOp()
    {
        var (_, svc, path, _, calendarId) = await SetupWithCalendarAsync();
        try
        {
            var eventSvc = new EventService(svc.Events, svc.SyncOperations, svc);
            // Start from a synced (Clean) event with a server href.
            var evt = MakeEvent(calendarId, href: "/work/u.ics");
            evt.SyncStatus = SyncStatus.Clean;
            await svc.Events.UpsertAsync(evt);

            await eventSvc.UpdateAsync(evt);
            evt.Summary = "edited once";
            await eventSvc.UpdateAsync(evt);

            Assert.Equal(SyncStatus.ModifiedLocal, (await svc.Events.GetAsync(evt.Id))!.SyncStatus);
            var ops = await OpsForAsync(svc, evt.Id);
            Assert.Single(ops);
            Assert.Equal(SyncOperationKind.Update, ops[0].Kind);
        }
        finally
        {
            CleanupDb(path);
        }
    }

    [Fact]
    public async Task Coalesce_UpdateThenDelete_BecomesSingleDeleteOp()
    {
        var (_, svc, path, _, calendarId) = await SetupWithCalendarAsync();
        try
        {
            var eventSvc = new EventService(svc.Events, svc.SyncOperations, svc);
            var evt = MakeEvent(calendarId, href: "/work/v.ics");
            evt.SyncStatus = SyncStatus.Clean;
            await svc.Events.UpsertAsync(evt);

            await eventSvc.UpdateAsync(evt);
            await eventSvc.DeleteAsync(evt.Id);

            Assert.Equal(SyncStatus.DeletedLocal, (await svc.Events.GetAsync(evt.Id))!.SyncStatus);
            var ops = await OpsForAsync(svc, evt.Id);
            Assert.Single(ops);
            Assert.Equal(SyncOperationKind.Delete, ops[0].Kind);
        }
        finally
        {
            CleanupDb(path);
        }
    }
}
