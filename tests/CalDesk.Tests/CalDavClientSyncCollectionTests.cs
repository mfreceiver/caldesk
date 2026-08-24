using System.Net;
using System.Text;
using CalDesk.CalDav;
using CalDesk.Core.Models;
using Xunit;

namespace CalDesk.Tests;

/// <summary>Classification tests for <see cref="CalDavClient.SyncCollectionAsync"/> over a
/// stub HTTP transport (no server, no SQLite). Locks the CQ-008/CQ-009① semantics:
/// transient network failures must propagate (so the engine retries the incremental sync
/// instead of silently falling back to a full re-pull), only definitive protocol-level
/// rejections mean "unsupported", and bodyless changed hrefs must be surfaced rather than
/// filtered away.</summary>
public sealed class CalDavClientSyncCollectionTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }

    private static CalendarInfo Cal() => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = "work",
        RemoteUrl = "https://srv/work/",
        SupportsSyncToken = true,
    };

    private static CalDavClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        // BaseAddress is required: CalDavClient issues path-relative request targets and
        // relies on the HttpClient to resolve them (same shape as CalDavClientFactory).
        var http = new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("https://srv/") };
        return new CalDavClient(new Account { ServerUrl = "https://srv/" }, http, new Uri("https://srv/"));
    }

    [Fact]
    public async Task Network_Failure_Propagates_Instead_Of_Reported_Unsupported()
    {
        // CQ-009①: a connection reset is transient. The old blanket catch mapped it to
        // Supported=false, which made the engine drop its incremental token and perform an
        // unnecessary full re-pull (and masked persistent outages as empty syncs).
        var client = Client(_ => throw new HttpRequestException("connection reset"));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SyncCollectionAsync(Cal(), "#t"));
    }

    [Fact]
    public async Task MethodNotAllowed_Means_Unsupported_Not_Failure()
    {
        // CQ-009①: 405/501 on the REPORT itself is the definitive "server does not implement
        // sync-collection" signal — the engine may fall back to calendar-query.
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));

        var result = await client.SyncCollectionAsync(Cal(), "#t");

        Assert.False(result.Supported);
    }

    [Fact]
    public async Task Changed_Href_Without_Body_Is_Surfaced_As_MissingData()
    {
        // CQ-008: /work/a.ics arrives complete; /work/b.ics is 200-but-bodyless (truncated
        // response / non-compliant server). The bodyless href must be reported via
        // MissingDataHrefs — filtering it out here would advance the token past a change
        // the engine never saw.
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
              <D:response>
                <D:href>/work/a.ics</D:href>
                <D:propstat><D:status>HTTP/1.1 200 OK</D:status>
                  <D:prop><D:getetag>"e1"</D:getetag>
                    <C:calendar-data>BEGIN:VCALENDAR&#xD;&#xA;VERSION:2.0&#xD;&#xA;BEGIN:VEVENT&#xD;&#xA;UID:evt-a&#xD;&#xA;DTSTART:20260715T100000Z&#xD;&#xA;DTEND:20260715T110000Z&#xD;&#xA;SUMMARY:A&#xD;&#xA;END:VEVENT&#xD;&#xA;END:VCALENDAR&#xD;&#xA;</C:calendar-data>
                  </D:prop>
                </D:propstat>
              </D:response>
              <D:response>
                <D:href>/work/b.ics</D:href>
                <D:propstat><D:status>HTTP/1.1 200 OK</D:status>
                  <D:prop><D:getetag>"e2"</D:getetag></D:prop>
                </D:propstat>
              </D:response>
              <D:sync-token>#t1</D:sync-token>
            </D:multistatus>
            """;
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.MultiStatus)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
        });

        var result = await client.SyncCollectionAsync(Cal(), null);

        Assert.True(result.Supported);
        var href = Assert.Single(result.Changed).Href;
        Assert.Equal("/work/a.ics", href);
        Assert.NotNull(result.MissingDataHrefs);
        Assert.Equal(new[] { "/work/b.ics" }, result.MissingDataHrefs);
        Assert.Equal("#t1", result.NewSyncToken);
    }
}
