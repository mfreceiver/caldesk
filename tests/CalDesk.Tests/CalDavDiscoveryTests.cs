using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CalDesk.CalDav;
using CalDesk.Core.Models;

namespace CalDesk.Tests;

/// <summary>Offline discovery tests with a scripted <see cref="HttpMessageHandler"/>.
/// Covers redirect method-preservation, same-origin redirect policy, cancellation,
/// root principal fallback, and the happy path.</summary>
public sealed class CalDavDiscoveryTests
{
    private const string Origin = "https://caldav.example:8443/";

    private static Account MakeAccount(string serverUrl = Origin) => new()
    {
        ServerUrl = serverUrl,
        Username = "mar",
        CredentialTarget = "test",
    };

    private static CalDavClient MakeClient(
        HttpMessageHandler handler,
        Account? account = null,
        Action<string>? log = null,
        AuthenticationHeaderValue? authorization = null)
    {
        var origin = new Uri(Origin);
        var http = new HttpClient(handler) { BaseAddress = origin };
        if (authorization is not null)
        {
            http.DefaultRequestHeaders.Authorization = authorization;
        }

        return new CalDavClient(account ?? MakeAccount(), http, origin, log);
    }

    private static string MultistatusProp(string href, string propLocalName, string propInnerXml, string? propNs = null)
    {
        var nsAttr = propNs is null ? "" : $" xmlns:x=\"{propNs}\"";
        var propTag = propNs is null ? $"d:{propLocalName}" : $"x:{propLocalName}";
        return
            $"""<?xml version="1.0" encoding="utf-8"?><d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"{nsAttr}><d:response><d:href>{href}</d:href><d:propstat><d:status>HTTP/1.1 200 OK</d:status><d:prop><{propTag}>{propInnerXml}</{propTag}></d:prop></d:propstat></d:response></d:multistatus>""";
    }

    private static string PrincipalXml(string principalHref) =>
        MultistatusProp("/", "current-user-principal", $"<d:href>{principalHref}</d:href>");

    private static string HomeSetXml(string homeHref) =>
        MultistatusProp("/principals/mar/", "calendar-home-set", $"<d:href>{homeHref}</d:href>", "urn:ietf:params:xml:ns:caldav");

    private static string EnumerateCalendarsXml(string homeHref, params (string href, string name)[] calendars)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="utf-8"?><d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">""");
        // home itself (collection, not a calendar)
        sb.Append($"<d:response><d:href>{homeHref}</d:href><d:propstat><d:status>HTTP/1.1 200 OK</d:status><d:prop><d:resourcetype><d:collection/></d:resourcetype><d:displayname>home</d:displayname></d:prop></d:propstat></d:response>");
        foreach (var (href, name) in calendars)
        {
            sb.Append(
                $"""<d:response><d:href>{href}</d:href><d:propstat><d:status>HTTP/1.1 200 OK</d:status><d:prop><d:resourcetype><d:collection/><c:calendar/></d:resourcetype><d:displayname>{name}</d:displayname></d:prop></d:propstat></d:response>""");
        }
        sb.Append("</d:multistatus>");
        return sb.ToString();
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>> _steps = new();

        public List<(string Method, string Path, string? Host, string? Body)> Requests { get; } = new();

        public void Enqueue(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> step) => _steps.Enqueue(step);

        public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> step) =>
            Enqueue((req, _) => step(req));

        public void Enqueue(HttpStatusCode status, string? body = null, string? location = null, string contentType = "application/xml")
        {
            Enqueue(_ =>
            {
                var resp = new HttpResponseMessage(status);
                if (location is not null)
                {
                    // Prefer absolute http(s); path-only strings must stay Relative (on Unix,
                    // UriKind.RelativeOrAbsolute turns "/caldav/" into file:///caldav/).
                    if (Uri.TryCreate(location, UriKind.Absolute, out var abs)
                        && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
                    {
                        resp.Headers.Location = abs;
                    }
                    else
                    {
                        resp.Headers.Location = new Uri(location, UriKind.Relative);
                    }
                }
                if (body is not null)
                {
                    resp.Content = new StringContent(body, Encoding.UTF8, contentType);
                }
                return resp;
            });
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = request.RequestUri?.IsAbsoluteUri == true
                ? request.RequestUri.AbsolutePath
                : request.RequestUri?.OriginalString ?? "";
            var host = request.RequestUri?.IsAbsoluteUri == true
                ? request.RequestUri.GetLeftPart(UriPartial.Authority)
                : null;
            var body = request.Content is null
                ? null
                : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            Requests.Add((request.Method.Method, path, host, body));

            if (_steps.Count == 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("unexpected request: " + request.Method + " " + path),
                });
            }

            return Task.FromResult(_steps.Dequeue()(request, cancellationToken));
        }
    }

    [Fact]
    public async Task Discover_Redirect_Preserves_Propfind_Method()
    {
        var handler = new ScriptedHandler();
        // 1) well-known → 302 to /caldav/
        handler.Enqueue(HttpStatusCode.Found, location: "/caldav/");
        // 2) redirected PROPFIND at /caldav/ → principal
        handler.Enqueue(HttpStatusCode.MultiStatus, PrincipalXml("/principals/mar/"));
        // 3) principal → home-set
        handler.Enqueue(HttpStatusCode.MultiStatus, HomeSetXml("/calendars/mar/"));
        // 4) enumerate home
        handler.Enqueue(HttpStatusCode.MultiStatus,
            EnumerateCalendarsXml("/calendars/mar/", ("/calendars/mar/work/", "Work"), ("/calendars/mar/home/", "Home")));

        await using var client = MakeClient(handler);
        var calendars = await client.DiscoverCalendarsAsync();

        Assert.Equal(2, calendars.Count);
        Assert.Contains(calendars, c => c.DisplayName == "Work");
        Assert.Contains(calendars, c => c.DisplayName == "Home");

        // First request: well-known PROPFIND
        Assert.Equal("PROPFIND", handler.Requests[0].Method);
        Assert.Equal("/.well-known/caldav", handler.Requests[0].Path);
        Assert.False(string.IsNullOrEmpty(handler.Requests[0].Body));

        // Second request MUST re-issue PROPFIND (not GET) at the Location
        Assert.True(handler.Requests.Count >= 2, "expected follow-up after redirect");
        Assert.Equal("PROPFIND", handler.Requests[1].Method);
        Assert.Equal("/caldav/", handler.Requests[1].Path);
        Assert.False(string.IsNullOrEmpty(handler.Requests[1].Body), "redirected PROPFIND must keep its body");
    }

    [Fact]
    public async Task Discover_Root_CurrentUserPrincipal_Fallback_When_WellKnown_Fails()
    {
        var handler = new ScriptedHandler();
        // 1) well-known 404 → no principal
        handler.Enqueue(HttpStatusCode.NotFound);
        // 2) fallback: PROPFIND current-user-principal on ServerUrl root "/"
        handler.Enqueue(HttpStatusCode.MultiStatus, PrincipalXml("/principals/mar/"));
        // 3) home-set
        handler.Enqueue(HttpStatusCode.MultiStatus, HomeSetXml("/calendars/mar/"));
        // 4) enumerate
        handler.Enqueue(HttpStatusCode.MultiStatus,
            EnumerateCalendarsXml("/calendars/mar/", ("/calendars/mar/work/", "Work")));

        await using var client = MakeClient(handler, MakeAccount("https://caldav.example:8443/"));
        var calendars = await client.DiscoverCalendarsAsync();

        Assert.Single(calendars);
        Assert.Equal("Work", calendars[0].DisplayName);

        Assert.Equal("PROPFIND", handler.Requests[0].Method);
        Assert.Equal("/.well-known/caldav", handler.Requests[0].Path);

        Assert.Equal("PROPFIND", handler.Requests[1].Method);
        Assert.Equal("/", handler.Requests[1].Path);
        Assert.Contains("current-user-principal", handler.Requests[1].Body ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_HappyPath_NoRedirect()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.MultiStatus, PrincipalXml("/principals/mar/"));
        handler.Enqueue(HttpStatusCode.MultiStatus, HomeSetXml("/calendars/mar/"));
        handler.Enqueue(HttpStatusCode.MultiStatus,
            EnumerateCalendarsXml("/calendars/mar/",
                ("/calendars/mar/work/", "Work"),
                ("/calendars/mar/personal/", "Personal"),
                ("/calendars/mar/shared/", "Shared")));

        var logs = new List<string>();
        await using var client = MakeClient(handler, log: logs.Add);
        var calendars = await client.DiscoverCalendarsAsync();

        Assert.Equal(3, calendars.Count);
        Assert.All(calendars, c => Assert.Equal(client.Account.Id, c.AccountId));

        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal("PROPFIND", r.Method));
        Assert.Equal("/.well-known/caldav", handler.Requests[0].Path);

        // Optional step logger should have recorded discovery progress without throwing.
        Assert.NotEmpty(logs);
        Assert.Contains(logs, l => l.Contains("well-known", StringComparison.OrdinalIgnoreCase)
                                   || l.Contains("PROPFIND", StringComparison.OrdinalIgnoreCase)
                                   || l.Contains("principal", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Discover_CrossOrigin_Redirect_Does_Not_Issue_Foreign_Request()
    {
        var handler = new ScriptedHandler();
        var logs = new List<string>();
        // well-known open-redirects off-origin — must NOT be followed
        handler.Enqueue(HttpStatusCode.Found, location: "https://evil.example/steal");
        // safe fallback after refused hop: root principal chain
        handler.Enqueue(HttpStatusCode.MultiStatus, PrincipalXml("/principals/mar/"));
        handler.Enqueue(HttpStatusCode.MultiStatus, HomeSetXml("/calendars/mar/"));
        handler.Enqueue(HttpStatusCode.MultiStatus,
            EnumerateCalendarsXml("/calendars/mar/", ("/calendars/mar/work/", "Work")));

        await using var client = MakeClient(handler, log: logs.Add);
        var calendars = await client.DiscoverCalendarsAsync();

        Assert.DoesNotContain(handler.Requests, r =>
            (r.Host is not null && r.Host.Contains("evil.example", StringComparison.OrdinalIgnoreCase))
            || r.Path.Contains("evil.example", StringComparison.OrdinalIgnoreCase)
            || r.Path.Contains("/steal", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(logs, l => l.Contains("refused cross-origin", StringComparison.OrdinalIgnoreCase));
        Assert.Single(calendars);
        Assert.Equal("Work", calendars[0].DisplayName);

        // First request only to well-known; next is root fallback, never evil host
        Assert.Equal("/.well-known/caldav", handler.Requests[0].Path);
        Assert.Equal("/", handler.Requests[1].Path);
    }

    [Fact]
    public async Task Discover_Cancellation_Propagates_OperationCanceledException()
    {
        var handler = new ScriptedHandler();
        using var cts = new CancellationTokenSource();
        handler.Enqueue((_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        await using var client = MakeClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.DiscoverCalendarsAsync(cts.Token));
    }

    [Fact]
    public async Task Discover_Does_Not_Clear_DefaultRequestHeaders_Authorization()
    {
        var handler = new ScriptedHandler();
        // same-origin redirect only — if code cleared DefaultRequestHeaders on any hop, auth would vanish
        handler.Enqueue(HttpStatusCode.Found, location: "/caldav/");
        handler.Enqueue(HttpStatusCode.MultiStatus, PrincipalXml("/principals/mar/"));
        handler.Enqueue(HttpStatusCode.MultiStatus, HomeSetXml("/calendars/mar/"));
        handler.Enqueue(HttpStatusCode.MultiStatus,
            EnumerateCalendarsXml("/calendars/mar/", ("/calendars/mar/work/", "Work")));

        var auth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("mar:secret")));
        var origin = new Uri(Origin);
        using var http = new HttpClient(handler) { BaseAddress = origin };
        http.DefaultRequestHeaders.Authorization = auth;

        await using var client = new CalDavClient(MakeAccount(), http, origin);
        _ = await client.DiscoverCalendarsAsync();

        Assert.NotNull(http.DefaultRequestHeaders.Authorization);
        Assert.Equal(auth.Scheme, http.DefaultRequestHeaders.Authorization!.Scheme);
        Assert.Equal(auth.Parameter, http.DefaultRequestHeaders.Authorization.Parameter);
    }

    // ---- first-hop same-origin gate (DELETE/PUT entry points) -----------------

    [Fact]
    public async Task DeleteEvent_Absolute_Foreign_Href_Does_Not_Send_And_Throws()
    {
        var handler = new ScriptedHandler();
        // No successful responses enqueued — any SendAsync would still be recorded.
        await using var client = MakeClient(handler);
        var cal = new CalendarInfo
        {
            AccountId = Guid.NewGuid(),
            RemoteUrl = Origin + "calendars/mar/work/",
            DisplayName = "Work",
        };

        var ex = await Assert.ThrowsAsync<CalDavException>(() =>
            client.DeleteEventAsync(cal, "https://evil.example/steal/x.ics", "\"etag1\""));

        Assert.Empty(handler.Requests);
        Assert.Contains("refused", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("evil.example", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(handler.Requests, r =>
            (r.Host is not null && r.Host.Contains("evil.example", StringComparison.OrdinalIgnoreCase))
            || r.Path.Contains("evil.example", StringComparison.OrdinalIgnoreCase)
            || r.Path.Contains("/steal", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Protocol-relative / network-path reference (RFC 3986 §4.2). Combined with BaseAddress,
    /// <c>//evil.example/x.ics</c> resolves to a foreign host while Default Authorization stays —
    /// must refuse before any outbound send.
    /// </summary>
    [Fact]
    public async Task DeleteEvent_NetworkPath_Href_Does_Not_Send_And_Throws()
    {
        var handler = new ScriptedHandler();
        await using var client = MakeClient(handler);
        var cal = new CalendarInfo
        {
            AccountId = Guid.NewGuid(),
            RemoteUrl = Origin + "calendars/mar/work/",
            DisplayName = "Work",
        };

        var ex = await Assert.ThrowsAsync<CalDavException>(() =>
            client.DeleteEventAsync(cal, "//evil.example/x.ics", "\"etag1\""));

        Assert.Empty(handler.Requests);
        Assert.Contains("refused", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(handler.Requests, r =>
            (r.Host is not null && r.Host.Contains("evil.example", StringComparison.OrdinalIgnoreCase))
            || r.Path.Contains("evil.example", StringComparison.OrdinalIgnoreCase)
            || r.Path.Contains("//evil", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeleteEvent_Absolute_SameOrigin_Href_Is_Rewritten_To_Path()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.NoContent);

        await using var client = MakeClient(handler);
        var cal = new CalendarInfo
        {
            AccountId = Guid.NewGuid(),
            RemoteUrl = Origin + "calendars/mar/work/",
            DisplayName = "Work",
        };

        await client.DeleteEventAsync(
            cal,
            "https://caldav.example:8443/calendars/mar/work/x.ics",
            "\"etag1\"");

        Assert.Single(handler.Requests);
        Assert.Equal("DELETE", handler.Requests[0].Method);
        Assert.Equal("/calendars/mar/work/x.ics", handler.Requests[0].Path);
        // Must not leave an absolute foreign-looking RequestUri host; relative path keeps BaseAddress.
        var host = handler.Requests[0].Host;
        Assert.True(
            host is null
            || host.Contains("caldav.example", StringComparison.OrdinalIgnoreCase),
            "same-origin delete must stay on account origin");
        Assert.DoesNotContain(handler.Requests, r =>
            r.Host is not null && r.Host.Contains("evil.example", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Discover_HttpTimeout_OCE_Falls_Through_To_Root_Fallback()
    {
        // HttpClient.Timeout surfaces as OperationCanceledException with a non-cancelled
        // caller token — discovery must treat that like other failures and try fallbacks.
        var handler = new ScriptedHandler();
        handler.Enqueue((_, _) => throw new OperationCanceledException("simulated HttpClient.Timeout"));
        handler.Enqueue(HttpStatusCode.MultiStatus, PrincipalXml("/principals/mar/"));
        handler.Enqueue(HttpStatusCode.MultiStatus, HomeSetXml("/calendars/mar/"));
        handler.Enqueue(HttpStatusCode.MultiStatus,
            EnumerateCalendarsXml("/calendars/mar/", ("/calendars/mar/work/", "Work")));

        await using var client = MakeClient(handler);
        var calendars = await client.DiscoverCalendarsAsync();

        Assert.Single(calendars);
        Assert.Equal("Work", calendars[0].DisplayName);
        Assert.Equal("/.well-known/caldav", handler.Requests[0].Path);
        Assert.Equal("/", handler.Requests[1].Path);
    }
}
