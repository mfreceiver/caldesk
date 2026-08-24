using CalDesk.App.Models;
using CalDesk.App.Services;
using CalDesk.App.ViewModels;
using CalDesk.Core.Services;

namespace CalDesk.App.Tests;

/// <summary>CQ-001 App-level regression: editing an event whose RRULE is outside the QuickAdd
/// preset subset (FREQ=WEEKLY;INTERVAL=2 surfaces as "Custom") must not destroy the rule when
/// only the title changed. Drives the real <see cref="QuickAddViewModel"/> (constructible
/// headless — no WPF Dispatcher involved) through the real EventService outbox and the real
/// SyncRunner push, then asserts on the PUT body the fake server actually received.
/// ⚠ Windows-only project — runs on winbuild, not on Linux.</summary>
public sealed class QuickAddRecurrenceTests
{
    [Fact]
    public async Task EditBiweeklyEvent_TitleOnly_PutBodyKeepsRrule()
    {
        using var server = FakeCalDavServer.Start();
        server.SeedEvent("cq1-biweekly", "Biweekly review", rrule: "FREQ=WEEKLY;INTERVAL=2");

        using var harness = new AppSyncHarness(server);

        // Pull: the event lands locally with the rule stored verbatim.
        await harness.Runner.RunAsync();
        var stored = (await harness.EventsAsync()).Single();
        Assert.Contains("FREQ=WEEKLY", stored.RecurrenceRule, StringComparison.Ordinal);
        Assert.Contains("INTERVAL=2", stored.RecurrenceRule, StringComparison.Ordinal);
        Assert.NotNull(stored.RemoteHref);
        Assert.NotNull(stored.ETag);

        var eventService = new EventService(harness.Store.Events, harness.Store.SyncOperations, harness.Store);
        var vm = new QuickAddViewModel(
            eventService,
            new SettingsService(),
            new AppSettings(),
            harness.Runner,
            [harness.Calendar]);

        vm.InitializeForEdit(stored);
        // INTERVAL=2 is outside the preset subset → must surface as Custom (not Weekly, and
        // never None, which would strip the rule on save).
        Assert.Equal(RecurrencePick.Custom, vm.Repeat);

        // Title-only edit, then save exactly as the dialog does.
        vm.Title = "Biweekly review (renamed)";
        await vm.SaveCommand.ExecuteAsync(null);

        // SaveAsync kicks the sync runner fire-and-forget; wait for the PUT to land.
        await server.WaitUntilAsync(
            () => server.Puts.Count > 0,
            "the QuickAdd edit PUT to reach the server");

        var put = server.Puts.Single();
        Assert.Contains("Biweekly review (renamed)", put.Body, StringComparison.Ordinal);
        // The recurrence must survive the edit round-trip on the wire — assert on the RRULE
        // line of the uploaded iCalendar, not on local state.
        Assert.Contains("RRULE:", put.Body, StringComparison.Ordinal);
        Assert.Contains("FREQ=WEEKLY", put.Body, StringComparison.Ordinal);
        Assert.Contains("INTERVAL=2", put.Body, StringComparison.Ordinal);
    }
}
