namespace CalDesk.App.Tests;

/// <summary>CQ-002 App-level regression: <c>SyncRunner.FullResyncAsync</c> must not wipe the
/// local database while a periodic sync run is still in flight. In the buggy version the wipe
/// raced the in-flight run, whose exit then wrote the pre-wipe sync token back into the
/// now-empty store — an empty calendar that reports success forever (valid token → zero
/// changes on every later pull). Reproduced with the real SyncRunner + real StorageServices;
/// only the server is fake: its REPORT gate suspends the second run so the interleave is
/// deterministic. ⚠ Windows-only project — runs on winbuild, not on Linux.</summary>
public sealed class SyncRunnerFullResyncTests
{
    [Fact]
    public async Task FullResync_DuringSuspendedInFlightSync_RepullsEverything()
    {
        using var server = FakeCalDavServer.Start();
        server.SeedEvent("cq2-event-1", "Event 1");
        server.SeedEvent("cq2-event-2", "Event 2");
        server.SeedEvent("cq2-event-3", "Event 3");

        using var harness = new AppSyncHarness(server);

        // Run #1: initial pull stores the 3 events + the calendar sync token.
        await harness.Runner.RunAsync();
        Assert.Equal(3, (await harness.EventsAsync()).Count);

        // Run #2: its sync-collection REPORT is suspended by the server gate, so the run
        // stays in flight exactly like a slow periodic sync would.
        server.GateReports();
        var inFlight = harness.Runner.RunAsync();
        await server.WaitUntilAsync(
            () => server.Requests.Count(r => r.Method == "REPORT") >= 2,
            "the gated REPORT to arrive (run #2 suspended)");

        // Full resync while run #2 is suspended. With the fix it first spins on the _running
        // mutex; before the fix it wiped the database right here (events → 0 mid-flight).
        var resync = harness.Runner.FullResyncAsync();
        await Task.Delay(300); // let the resync spinner start competing for the mutex
        Assert.Equal(3, (await harness.EventsAsync()).Count); // wipe must NOT have happened yet

        server.ReleaseGate();
        await inFlight;
        await resync;

        // End state after the fix: the wipe ran (post run #2), then RunCore re-pulled from
        // empty tokens → 3 events again. Before the fix this was 0 — the empty-calendar
        // deadlock the audit described.
        Assert.Equal(3, (await harness.EventsAsync()).Count);

        // The re-saved token is fresh: a normal run must see zero changes (not re-pull, and
        // not the "valid token over an empty store → 0 events forever" state either).
        await harness.Runner.RunAsync();
        Assert.Equal(0, harness.Runner.LastReport!.Pulled);
        Assert.Equal(3, (await harness.EventsAsync()).Count);
    }
}
