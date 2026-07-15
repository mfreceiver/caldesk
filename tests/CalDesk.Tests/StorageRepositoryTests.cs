using CalDesk.Core.Models;
using CalDesk.Storage;

namespace CalDesk.Tests;

/// <summary>Round-trip tests for the SQLite-backed repositories in CalDesk.Storage.
/// Uses a temp file so that each repository call (which opens its own connection)
/// operates on the same persistent in-process database.</summary>
public sealed class StorageRepositoryTests
{
    [Fact]
    public async Task Calendar_And_Event_Round_Trip_Through_Repositories()
    {
        var dbPath = Path.GetTempFileName();
        try
        {
            // The temp file already exists; SQLite is happy with that (it'll add its header).
            // But to avoid a stale header from a prior run, truncate/delete it first.
            File.Delete(dbPath);

            var database = new CalDeskDatabase(dbPath);
            await database.InitializeAsync();
            var services = new StorageServices(database);

            // --- Account + Calendar setup -------------------------------------------------
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
                RemoteUrl = "https://example.caldav/mar/work/",
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

            // --- Insert one non-recurring event within the range -------------------------
            var rangeStart = new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);
            var rangeEnd = new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero);

            var evtStart = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);
            var evtEnd = new DateTimeOffset(2026, 7, 15, 11, 0, 0, TimeSpan.Zero);

            var evt = new CalendarEvent
            {
                Id = Guid.NewGuid(),
                CalendarId = calendarId,
                Uid = "evt-1@example",
                RemoteHref = "/mar/work/evt-1.ics",
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

            // --- Insert a recurring event whose series start is BEFORE the range end ------
            // (series start outside the window — must still be pulled in for expansion)
            var recurringStart = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
            var recurringEnd = new DateTimeOffset(2026, 1, 1, 9, 30, 0, TimeSpan.Zero);
            var recurring = new CalendarEvent
            {
                Id = Guid.NewGuid(),
                CalendarId = calendarId,
                Uid = "evt-recur@example",
                RemoteHref = "/mar/work/evt-recur.ics",
                ETag = null,
                ICalendarData = "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n",
                Summary = "Weekly review",
                Description = null, // nullable string round-trip
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

            // --- Read back by id and assert round-trip of the non-recurring event --------
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
            Assert.Equal("/mar/work/evt-1.ics", fetched.RemoteHref);

            // --- Range query: both events should come back -------------------------------
            var inRange = await services.Events.GetInRangeAsync(
                new[] { calendarId }, rangeStart, rangeEnd);

            Assert.Equal(2, inRange.Count);

            // ORDER BY start_utc ascending: the recurring event's series start (2026-01-01)
            // is earlier than the Standup (2026-07-15 10:00), so it sorts first.
            Assert.Equal(recurring.Id, inRange[0].Id);
            Assert.Equal(evt.Id, inRange[1].Id);

            // Recurring event round-trips its nullable Description (was null) and flags.
            var fetchedRecur = inRange[0];
            Assert.Null(fetchedRecur.Description);
            Assert.True(fetchedRecur.IsAllDay);
            Assert.True(fetchedRecur.IsFloating);
            Assert.Equal("FREQ=WEEKLY;BYDAY=MO", fetchedRecur.RecurrenceRule);
            Assert.True(fetchedRecur.IsRecurring);

            // --- Range query with empty calendar id list returns nothing ----------------
            var empty = await services.Events.GetInRangeAsync(
                Array.Empty<Guid>(), rangeStart, rangeEnd);
            Assert.Empty(empty);

            // --- Find by UID / href -------------------------------------------------------
            var byUid = await services.Events.FindByUidAsync(calendarId, "evt-1@example");
            Assert.NotNull(byUid);
            Assert.Equal(evt.Id, byUid!.Id);

            var byHref = await services.Events.FindByRemoteHrefAsync("/mar/work/evt-recur.ics");
            Assert.NotNull(byHref);
            Assert.Equal(recurring.Id, byHref!.Id);

            // --- Sync state upsert keeps ctag independent of sync_token -----------------
            await services.SyncState.UpsertSyncTokenAsync(calendarId, "token-1");
            Assert.Equal("token-1", await services.SyncState.GetSyncTokenAsync(calendarId));
            Assert.Null(await services.SyncState.GetCtagAsync(calendarId));

            await services.SyncState.SaveCtagAsync(calendarId, "ctag-1");
            Assert.Equal("ctag-1", await services.SyncState.GetCtagAsync(calendarId));
            // sync_token must survive the ctag write
            Assert.Equal("token-1", await services.SyncState.GetSyncTokenAsync(calendarId));

            await services.SyncState.UpsertSyncTokenAsync(calendarId, "token-2");
            Assert.Equal("token-2", await services.SyncState.GetSyncTokenAsync(calendarId));
            // ctag must survive the sync_token write
            Assert.Equal("ctag-1", await services.SyncState.GetCtagAsync(calendarId));

            // --- Sync operation enqueue returns an id and is "due" immediately -----------
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

            // MarkAttemptedAsync bumps attempt count, records error and a backoff time
            // far enough in the future that the op should no longer be "due".
            var backoff = DateTimeOffset.UtcNow.AddHours(1);
            await services.SyncOperations.MarkAttemptedAsync(op.Id, "boom", backoff);
            var dueAfterBackoff = await services.SyncOperations.GetDueAsync(limit: 10);
            Assert.Empty(dueAfterBackoff);

            // --- MarkDeleted flips sync status; range query should then exclude the event
            await services.Events.MarkDeletedAsync(evt.Id);
            var afterDelete = await services.Events.GetInRangeAsync(
                new[] { calendarId }, rangeStart, rangeEnd);
            Assert.Single(afterDelete); // only the recurring event remains
            Assert.Equal(recurring.Id, afterDelete[0].Id);

            // --- HardDelete removes the row entirely -------------------------------------
            await services.Events.HardDeleteAsync(recurring.Id);
            var afterHardDelete = await services.Events.GetInRangeAsync(
                new[] { calendarId }, rangeStart, rangeEnd);
            Assert.Empty(afterHardDelete);
        }
        finally
        {
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }
}
