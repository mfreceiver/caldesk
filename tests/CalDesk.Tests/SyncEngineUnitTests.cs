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

        public Task<IReadOnlyList<CalendarInfo>> DiscoverCalendarsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CalendarInfo>>(Array.Empty<CalendarInfo>());

        public Task<CalendarQueryResult> QueryEventsAsync(CalendarInfo calendar, DateTimeOffset s, DateTimeOffset e, CancellationToken ct = default)
            => QueryHandler is null
                ? Task.FromResult(new CalendarQueryResult(Array.Empty<RemoteEventEntry>()))
                : Task.FromResult(QueryHandler(calendar));

        public Task<PutResult> PutEventAsync(CalendarInfo calendar, CalendarEvent evt, string? ifMatchEtag, CancellationToken ct = default)
            => Task.FromResult(PutHandler!(evt));

        public Task DeleteEventAsync(CalendarInfo calendar, string href, string ifMatchEtag, CancellationToken ct = default)
            => Task.CompletedTask;

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

        public Task<IReadOnlyList<CalendarEvent>> GetPendingSyncAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CalendarEvent>>(Array.Empty<CalendarEvent>());

        public Task UpsertAsync(CalendarEvent evt, CancellationToken ct = default) { _byId[evt.Id] = evt; return Task.CompletedTask; }
        public Task MarkDeletedAsync(Guid id, CancellationToken ct = default) { if (_byId.TryGetValue(id, out var e)) e.SyncStatus = SyncStatus.DeletedLocal; return Task.CompletedTask; }
        public Task HardDeleteAsync(Guid id, CancellationToken ct = default) { _byId.Remove(id); return Task.CompletedTask; }
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
    }
}
