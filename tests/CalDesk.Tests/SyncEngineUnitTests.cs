using CalDesk.CalDav;
using CalDesk.CalDav.Synchronization;
using CalDesk.Core.Models;
using CalDesk.Core.Services;
using Xunit;

namespace CalDesk.Tests;

/// <summary>Unit tests for SyncEngine error handling, in-memory (no server, no SQLite).
/// Covers: invalid-sync-token full resync (Issue 2), per-calendar isolation (Issue 3),
/// and parse-failure quarantine (Issue 5).</summary>
public sealed class SyncEngineUnitTests
{
    // ---- helpers ------------------------------------------------------------

    private static CalendarInfo Cal(string name, bool supportsSync = true, string? token = null) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = name,
        RemoteUrl = $"https://srv/{name}/",
        SupportsSyncToken = supportsSync,
        SyncToken = token,
    };

    private const string OneEventIcs =
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n" +
        "BEGIN:VEVENT\r\nUID:evt-a\r\nDTSTART:20260715T100000Z\r\nDTEND:20260715T110000Z\r\nSUMMARY:A\r\nEND:VEVENT\r\n" +
        "END:VCALENDAR\r\n";

    private const string OtherEventIcs =
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n" +
        "BEGIN:VEVENT\r\nUID:evt-b\r\nDTSTART:20260716T100000Z\r\nDTEND:20260716T110000Z\r\nSUMMARY:B\r\nEND:VEVENT\r\n" +
        "END:VCALENDAR\r\n";

    // ---- Issue 2: invalid sync-token ⇒ full resync -------------------------

    [Fact]
    public async Task InvalidSyncToken_Clears_Token_And_Does_Full_Resync()
    {
        var cal = Cal("work", supportsSync: true, token: "#stale");
        var cals = new[] { cal };
        var events = new FakeEventRepo();
        var calRepo = new FakeCalendarRepo(cals);
        var state = new FakeSyncStateRepo();
        await state.UpsertSyncTokenAsync(cal.Id, "#stale");

        // SyncCollectionAsync: throws 403 on the stale token, returns two events + new token on null.
        var client = new FakeCalDavClient();
        client.SyncHandler = (c, token) =>
        {
            if (token == "#stale")
            {
                throw new CalDavException("REPORT /work/ failed: 403 valid-sync-token", 403, "valid-sync-token");
            }
            Assert.Null(token); // full re-pull must pass null
            return new SyncCollectionResult(
                new[] { new RemoteEventEntry("/work/a.ics", "\"v1\"", OneEventIcs) },
                Array.Empty<string>(),
                "#fresh",
                Supported: true);
        };

        var engine = new SyncEngine(events, calRepo, state, new FakeOpRepo(), client);
        var report = await engine.SyncAsync();

        Assert.True(report.Pulled >= 1);
        Assert.Equal("#fresh", cal.SyncToken);
        Assert.Equal("#fresh", await state.GetSyncTokenAsync(cal.Id));
        // The 403 should be surfaced in errors for visibility.
        Assert.Contains(report.Errors, e => e.Contains("valid-sync-token", StringComparison.OrdinalIgnoreCase)
                                         || e.Contains("403", StringComparison.Ordinal));
        // The re-pulled event is persisted.
        Assert.NotNull(await events.FindByRemoteHrefAsync("/work/a.ics"));
    }

    [Fact]
    public async Task Network_Error_Does_Not_Clear_Token()
    {
        var cal = Cal("work", supportsSync: true, token: "#keep");
        var cals = new[] { cal };
        var events = new FakeEventRepo();
        var calRepo = new FakeCalendarRepo(cals);
        var state = new FakeSyncStateRepo();
        await state.UpsertSyncTokenAsync(cal.Id, "#keep");

        // SyncCollectionAsync: HttpRequestException (network failure) on the stored token.
        // We do NOT reset the token for transient/network errors.
        var client = new FakeCalDavClient();
        client.SyncHandler = (c, token) => throw new HttpRequestException("connection reset");

        var engine = new SyncEngine(events, calRepo, state, new FakeOpRepo(), client);
        var report = await engine.SyncAsync();

        // Token preserved for retry.
        Assert.Equal("#keep", cal.SyncToken);
        Assert.Equal("#keep", await state.GetSyncTokenAsync(cal.Id));
        // Failure is recorded but does not abort.
        Assert.NotEmpty(report.Errors);
        Assert.Equal(0, report.Pulled);
    }

    // ---- Issue 3: per-calendar failure isolation ---------------------------

    [Fact]
    public async Task Failure_On_One_Calendar_Does_Not_Abort_Others()
    {
        var calA = Cal("a", supportsSync: false); // uses QueryEventsAsync fallback path
        var calB = Cal("b", supportsSync: false);
        var cals = new[] { calA, calB };
        var events = new FakeEventRepo();
        var calRepo = new FakeCalendarRepo(cals);
        var state = new FakeSyncStateRepo();

        var client = new FakeCalDavClient();
        client.QueryHandler = c =>
        {
            if (c == calA)
            {
                throw new InvalidOperationException("server exploded for A");
            }
            return new CalendarQueryResult(new[] { new RemoteEventEntry("/b/x.ics", "\"v1\"", OneEventIcs) });
        };

        var engine = new SyncEngine(events, calRepo, state, new FakeOpRepo(), client);
        var report = await engine.SyncAsync();

        // Calendar B was synced successfully.
        Assert.True(report.Pulled >= 1);
        Assert.NotNull(await events.FindByRemoteHrefAsync("/b/x.ics"));
        // Calendar A's failure was recorded but did not abort the run.
        Assert.Contains(report.Errors, e => e.Contains("a", StringComparison.OrdinalIgnoreCase) && e.Contains("failed"));
    }

    [Fact]
    public async Task Push_Failure_On_One_Op_Does_Not_Abort_Others()
    {
        // One event pushes successfully, the other fails; both must be isolated.
        var events = new FakeEventRepo();
        var cal = Cal("work");
        var calRepo = new FakeCalendarRepo(new[] { cal });

        var okEvt = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            CalendarId = cal.Id,
            Uid = "ok-1",
            Summary = "OK",
            StartUtc = DateTimeOffset.UtcNow,
            EndUtc = DateTimeOffset.UtcNow.AddHours(1),
            SyncStatus = SyncStatus.NewLocal,
        };
        var badEvt = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            CalendarId = cal.Id,
            Uid = "bad-1",
            Summary = "Bad",
            StartUtc = DateTimeOffset.UtcNow,
            EndUtc = DateTimeOffset.UtcNow.AddHours(1),
            SyncStatus = SyncStatus.NewLocal,
        };
        await events.UpsertAsync(okEvt);
        await events.UpsertAsync(badEvt);

        var ops = new FakeOpRepo();
        await ops.EnqueueAsync(new SyncOperation { Id = 1, EventId = okEvt.Id, Kind = SyncOperationKind.Create });
        await ops.EnqueueAsync(new SyncOperation { Id = 2, EventId = badEvt.Id, Kind = SyncOperationKind.Create });

        var client = new FakeCalDavClient
        {
            PutHandler = e =>
            {
                if (e.Uid == "bad-1")
                {
                    throw new CalDavException("PUT /work/bad.ics failed: 500 boom", 500);
                }
                return new PutResult($"/work/{e.Uid}.ics", "\"e-" + e.Uid + "\"");
            },
        };

        var engine = new SyncEngine(events, calRepo, new FakeSyncStateRepo(), ops, client);
        var report = await engine.SyncAsync();

        Assert.True(report.Pushed >= 1, "okEvt should have been pushed");
        Assert.NotEmpty(report.Errors);
        // The bad event is flagged Error; the ok event is Clean.
        var okAfter = await events.GetAsync(okEvt.Id);
        var badAfter = await events.GetAsync(badEvt.Id);
        Assert.Equal(SyncStatus.Clean, okAfter!.SyncStatus);
        Assert.Equal(SyncStatus.Error, badAfter!.SyncStatus);
    }

    // ---- Issue 5: parse-failure quarantine ---------------------------------

    [Fact]
    public async Task Malformed_Remote_Resource_Is_Quarantined_Not_Silently_Dropped()
    {
        var cal = Cal("work", supportsSync: true, token: null);
        var cals = new[] { cal };
        var events = new FakeEventRepo();
        var calRepo = new FakeCalendarRepo(cals);
        var state = new FakeSyncStateRepo();

        var client = new FakeCalDavClient();
        client.SyncHandler = (c, token) => new SyncCollectionResult(
            new[]
            {
                // A good resource and a malformed one in the same sync batch.
                new RemoteEventEntry("/work/good.ics", "\"g1\"", OneEventIcs),
                new RemoteEventEntry("/work/bad.ics", "\"b1\"", "this is not an iCalendar object"),
            },
            Array.Empty<string>(),
            "#after",
            Supported: true);

        var engine = new SyncEngine(events, calRepo, state, new FakeOpRepo(), client);
        var report = await engine.SyncAsync();

        // The good resource is parsed and persisted.
        Assert.NotNull(await events.FindByRemoteHrefAsync("/work/good.ics"));
        // The malformed resource is also persisted (as Error), with the raw payload preserved.
        var quarantined = await events.FindByRemoteHrefAsync("/work/bad.ics");
        Assert.NotNull(quarantined);
        Assert.Equal(SyncStatus.Error, quarantined!.SyncStatus);
        Assert.Equal("\"b1\"", quarantined.ETag);
        Assert.Contains("this is not", quarantined.ICalendarData, StringComparison.Ordinal);
        // An error was surfaced.
        Assert.Contains(report.Errors, e => e.Contains("bad.ics", StringComparison.Ordinal));
        Assert.True(report.Pulled >= 1);
    }

    // ---- CQ-008: bodyless sync-collection entries are refetched, never dropped ---------

    [Fact]
    public async Task Bodyless_Sync_Entry_Is_Refetched_And_Stored()
    {
        var cal = Cal("work");
        var events = new FakeEventRepo();
        var calRepo = new FakeCalendarRepo(new[] { cal });

        var client = new FakeCalDavClient();
        client.SyncHandler = (c, token) => new SyncCollectionResult(
            new[] { new RemoteEventEntry("/work/a.ics", "\"v1\"", OneEventIcs) },
            Array.Empty<string>(),
            "#t1",
            Supported: true,
            MissingDataHrefs: new[] { "/work/nodata.ics" });
        client.GetHandler = (c, href) => new RemoteEventEntry(href, "\"v2\"", OtherEventIcs);

        var engine = new SyncEngine(events, calRepo, new FakeSyncStateRepo(), new FakeOpRepo(), client);
        var report = await engine.SyncAsync();

        // The bodyless href was explicitly refetched…
        Assert.Equal(new[] { "/work/nodata.ics" }, client.GetHrefs);
        // …and its event landed in the store instead of being lost behind the token.
        var fetched = await events.FindByRemoteHrefAsync("/work/nodata.ics");
        Assert.NotNull(fetched);
        Assert.Equal("evt-b", fetched!.Uid);
        Assert.True(report.Pulled >= 2);
    }

    [Fact]
    public async Task Bodyless_Sync_Entry_Gone_On_Server_Is_Treated_As_Deletion()
    {
        var cal = Cal("work");
        var events = new FakeEventRepo();
        var calRepo = new FakeCalendarRepo(new[] { cal });
        await events.UpsertAsync(Row(cal, "/work/nodata.ics", SyncStatus.Clean));

        var client = new FakeCalDavClient();
        client.SyncHandler = (c, token) => new SyncCollectionResult(
            Array.Empty<RemoteEventEntry>(),
            Array.Empty<string>(),
            "#t1",
            Supported: true,
            MissingDataHrefs: new[] { "/work/nodata.ics" });
        // GET answers 404: the resource vanished between the REPORT and the refetch.
        client.GetHandler = (c, href) => null;

        var engine = new SyncEngine(events, calRepo, new FakeSyncStateRepo(), new FakeOpRepo(), client);
        var report = await engine.SyncAsync();

        Assert.Equal(new[] { "/work/nodata.ics" }, client.GetHrefs);
        Assert.Null(await events.FindByRemoteHrefAsync("/work/nodata.ics"));
        Assert.True(report.RemoteDeleted >= 1);
    }

    [Fact]
    public async Task Bodyless_Sync_Entry_Refetch_Failure_Is_Recorded_And_Token_Advances()
    {
        var cal = Cal("work");
        var events = new FakeEventRepo();
        var calRepo = new FakeCalendarRepo(new[] { cal });
        await events.UpsertAsync(Row(cal, "/work/nodata.ics", SyncStatus.Clean));

        var client = new FakeCalDavClient();
        client.SyncHandler = (c, token) => new SyncCollectionResult(
            Array.Empty<RemoteEventEntry>(),
            Array.Empty<string>(),
            "#t1",
            Supported: true,
            MissingDataHrefs: new[] { "/work/nodata.ics" });
        client.GetHandler = (c, href) => throw new CalDavException("GET /work/nodata.ics failed: 507 insufficient storage", 507);

        var engine = new SyncEngine(events, calRepo, new FakeSyncStateRepo(), new FakeOpRepo(), client);
        var report = await engine.SyncAsync();

        // Surfaced like a parse failure — never silently skipped.
        Assert.Contains(report.Errors, e => e.Contains("Refetch", StringComparison.Ordinal)
                                         && e.Contains("/work/nodata.ics", StringComparison.Ordinal));
        // The token still advances (no rollback for one unfetchable resource)…
        Assert.Equal("#t1", cal.SyncToken);
        // …and the local row survives: its server state is UNKNOWN (refetch errored), so the
        // full-sync reconciliation must not reap it as a deletion.
        Assert.NotNull(await events.FindByRemoteHrefAsync("/work/nodata.ics"));
    }

    // ---- CQ-009②: full sync reconciles server-side deletions --------------------------

    [Fact]
    public async Task FullSync_Reaps_Clean_Rows_Absent_From_Server()
    {
        var cal = Cal("work");
        var events = new FakeEventRepo();
        var calRepo = new FakeCalendarRepo(new[] { cal });
        // Zombie: Clean row whose href the (full) response does not list — the server deleted
        // it while this client was offline / held an invalidated token.
        await events.UpsertAsync(Row(cal, "/work/zombie.ics", SyncStatus.Clean));
        // Dirty rows must never be touched by reconciliation — push owns them.
        await events.UpsertAsync(Row(cal, "/work/dirty.ics", SyncStatus.ModifiedLocal));
        // Never-uploaded row: no server counterpart at all.
        var localOnly = Row(cal, null, SyncStatus.NewLocal);
        await events.UpsertAsync(localOnly);

        var client = new FakeCalDavClient();
        client.SyncHandler = (c, token) => new SyncCollectionResult(
            new[] { new RemoteEventEntry("/work/a.ics", "\"v1\"", OneEventIcs) },
            Array.Empty<string>(),
            "#t1",
            Supported: true);

        var engine = new SyncEngine(events, calRepo, new FakeSyncStateRepo(), new FakeOpRepo(), client);
        var report = await engine.SyncAsync();

        Assert.Null(await events.FindByRemoteHrefAsync("/work/zombie.ics"));
        Assert.NotNull(await events.FindByRemoteHrefAsync("/work/dirty.ics"));
        Assert.NotNull(await events.GetAsync(localOnly.Id));
        Assert.True(report.RemoteDeleted >= 1);
        Assert.Equal("#t1", cal.SyncToken);
    }

    [Fact]
    public async Task Incremental_Sync_Does_Not_Reap_Absent_Rows()
    {
        // Lock the counter-case: on an INCREMENTAL sync an absent href is not a deletion
        // (RFC 6578 — deletions are reported as 404 responses) — only the full sync is
        // authoritative. Reaping here would wipe the local store on every no-op sync.
        var cal = Cal("work", supportsSync: true, token: "#have");
        var events = new FakeEventRepo();
        var calRepo = new FakeCalendarRepo(new[] { cal });
        await events.UpsertAsync(Row(cal, "/work/keep.ics", SyncStatus.Clean));

        var client = new FakeCalDavClient();
        client.SyncHandler = (c, token) => new SyncCollectionResult(
            Array.Empty<RemoteEventEntry>(),
            Array.Empty<string>(),
            "#t2",
            Supported: true);

        var engine = new SyncEngine(events, calRepo, new FakeSyncStateRepo(), new FakeOpRepo(), client);
        var report = await engine.SyncAsync();

        Assert.NotNull(await events.FindByRemoteHrefAsync("/work/keep.ics"));
        Assert.Equal(0, report.RemoteDeleted);
    }

    // ---- CQ-014: delete push without ETag ----------------------------------------------

    [Fact]
    public async Task Delete_Without_ETag_Sends_Unconditional_Delete()
    {
        var cal = Cal("work");
        var events = new FakeEventRepo();
        var calRepo = new FakeCalendarRepo(new[] { cal });
        var evt = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            CalendarId = cal.Id,
            Uid = "del-1",
            RemoteHref = "/work/del-1.ics",
            ETag = null, // ETag-less server (RFC 4791 allows getetag to be absent)
            Summary = "D",
            StartUtc = DateTimeOffset.UtcNow,
            EndUtc = DateTimeOffset.UtcNow.AddHours(1),
            SyncStatus = SyncStatus.DeletedLocal,
        };
        await events.UpsertAsync(evt);
        var ops = new FakeOpRepo();
        await ops.EnqueueAsync(new SyncOperation { Id = 1, EventId = evt.Id, Kind = SyncOperationKind.Delete });

        var client = new FakeCalDavClient();
        client.SyncHandler = (c, token) => new SyncCollectionResult(
            Array.Empty<RemoteEventEntry>(), Array.Empty<string>(), "#t1", Supported: true);

        var engine = new SyncEngine(events, calRepo, new FakeSyncStateRepo(), ops, client);
        var report = await engine.SyncAsync();

        // The remote DELETE happened — with NO If-Match (unconditional).
        var call = Assert.Single(client.DeletedCalls);
        Assert.Equal("/work/del-1.ics", call.Href);
        Assert.Null(call.IfMatch);
        // Local row gone and the push counted once.
        Assert.Null(await events.GetAsync(evt.Id));
        Assert.Equal(1, report.Pushed);
    }

    // ---- hygiene #6: 412 conflict detection is typed-StatusCode only -------------------

    [Fact]
    public async Task Precondition_Failure_Is_Detected_Via_Typed_StatusCode()
    {
        // Lock the ONLY supported detection path: CalDavException.StatusCode == 412. The
        // former `ex.Message.Contains(" 412 ")` companion sniff is dead weight — every
        // CalDavException the client throws carries the code — and message-text matching is
        // brittle against localized/renamed status reasons.
        var cal = Cal("work");
        var events = new FakeEventRepo();
        var calRepo = new FakeCalendarRepo(new[] { cal });
        var evt = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            CalendarId = cal.Id,
            Uid = "conflict-1",
            RemoteHref = "/work/conflict-1.ics",
            ETag = "\"v0\"",
            Summary = "C",
            StartUtc = DateTimeOffset.UtcNow,
            EndUtc = DateTimeOffset.UtcNow.AddHours(1),
            SyncStatus = SyncStatus.ModifiedLocal,
        };
        await events.UpsertAsync(evt);
        var ops = new FakeOpRepo();
        await ops.EnqueueAsync(new SyncOperation { Id = 1, EventId = evt.Id, Kind = SyncOperationKind.Update });

        var client = new FakeCalDavClient();
        client.SyncHandler = (c, token) => new SyncCollectionResult(
            Array.Empty<RemoteEventEntry>(), Array.Empty<string>(), "#t1", Supported: true);
        client.PutHandler = e => throw new CalDavException("REPORT rejected the PUT: Precondition Failed", 412);

        var engine = new SyncEngine(events, calRepo, new FakeSyncStateRepo(), ops, client);
        var report = await engine.SyncAsync();

        var after = await events.GetAsync(evt.Id);
        Assert.Equal(SyncStatus.Conflict, after!.SyncStatus);
        Assert.Contains(evt.Id, report.ConflictEventIds);
    }

    private static CalendarEvent Row(CalendarInfo cal, string? href, SyncStatus status) => new()
    {
        Id = Guid.NewGuid(),
        CalendarId = cal.Id,
        Uid = $"evt-{Guid.NewGuid():N}",
        RemoteHref = href,
        ETag = href is null ? null : "\"v0\"",
        Summary = "R",
        StartUtc = DateTimeOffset.UtcNow,
        EndUtc = DateTimeOffset.UtcNow.AddHours(1),
        SyncStatus = status,
    };

    // ========================================================================
    // Minimal in-memory fakes. Kept intentionally small — only the surface area
    // the SyncEngine uses.
    // ========================================================================

    private sealed class FakeCalDavClient : ICalDavClient
    {
        public Account Account { get; init; } = new() { ServerUrl = "https://srv/" };
        public Func<CalendarInfo, string?, SyncCollectionResult>? SyncHandler { get; set; }
        public Func<CalendarInfo, CalendarQueryResult>? QueryHandler { get; set; }
        public Func<CalendarEvent, PutResult>? PutHandler { get; set; }

        // CQ-008: single-resource refetch. Records every href asked for; the handler decides
        // the outcome (entry / null=404 / throw=error).
        public Func<CalendarInfo, string, RemoteEventEntry?>? GetHandler { get; set; }
        public List<string> GetHrefs { get; } = new();

        // CQ-014: records the exact DELETE calls (href + If-Match actually passed).
        public List<(string Href, string? IfMatch)> DeletedCalls { get; } = new();

        public Task<IReadOnlyList<CalendarInfo>> DiscoverCalendarsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CalendarInfo>>(Array.Empty<CalendarInfo>());

        public Task<CalendarQueryResult> QueryEventsAsync(CalendarInfo calendar, DateTimeOffset s, DateTimeOffset e, CancellationToken ct = default)
            => QueryHandler is null
                ? Task.FromResult(new CalendarQueryResult(Array.Empty<RemoteEventEntry>()))
                : Task.FromResult(QueryHandler(calendar));

        public Task<PutResult> PutEventAsync(CalendarInfo calendar, CalendarEvent evt, string? ifMatchEtag, CancellationToken ct = default)
            => Task.FromResult(PutHandler!(evt));

        public Task DeleteEventAsync(CalendarInfo calendar, string href, string? ifMatchEtag, CancellationToken ct = default)
        {
            DeletedCalls.Add((href, ifMatchEtag));
            return Task.CompletedTask;
        }

        public Task<RemoteEventEntry?> GetEventAsync(CalendarInfo calendar, string href, CancellationToken ct = default)
        {
            GetHrefs.Add(href);
            return Task.FromResult(GetHandler?.Invoke(calendar, href));
        }

        public Task<SyncCollectionResult> SyncCollectionAsync(CalendarInfo calendar, string? syncToken, CancellationToken ct = default)
            => Task.FromResult(SyncHandler!(calendar, syncToken));

        public Task<CalendarInfo> RefreshCalendarAsync(CalendarInfo calendar, CancellationToken ct = default)
            => Task.FromResult(calendar);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCalendarRepo : ICalendarRepository
    {
        private readonly Dictionary<Guid, CalendarInfo> _store;
        public FakeCalendarRepo(IEnumerable<CalendarInfo>? seed = null)
            => _store = (seed ?? Array.Empty<CalendarInfo>()).ToDictionary(c => c.Id);

        public Task<IReadOnlyList<CalendarInfo>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CalendarInfo>>(_store.Values.ToList());

        public Task<IReadOnlyList<CalendarInfo>> GetByAccountAsync(Guid accountId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CalendarInfo>>(Array.Empty<CalendarInfo>());

        public Task<CalendarInfo?> GetAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(_store.TryGetValue(id, out var c) ? c : null);

        public Task<CalendarInfo?> FindByRemoteUrlAsync(string remoteUrl, CancellationToken ct = default)
            => Task.FromResult<CalendarInfo?>(null);

        public Task UpsertAsync(CalendarInfo calendar, CancellationToken ct = default)
        {
            _store[calendar.Id] = calendar;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, CancellationToken ct = default) { _store.Remove(id); return Task.CompletedTask; }
        public Task ClearAllSyncTokensAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeEventRepo : IEventRepository
    {
        private readonly Dictionary<Guid, CalendarEvent> _byId = new();
        public Task<CalendarEvent?> GetAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(_byId.TryGetValue(id, out var e) ? e : null);

        public Task<CalendarEvent?> FindByUidAsync(Guid calendarId, string uid, CancellationToken ct = default)
            => Task.FromResult(_byId.Values.FirstOrDefault(e => e.CalendarId == calendarId && e.Uid == uid));

        public Task<CalendarEvent?> FindByRemoteHrefAsync(string href, CancellationToken ct = default)
            => Task.FromResult(_byId.Values.FirstOrDefault(e => e.RemoteHref == href));

        public Task<IReadOnlyList<CalendarEvent>> GetInRangeAsync(IReadOnlyCollection<Guid> calendarIds, DateTimeOffset s, DateTimeOffset e, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CalendarEvent>>(_byId.Values.Where(x => calendarIds.Contains(x.CalendarId)).ToList());

        // T7: lightweight display-path stubs. The sync-engine tests don't exercise display paths,
        // so these mirror the full GetInRangeAsync behaviour (the fake doesn't actually save space
        // by skipping icalendar_data — the real repository does).
        public Task<IReadOnlyList<CalendarEvent>> GetInRangeLightweightAsync(IReadOnlyCollection<Guid> calendarIds, DateTimeOffset s, DateTimeOffset e, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CalendarEvent>>(_byId.Values.Where(x => calendarIds.Contains(x.CalendarId)).ToList());

        public Task<IReadOnlyDictionary<Guid, string>> GetRecurringDataAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<Guid, string>>(
                _byId.Where(kv => ids.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value.ICalendarData ?? string.Empty));

        public Task<IReadOnlyList<CalendarEvent>> GetPendingSyncAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CalendarEvent>>(Array.Empty<CalendarEvent>());

        public Task UpsertAsync(CalendarEvent evt, CancellationToken ct = default) { _byId[evt.Id] = evt; return Task.CompletedTask; }

        // CQ-003: sync-fields-only update. The fake stores references, so this must clone —
        // otherwise it would mutate the very snapshot the engine still holds.
        public Task UpdateSyncFieldsAsync(Guid id, string? remoteHref, string? etag, CancellationToken ct = default)
        {
            if (_byId.TryGetValue(id, out var e))
            {
                var copy = Clone(e);
                copy.RemoteHref = remoteHref;
                copy.ETag = etag;
                _byId[id] = copy;
            }

            return Task.CompletedTask;
        }

        public Task MarkDeletedAsync(Guid id, CancellationToken ct = default) { if (_byId.TryGetValue(id, out var e)) e.SyncStatus = SyncStatus.DeletedLocal; return Task.CompletedTask; }
        public Task HardDeleteAsync(Guid id, CancellationToken ct = default) { _byId.Remove(id); return Task.CompletedTask; }
        public Task HardDeleteAllAsync(CancellationToken ct = default) { _byId.Clear(); return Task.CompletedTask; }

        private static CalendarEvent Clone(CalendarEvent e) => new()
        {
            Id = e.Id,
            CalendarId = e.CalendarId,
            Uid = e.Uid,
            RemoteHref = e.RemoteHref,
            ETag = e.ETag,
            ICalendarData = e.ICalendarData,
            Summary = e.Summary,
            Description = e.Description,
            Location = e.Location,
            IsAllDay = e.IsAllDay,
            StartUtc = e.StartUtc,
            EndUtc = e.EndUtc,
            TimeZoneId = e.TimeZoneId,
            IsFloating = e.IsFloating,
            RecurrenceRule = e.RecurrenceRule,
            RecurrenceId = e.RecurrenceId,
            SyncStatus = e.SyncStatus,
            DeletedAt = e.DeletedAt,
            CreatedAt = e.CreatedAt,
            UpdatedAt = e.UpdatedAt,
        };
    }

    private sealed class FakeSyncStateRepo : ISyncStateRepository
    {
        private readonly Dictionary<Guid, string?> _tokens = new();
        private readonly Dictionary<Guid, string?> _ctags = new();
        public Task UpsertSyncTokenAsync(Guid calendarId, string? syncToken, CancellationToken ct = default) { _tokens[calendarId] = syncToken; return Task.CompletedTask; }
        public Task<string?> GetSyncTokenAsync(Guid calendarId, CancellationToken ct = default)
            => Task.FromResult(_tokens.TryGetValue(calendarId, out var t) ? t : null);
        public Task SaveCtagAsync(Guid calendarId, string? ctag, CancellationToken ct = default) { _ctags[calendarId] = ctag; return Task.CompletedTask; }
        public Task<string?> GetCtagAsync(Guid calendarId, CancellationToken ct = default)
            => Task.FromResult(_ctags.TryGetValue(calendarId, out var c) ? c : null);
        public Task ClearAllAsync(CancellationToken ct = default) { _tokens.Clear(); _ctags.Clear(); return Task.CompletedTask; }
    }

    private sealed class FakeOpRepo : ISyncOperationRepository
    {
        private readonly List<SyncOperation> _ops = new();
        public Task EnqueueAsync(SyncOperation op, CancellationToken ct = default) { _ops.Add(op); return Task.CompletedTask; }
        public Task<IReadOnlyList<SyncOperation>> GetDueAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SyncOperation>>(_ops.Where(o => o.NextAttemptAt is null || o.NextAttemptAt <= DateTimeOffset.UtcNow).ToList());
        public Task MarkAttemptedAsync(long id, string? error, DateTimeOffset? next, CancellationToken ct = default)
        {
            var op = _ops.FirstOrDefault(o => o.Id == id);
            if (op is not null) { op.AttemptCount++; op.LastError = error; op.NextAttemptAt = next; }
            return Task.CompletedTask;
        }
        public Task RemoveAsync(long id, CancellationToken ct = default)
        {
            _ops.RemoveAll(o => o.Id == id);
            return Task.CompletedTask;
        }
        public Task<PendingSyncCounts> GetPendingCountsAsync(CancellationToken ct = default)
        {
            var create = _ops.Count(o => o.Kind == SyncOperationKind.Create);
            var update = _ops.Count(o => o.Kind == SyncOperationKind.Update);
            var delete = _ops.Count(o => o.Kind == SyncOperationKind.Delete);
            return Task.FromResult(new PendingSyncCounts(create, update, delete));
        }
        public Task ClearAllAsync(CancellationToken ct = default) { _ops.Clear(); return Task.CompletedTask; }
    }
}
