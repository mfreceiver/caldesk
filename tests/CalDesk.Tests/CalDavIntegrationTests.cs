using CalDesk.CalDav;
using CalDesk.CalDav.Calendar;
using CalDesk.Core.Models;
using CalDesk.Core.Services;
using Xunit;

namespace CalDesk.Tests;

/// <summary>Live integration tests against a real CalDAV server.
/// Configure via env: CALDAV_TEST_URL, CALDAV_TEST_USER, CALDAV_TEST_PASS.
/// Skipped when the variables are absent.</summary>
public sealed class CalDavIntegrationTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("CALDAV_TEST_URL");
    private static readonly string? User = Environment.GetEnvironmentVariable("CALDAV_TEST_USER");
    private static readonly string? Pass = Environment.GetEnvironmentVariable("CALDAV_TEST_PASS");

    private static bool Configured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(User) && Pass is not null;

    private sealed class EnvCredentialStore : ICredentialStore
    {
        private readonly string _password;
        public EnvCredentialStore(string password) => _password = password;
        public Task<string?> GetPasswordAsync(string target, CancellationToken ct = default) => Task.FromResult<string?>(_password);
        public Task SetPasswordAsync(string target, string password, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string target, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string target, CancellationToken ct = default) => Task.FromResult(!string.IsNullOrEmpty(_password));
    }

    private static async Task<ICalDavClient> CreateClientAsync()
    {
        var account = new Account
        {
            ServerUrl = Url!,
            Username = User!,
            CredentialTarget = "test",
        };
        var factory = new CalDavClientFactory(new EnvCredentialStore(Pass!));
        return await factory.CreateAsync(account);
    }

    [Fact]
    public async Task DiscoversCalendarsExcludingAddressbooks()
    {
        if (!Configured) return;

        await using var client = await CreateClientAsync();
        var calendars = await client.DiscoverCalendarsAsync();

        Assert.NotEmpty(calendars);
        Assert.Contains(calendars, c => c.RemoteUrl.Contains("/work/", StringComparison.OrdinalIgnoreCase));
        // The CardDAV addressbook (/user/contact/) must be filtered out.
        Assert.DoesNotContain(calendars, c => c.RemoteUrl.Contains("/contact", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task QueryReturnsAndParsesEvents()
    {
        if (!Configured) return;

        await using var client = await CreateClientAsync();
        var calendars = await client.DiscoverCalendarsAsync();
        var work = calendars.First(c => c.RemoteUrl.Contains("/work/", StringComparison.OrdinalIgnoreCase));

        var start = DateTimeOffset.UtcNow.AddDays(-7);
        var end = DateTimeOffset.UtcNow.AddDays(14);
        var result = await client.QueryEventsAsync(work, start, end);

        Assert.NotEmpty(result.Entries);

        var parsed = result.Entries.SelectMany(e => IcalMapper.ToCalendarEvents(e.ICalendarData, work.Id, e.Href, e.ETag)).ToList();
        Assert.NotEmpty(parsed);
        Assert.All(parsed, e => Assert.False(string.IsNullOrEmpty(e.Uid)));
        Assert.Contains(parsed, e => !string.IsNullOrEmpty(e.Summary));

        // The work calendar contains a floating-time event (DTSTART without Z / TZID).
        Assert.Contains(parsed, e => e.IsFloating);
    }
}
