using System.IO;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace CalDesk.App.Tests;

/// <summary>One request the fake server received, in arrival order.
/// <see cref="Body"/> carries the decoded request payload (PUT = the iCalendar text;
/// REPORT = the sync-collection XML).</summary>
public sealed record RecordedRequest(
    int Seq,
    string Method,
    string Path,
    string? Body,
    string? IfMatch,
    string? IfNoneMatch);

/// <summary>In-memory fake CalDAV server for App-level regression tests (CQ-001 / CQ-002).
/// Speaks only the WebDAV/CalDAV slice the sync engine exercises against an already-known
/// calendar: REPORT (sync-collection) for pull, PUT/DELETE for push, GET for debugging.
/// Discovery (OPTIONS/PROPFIND) is not implemented — tests upsert the calendar row directly,
/// which is also what pins the engine to the sync-collection (REPORT) code path.
///
/// Test affordances:
/// <list type="bullet">
/// <item><see cref="GateReports"/> — suspend the next REPORT until <see cref="ReleaseGate"/>
/// (TaskCompletionSource latch). Used to manufacture the CQ-002 interleave where a periodic
/// run is still in flight while FullResyncAsync wants to wipe the database.</item>
/// <item><see cref="Puts"/> — every PUT (path + body + match headers) is recorded, so tests
/// can assert what actually went over the wire (CQ-001).</item>
/// </list>
/// </summary>
public sealed class FakeCalDavServer : IDisposable
{
    private const string CalendarSegment = "/cal";

    private readonly HttpListener _listener = new();
    private readonly object _lock = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly Dictionary<string, FakeEvent> _events = new(StringComparer.OrdinalIgnoreCase);

    private int _seq;
    private int _version = 1; // sync-token version; bumped on every mutation
    private int _etagSeed = 1;
    private TaskCompletionSource? _reportGate;

    private FakeCalDavServer(int port)
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        Port = port;
    }

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}/";

    /// <summary>URL of the (single) fake calendar collection the tests configure.</summary>
    public string CalendarUrl => BaseUrl + "cal/";

    /// <summary>Snapshot of all requests received so far, in arrival order.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get { lock (_lock) return [.. _requests]; }
    }

    /// <summary>Snapshot of the PUT requests received so far (upload bodies for assertions).</summary>
    public IReadOnlyList<RecordedRequest> Puts
    {
        get { lock (_lock) return _requests.Where(r => r.Method == "PUT").ToList(); }
    }

    public static FakeCalDavServer Start()
    {
        // HttpListener has no "port 0" wildcard: probe random loopback ports until one binds.
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var server = new FakeCalDavServer(Random.Shared.Next(20000, 60000));
            try
            {
                server._listener.Start();
            }
            catch (HttpListenerException)
            {
                try { server._listener.Close(); } catch { /* best effort */ }
                continue;
            }

            server.RunLoop();
            return server;
        }

        throw new InvalidOperationException("Could not bind a loopback port for the fake CalDAV server.");
    }

    /// <summary>Seed (or replace) a VEVENT the server serves via sync-collection REPORT.
    /// The UID also determines the event href (<c>/cal/{uid}.ics</c>). Seeding advances the
    /// sync token — a client holding an older token must see the new/changed event.</summary>
    public void SeedEvent(string uid, string summary, string? rrule = null)
    {
        var href = $"{CalendarSegment}/{uid}.ics";
        lock (_lock)
        {
            _version++;
            _events[href] = new FakeEvent(href, uid, $"\"e{_etagSeed++}\"", BuildIcs(uid, summary, rrule));
        }
    }

    /// <summary>Suspend the next REPORT request until <see cref="ReleaseGate"/> is called.
    /// One-shot: after release (or if no REPORT ever arrives) the gate is gone.</summary>
    public void GateReports()
        => Interlocked.Exchange(ref _reportGate, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>Release a gate installed by <see cref="GateReports"/> (no-op when none).</summary>
    public void ReleaseGate()
        => Interlocked.Exchange(ref _reportGate, null)?.TrySetResult();

    /// <summary>Poll <paramref name="condition"/> every 25 ms until it holds or the timeout
    /// (default 20 s) expires; throws <see cref="TimeoutException"/> with the request log,
    /// which is the only way to debug a hung winbuild run after the fact.</summary>
    public async Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Timed out waiting for {what}. Requests seen so far: {string.Join("; ", Requests.Select(r => $"#{r.Seq} {r.Method} {r.Path}"))}");
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _reportGate, null)?.TrySetResult();
        try { _listener.Stop(); } catch { /* already stopped */ }
        try { _listener.Close(); } catch { /* already closed */ }
    }

    private static string BuildIcs(string uid, string summary, string? rrule)
    {
        var sb = new StringBuilder();
        sb.Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//CalDesk//FakeCalDavServer//EN\r\nBEGIN:VEVENT\r\n");
        sb.Append("UID:").Append(uid).Append("\r\n");
        sb.Append("DTSTAMP:20260824T000000Z\r\n");
        sb.Append("DTSTART:20260901T090000Z\r\nDTEND:20260901T100000Z\r\n");
        if (!string.IsNullOrEmpty(rrule))
        {
            sb.Append("RRULE:").Append(rrule).Append("\r\n");
        }

        sb.Append("SUMMARY:").Append(summary).Append("\r\n");
        sb.Append("END:VEVENT\r\nEND:VCALENDAR\r\n");
        return sb.ToString();
    }

    private void RunLoop()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleAsync(ctx));
                }
            }
            catch
            {
                // GetContextAsync throws once Stop()/Close() disposes the listener: loop exit.
            }
        });
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var path = req.Url?.AbsolutePath ?? "/";
            string? body = null;
            if (req.HasEntityBody)
            {
                using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            Record(req.HttpMethod, path, body, req.Headers["If-Match"], req.Headers["If-None-Match"]);

            var normalized = path.TrimEnd('/');
            if (req.HttpMethod == "REPORT" && normalized.Equals(CalendarSegment, StringComparison.OrdinalIgnoreCase))
            {
                await HandleReportAsync(ctx, body).ConfigureAwait(false);
                return;
            }

            if (req.HttpMethod == "PUT" && normalized.StartsWith(CalendarSegment + "/", StringComparison.OrdinalIgnoreCase))
            {
                HandlePut(ctx, path, body);
                return;
            }

            if (req.HttpMethod == "DELETE" && normalized.StartsWith(CalendarSegment + "/", StringComparison.OrdinalIgnoreCase))
            {
                HandleDelete(ctx, path);
                return;
            }

            if (req.HttpMethod == "GET" && normalized.StartsWith(CalendarSegment + "/", StringComparison.OrdinalIgnoreCase))
            {
                HandleGet(ctx, path);
                return;
            }

            // OPTIONS/PROPFIND (discovery) and anything else: these tests never discover.
            Respond(ctx, 404, "text/plain", "not supported by FakeCalDavServer");
        }
        catch
        {
            // The fake server must never take the test process down; a dropped request
            // surfaces as a client-side timeout in the assertions.
        }
    }

    private void Record(string method, string path, string? body, string? ifMatch, string? ifNoneMatch)
    {
        lock (_lock)
        {
            _requests.Add(new RecordedRequest(++_seq, method, path, body, ifMatch, ifNoneMatch));
        }
    }

    private async Task HandleReportAsync(HttpListenerContext ctx, string? body)
    {
        // CQ-002 latch: if armed, this REPORT (and the whole sync run behind it) stays
        // suspended until the test releases the gate.
        var gate = Volatile.Read(ref _reportGate);
        if (gate is not null)
        {
            await gate.Task.ConfigureAwait(false);
        }

        var clientToken = TryParseSyncToken(body);
        List<FakeEvent> changed;
        string token;
        lock (_lock)
        {
            token = $"t{_version}";
            // Initial sync (no/empty token) or a stale token → replay everything;
            // current token → empty change list. Good enough for the flows under test.
            changed = string.IsNullOrEmpty(clientToken) || !string.Equals(clientToken, token, StringComparison.Ordinal)
                ? [.. _events.Values]
                : [];
        }

        Respond(ctx, 207, "application/xml; charset=utf-8", BuildMultistatus(changed, token));
    }

    private void HandlePut(HttpListenerContext ctx, string path, string? body)
    {
        lock (_lock)
        {
            _version++;
            var etag = $"\"e{_etagSeed++}\"";
            _events[path] = new FakeEvent(path, PathToUid(path), etag, body ?? string.Empty);
            ctx.Response.Headers["ETag"] = etag;
        }

        ctx.Response.StatusCode = 204; // No Content — client accepts any 2xx and reads the ETag
        ctx.Response.Close();
    }

    private void HandleDelete(HttpListenerContext ctx, string path)
    {
        bool removed;
        lock (_lock)
        {
            removed = _events.Remove(path);
            if (removed)
            {
                _version++;
            }
        }

        ctx.Response.StatusCode = removed ? 204 : 404; // the client treats 404 as already-gone
        ctx.Response.Close();
    }

    private void HandleGet(HttpListenerContext ctx, string path)
    {
        FakeEvent? evt;
        lock (_lock)
        {
            _events.TryGetValue(path, out evt);
        }

        if (evt is null)
        {
            Respond(ctx, 404, "text/plain", "not found");
            return;
        }

        Respond(ctx, 200, "text/calendar; charset=utf-8", evt.Ics);
    }

    private static string? TryParseSyncToken(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        try
        {
            var doc = XDocument.Parse(body);
            return doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "sync-token")?.Value;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string BuildMultistatus(IReadOnlyList<FakeEvent> events, string syncToken)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="utf-8"?>""");
        sb.Append("""<d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">""");
        foreach (var e in events)
        {
            sb.Append("<d:response>")
              .Append("<d:href>").Append(e.Href).Append("</d:href>")
              .Append("<d:propstat><d:status>HTTP/1.1 200 OK</d:status><d:prop>")
              .Append("<d:getetag>").Append(XmlEscape(e.Etag)).Append("</d:getetag>")
              .Append("<c:calendar-data>").Append(XmlEscape(e.Ics)).Append("</c:calendar-data>")
              .Append("</d:prop></d:propstat></d:response>");
        }

        sb.Append("<d:sync-token>").Append(XmlEscape(syncToken)).Append("</d:sync-token>");
        sb.Append("</d:multistatus>");
        return sb.ToString();
    }

    /// <summary>XML-escape text content; CR/LF as character references so XmlReader returns
    /// the exact \r\n line endings the iCalendar parser expects.</summary>
    private static string XmlEscape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\r", "&#xD;").Replace("\n", "&#xA;");

    private static string PathToUid(string path)
    {
        var trimmed = path.TrimEnd('/');
        var name = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        return name.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static void Respond(HttpListenerContext ctx, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    private sealed record FakeEvent(string Href, string Uid, string Etag, string Ics);
}
