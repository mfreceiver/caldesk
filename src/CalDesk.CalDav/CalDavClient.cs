using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CalDesk.CalDav.Calendar;
using CalDesk.CalDav.Dav;
using CalDesk.Core.Models;
using CalDesk.Core.Services;

namespace CalDesk.CalDav;

/// <summary>CalDAV client over HTTP/WebDAV (RFC 4791 subset) for a single account.</summary>
public sealed class CalDavClient : ICalDavClient
{
    private const string CalendarContentType = "text/calendar; charset=utf-8";
    private const string XmlContentType = "application/xml; charset=utf-8";
    private const int MaxRedirects = 5;

    private readonly HttpClient _http;
    private readonly Uri _origin;
    private readonly Action<string>? _log;

    public Account Account { get; }

    public CalDavClient(Account account, HttpClient http, Uri origin, Action<string>? log = null)
    {
        Account = account;
        _http = http;
        _origin = origin;
        _log = log;
    }

    public async Task<IReadOnlyList<CalendarInfo>> DiscoverCalendarsAsync(CancellationToken ct = default)
    {
        var found = new List<CalendarInfo>();

        // 1. well-known -> principal -> calendar-home-set -> enumerate
        try
        {
            Log("discovery: PROPFIND well-known /.well-known/caldav (current-user-principal)");
            var principal = await PropfindSingleValueAsync("/.well-known/caldav", RequestBodies.Principal, "current-user-principal", 0, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(principal))
            {
                Log($"discovery: principal={principal}");
                var home = await PropfindSingleValueAsync(principal, RequestBodies.HomeSet, "calendar-home-set", 0, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(home))
                {
                    Log($"discovery: home-set={home}");
                    var listed = await EnumerateCalendarsAsync(home, ct).ConfigureAwait(false);
                    Log($"discovery: enumerate home-set count={listed.Count}");
                    found.AddRange(listed);
                }
                else
                {
                    Log("discovery: home-set empty after principal");
                }
            }
            else
            {
                Log("discovery: well-known yielded no principal");
            }
        }
        catch (Exception ex)
        {
            // Only rethrow when the *caller* cancelled. HttpClient.Timeout also surfaces as
            // OperationCanceledException but must allow root/path fallbacks to run.
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                throw;
            }

            Log($"discovery: well-known chain failed: {ex.Message}");
            // discovery chain failed; try fallbacks
        }

        // 2. fallback: PROPFIND current-user-principal on ServerUrl / origin root
        if (found.Count == 0)
        {
            try
            {
                var rootPath = "/";
                Log($"discovery: fallback root principal PROPFIND {rootPath}");
                var principal = await PropfindSingleValueAsync(rootPath, RequestBodies.Principal, "current-user-principal", 0, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(principal))
                {
                    Log($"discovery: root principal={principal}");
                    var home = await PropfindSingleValueAsync(principal, RequestBodies.HomeSet, "calendar-home-set", 0, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(home))
                    {
                        Log($"discovery: root fallback home-set={home}");
                        var listed = await EnumerateCalendarsAsync(home, ct).ConfigureAwait(false);
                        Log($"discovery: enumerate root fallback count={listed.Count}");
                        found.AddRange(listed);
                    }
                }
                else
                {
                    Log("discovery: root principal empty");
                }
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException && ct.IsCancellationRequested)
                {
                    throw;
                }

                Log($"discovery: root principal fallback failed: {ex.Message}");
            }
        }

        // 3. fallback: treat ServerUrl path as the calendar home and enumerate siblings
        if (found.Count == 0)
        {
            try
            {
                var path = EnsureTrailingSlash(new Uri(Account.ServerUrl).AbsolutePath);
                Log($"discovery: fallback enumerate ServerUrl path={path}");
                var listed = await EnumerateCalendarsAsync(path, ct).ConfigureAwait(false);
                Log($"discovery: enumerate ServerUrl path count={listed.Count}");
                found.AddRange(listed);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException && ct.IsCancellationRequested)
                {
                    throw;
                }

                Log($"discovery: ServerUrl path fallback failed: {ex.Message}");
            }
        }

        // 4. fallback: ServerUrl is itself a single calendar
        if (found.Count == 0)
        {
            Log("discovery: fallback try ServerUrl as single calendar");
            var single = await TryAsSingleCalendarAsync(EnsureTrailingSlash(new Uri(Account.ServerUrl).AbsolutePath), ct).ConfigureAwait(false);
            if (single is not null)
            {
                found.Add(single);
            }
        }

        foreach (var c in found)
        {
            c.AccountId = Account.Id;
        }

        Log($"discovery: done count={found.Count}");
        return found;
    }

    public async Task<CalendarQueryResult> QueryEventsAsync(CalendarInfo calendar, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken ct = default)
    {
        var path = ToPath(calendar.RemoteUrl);
        var body = RequestBodies.CalendarQuery(startUtc, endUtc);
        var xml = await SendAsync("REPORT", path, body, XmlContentType, depth: "1", ct: ct).ConfigureAwait(false);
        var ms = MultistatusReader.Read(xml);

        var entries = ms.Responses
            .Where(r => !r.IsNotFound)
            .Select(r => new RemoteEventEntry(r.Href, NormalizeEtag(r.Props.GetValueOrDefault("getetag")), r.Props.GetValueOrDefault("calendar-data") ?? string.Empty))
            .Where(e => !string.IsNullOrEmpty(e.ICalendarData))
            .ToList();

        return new CalendarQueryResult(entries);
    }

    public async Task<PutResult> PutEventAsync(CalendarInfo calendar, Core.Models.CalendarEvent evt, string? ifMatchEtag, CancellationToken ct = default)
    {
        var payload = IcalMapper.Serialize(evt);
        var filename = $"{UidToFilename(evt.Uid)}.ics";
        var path = CombinePath(calendar.RemoteUrl, filename);

        using var resp = await SendRawAsync(
            "PUT",
            path,
            payload,
            "text/calendar",
            configure: req =>
            {
                if (ifMatchEtag is null)
                {
                    req.Headers.TryAddWithoutValidation("If-None-Match", "*");
                }
                else
                {
                    req.Headers.IfMatch.Add(new EntityTagHeaderValue(ifMatchEtag));
                }
            },
            ct: ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            throw new CalDavException(
                $"PUT {path} failed: {(int)resp.StatusCode} {resp.ReasonPhrase}",
                (int)resp.StatusCode);
        }

        var etag = resp.Headers.ETag?.Tag;
        return new PutResult(path, etag);
    }

    public async Task DeleteEventAsync(CalendarInfo calendar, string href, string ifMatchEtag, CancellationToken ct = default)
    {
        using var resp = await SendRawAsync(
            "DELETE",
            href,
            body: null,
            mediaType: null,
            configure: req => req.Headers.IfMatch.Add(new EntityTagHeaderValue(ifMatchEtag)),
            ct: ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.NotFound)
        {
            throw new CalDavException(
                $"DELETE {href} failed: {(int)resp.StatusCode} {resp.ReasonPhrase}",
                (int)resp.StatusCode);
        }
    }

    public async Task<SyncCollectionResult> SyncCollectionAsync(CalendarInfo calendar, string? syncToken, CancellationToken ct = default)
    {
        var path = ToPath(calendar.RemoteUrl);
        var body = RequestBodies.SyncCollection(syncToken);

        string xml;
        try
        {
            xml = await SendAsync("REPORT", path, body, XmlContentType, depth: "1", ct: ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return new SyncCollectionResult(Array.Empty<RemoteEventEntry>(), Array.Empty<string>(), null, Supported: false);
        }

        var ms = MultistatusReader.Read(xml);
        var changed = ms.Responses
            .Where(r => !r.IsNotFound && !string.IsNullOrEmpty(r.Props.GetValueOrDefault("calendar-data")))
            .Select(r => new RemoteEventEntry(r.Href, NormalizeEtag(r.Props.GetValueOrDefault("getetag")), r.Props.GetValueOrDefault("calendar-data") ?? string.Empty))
            .ToList();
        var deleted = ms.Responses.Where(r => r.IsNotFound).Select(r => r.Href).ToList();

        return new SyncCollectionResult(changed, deleted, ms.SyncToken, Supported: true);
    }

    public async Task<CalendarInfo> RefreshCalendarAsync(CalendarInfo calendar, CancellationToken ct = default)
    {
        var path = ToPath(calendar.RemoteUrl);
        var xml = await SendAsync("PROPFIND", path, RequestBodies.CalendarProps, XmlContentType, depth: "0", ct: ct).ConfigureAwait(false);
        var ms = MultistatusReader.Read(xml);
        var r = ms.Responses.FirstOrDefault();
        if (r is not null)
        {
            ApplyCalendarProps(calendar, r);
        }
        return calendar;
    }

    // ---- discovery helpers -------------------------------------------------

    private async Task<IReadOnlyList<CalendarInfo>> EnumerateCalendarsAsync(string homePath, CancellationToken ct)
    {
        var xml = await SendAsync("PROPFIND", homePath, RequestBodies.CalendarProps, XmlContentType, depth: "1", ct: ct).ConfigureAwait(false);
        var ms = MultistatusReader.Read(xml);

        var calendars = new List<CalendarInfo>();
        foreach (var r in ms.Responses)
        {
            if (!MultistatusReader.IsCalendar(r) || MultistatusReader.IsAddressbook(r))
            {
                continue;
            }

            var info = new CalendarInfo { AccountId = Account.Id, RemoteUrl = Absolute(r.Href) };
            ApplyCalendarProps(info, r);
            calendars.Add(info);
        }

        return calendars;
    }

    private async Task<CalendarInfo?> TryAsSingleCalendarAsync(string path, CancellationToken ct)
    {
        var xml = await SendAsync("PROPFIND", path, RequestBodies.CalendarProps, XmlContentType, depth: "0", ct: ct).ConfigureAwait(false);
        var ms = MultistatusReader.Read(xml);
        var r = ms.Responses.FirstOrDefault();
        if (r is null || !MultistatusReader.IsCalendar(r))
        {
            return null;
        }

        var info = new CalendarInfo { AccountId = Account.Id, RemoteUrl = Absolute(r.Href) };
        ApplyCalendarProps(info, r);
        return info;
    }

    private static void ApplyCalendarProps(CalendarInfo info, DavResponse r)
    {
        if (r.Props.TryGetValue("displayname", out var name) && !string.IsNullOrWhiteSpace(name))
        {
            info.DisplayName = name;
        }
        if (r.Props.TryGetValue("calendar-color", out var color))
        {
            info.Color = color;
        }
        if (r.Props.TryGetValue("getctag", out var ctag))
        {
            info.Ctag = ctag;
        }
        if (r.Props.TryGetValue("sync-token", out var token) && !string.IsNullOrEmpty(token))
        {
            // The server advertises sync-collection support. The actual baseline token is
            // obtained by the sync engine on first sync — not reused here, since the local
            // cache is empty at discovery time and reusing it would skip all existing events.
            info.SupportsSyncToken = true;
        }
    }

    private async Task<string?> PropfindSingleValueAsync(string path, string body, string propName, int depth, CancellationToken ct)
    {
        var xml = await SendAsync("PROPFIND", path, body, XmlContentType, depth: depth.ToString(CultureInfo.InvariantCulture), ct: ct).ConfigureAwait(false);
        var ms = MultistatusReader.Read(xml);
        return ms.Responses.FirstOrDefault()?.Props.GetValueOrDefault(propName);
    }

    // ---- low-level HTTP ----------------------------------------------------

    private async Task<string> SendAsync(string method, string path, string? body, string contentType, string? depth, CancellationToken ct)
    {
        using var resp = await SendRawAsync(
            method,
            path,
            body,
            mediaType: contentType.StartsWith("text/calendar", StringComparison.OrdinalIgnoreCase) ? "text/calendar" : "application/xml",
            configure: req =>
            {
                if (depth is not null)
                {
                    req.Headers.TryAddWithoutValidation("Depth", depth);
                }
            },
            ct: ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var precondition = ExtractPrecondition(resp);
            throw new CalDavException(
                $"{method} {path} failed: {(int)resp.StatusCode} {resp.ReasonPhrase}",
                (int)resp.StatusCode,
                precondition);
        }

        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a request with restricted manual redirect following (<c>AllowAutoRedirect=false</c>).
    /// Policy (security + CalDAV):
    /// <list type="bullet">
    /// <item>Every hop — including the initial request URI — must be relative or same-origin
    /// (scheme+host+port match <see cref="_origin"/>). Absolute foreign targets are refused
    /// before any send (covers stored multistatus <c>RemoteHref</c> / open-redirect payloads).</item>
    /// <item>Same-origin redirect Location only; cross-origin Location refused — never re-issue
    /// PUT/DELETE/REPORT/PROPFIND body to a foreign host.</item>
    /// <item>HTTPS→HTTP downgrade is refused via the scheme check above.</item>
    /// <item>PROPFIND/REPORT: re-issue same method+body on 301/302/303/307/308 (CalDAV needs
    /// method preservation; .NET auto-redirect would rewrite PROPFIND→GET).</item>
    /// <item>PUT/DELETE: follow only 307/308 same-origin (method already correct). Do not follow
    /// 301/302/303 with body re-PUT (open-redirect / method-change risk).</item>
    /// </list>
    /// <see cref="HttpClient.DefaultRequestHeaders"/> Authorization is never mutated; relative
    /// paths use BaseAddress + stable default auth.
    /// </summary>
    private async Task<HttpResponseMessage> SendRawAsync(
        string method,
        string path,
        string? body,
        string? mediaType,
        Action<HttpRequestMessage>? configure,
        CancellationToken ct)
    {
        // Only relative / same-origin targets are issued — gate *before* first hop so foreign
        // absolute hrefs never leave with DefaultRequestHeaders Authorization. Absolute
        // same-origin URLs are rewritten to path+query so BaseAddress + default auth apply.
        var current = NormalizeRequestTarget(method, path);
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            using var req = new HttpRequestMessage(new HttpMethod(method), current);
            if (body is not null)
            {
                req.Content = new StringContent(body, Encoding.UTF8);
                if (!string.IsNullOrEmpty(mediaType))
                {
                    req.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType) { CharSet = "utf-8" };
                }
            }

            configure?.Invoke(req);
            Log($"{method} {current}");

            var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

            var code = (int)resp.StatusCode;
            Log($"{method} {current} -> {code}");

            if (IsRedirect(resp.StatusCode) && resp.Headers.Location is { } location)
            {
                if (!ShouldFollowRedirect(method, resp.StatusCode))
                {
                    // e.g. PUT/DELETE on 301/302/303: do not re-issue body; surface 3xx to caller.
                    Log($"{method} not following {code} (method policy); Location={location}");
                    return resp;
                }

                Uri resolved;
                try
                {
                    resolved = ResolveRedirectUri(current, location);
                }
                catch (UriFormatException ex)
                {
                    resp.Dispose();
                    throw new CalDavException($"{method} {path} invalid redirect Location={location}: {ex.Message}", code);
                }

                // Only http(s) same-origin hops. file:// (Unix absolute path mis-parse) and
                // any foreign host are refused — never re-issue method+body off-origin.
                if (!IsHttpOrHttps(resolved) || !IsSameOriginUri(resolved))
                {
                    Log($"{method} refused cross-origin redirect Location={resolved}");
                    resp.Dispose();
                    var where = IsHttpOrHttps(resolved)
                        ? resolved.GetLeftPart(UriPartial.Authority)
                        : resolved.ToString();
                    throw new CalDavException(
                        $"{method} {path} refused cross-origin redirect to {where}",
                        code);
                }

                // Same-origin: path-relative so BaseAddress + default Authorization keep applying.
                var next = resolved.PathAndQuery;
                Log($"{method} redirect Location={location} -> {next}");
                resp.Dispose();
                if (hop == MaxRedirects)
                {
                    throw new CalDavException(
                        $"{method} {path} exceeded {MaxRedirects} redirects (last: {next})",
                        code);
                }

                current = next;
                continue;
            }

            return resp;
        }

        // Unreachable — loop always returns or throws.
        throw new CalDavException($"{method} {path} redirect loop exhausted", null);
    }

    /// <summary>
    /// PROPFIND/REPORT: follow all redirect statuses with method+body preserved.
    /// PUT/DELETE: only 307/308 (already method-preserving); never re-PUT on 301/302/303.
    /// </summary>
    private static bool ShouldFollowRedirect(string method, HttpStatusCode status)
    {
        if (status is HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect
            or (HttpStatusCode)308)
        {
            return true;
        }

        // 301 / 302 / 303 — only discovery-style methods re-issue with same method+body.
        return IsDiscoveryStyleMethod(method);
    }

    private static bool IsDiscoveryStyleMethod(string method) =>
        method.Equals("PROPFIND", StringComparison.OrdinalIgnoreCase)
        || method.Equals("REPORT", StringComparison.OrdinalIgnoreCase);

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect
            or (HttpStatusCode)308;

    private Uri ResolveRedirectUri(string currentRequest, Uri location)
    {
        Uri baseUri;
        if (Uri.TryCreate(currentRequest, UriKind.Absolute, out var absoluteCurrent)
            && IsHttpOrHttps(absoluteCurrent))
        {
            baseUri = absoluteCurrent;
        }
        else
        {
            baseUri = new Uri(_origin, currentRequest.TrimStart('/'));
        }

        // Absolute http(s) Location is used as-is. Relative (or non-http absolute such as
        // file:///path from UriKind.RelativeOrAbsolute on Unix) is resolved against base.
        if (location.IsAbsoluteUri && IsHttpOrHttps(location))
        {
            return location;
        }

        var relative = location.IsAbsoluteUri
            ? location.AbsolutePath + location.Query
            : location.OriginalString;
        return new Uri(baseUri, relative);
    }

    /// <summary>
    /// Resolve <paramref name="path"/> against <see cref="_origin"/> and refuse any target
    /// that is not same-origin. Returns path+query (relative to BaseAddress) so default auth
    /// stays attached. Never returns an absolute foreign URI.
    /// </summary>
    private string NormalizeRequestTarget(string method, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new CalDavException($"{method} refused empty request target");
        }

        // Network-path / protocol-relative (RFC 3986 §4.2): "//evil.example/x".
        // StartsWith('/') alone would treat this as root-relative and return unchanged;
        // BaseAddress then resolves it to a foreign host with Default Authorization attached.
        if (path.StartsWith("//", StringComparison.Ordinal))
        {
            Log($"{method} refused network-path request target={path}");
            throw new CalDavException($"{method} refused network-path (protocol-relative) request target {path}");
        }

        // Absolute http(s) only when a scheme is present. On Unix, UriKind.Absolute turns
        // root paths like "/" into file:/// — treat leading-slash strings as path-relative.
        if (LooksLikeHttpOrHttpsAbsolute(path)
            && Uri.TryCreate(path, UriKind.Absolute, out var absolute)
            && absolute.IsAbsoluteUri
            && IsHttpOrHttps(absolute))
        {
            if (!IsSameOriginUri(absolute))
            {
                Log($"{method} refused cross-origin request target={absolute}");
                throw new CalDavException(
                    $"{method} refused cross-origin request to {absolute.GetLeftPart(UriPartial.Authority)}");
            }

            // Same-origin absolute → path+query so BaseAddress + default Authorization apply.
            return absolute.PathAndQuery;
        }

        // Explicit non-http absolute (e.g. file://, data:) — refuse.
        if (LooksLikeSchemeAbsolute(path)
            && Uri.TryCreate(path, UriKind.Absolute, out var nonHttp)
            && nonHttp.IsAbsoluteUri
            && !IsHttpOrHttps(nonHttp))
        {
            Log($"{method} refused non-http(s) request target={path}");
            throw new CalDavException($"{method} refused non-http(s) request target {nonHttp}");
        }

        // Single-slash root path (e.g. "/calendars/…") — same origin via BaseAddress.
        // Network-path "//…" already refused above.
        if (path.StartsWith('/'))
        {
            return path;
        }

        // Non-slash relative: resolve against origin and refuse if host/scheme/port would change
        // (defensive — rare forms that Uri would interpret as authority-bearing).
        try
        {
            var resolved = new Uri(_origin, path);
            if (!IsHttpOrHttps(resolved) || !IsSameOriginUri(resolved))
            {
                Log($"{method} refused cross-origin relative target={path} -> {resolved}");
                var where = IsHttpOrHttps(resolved)
                    ? resolved.GetLeftPart(UriPartial.Authority)
                    : resolved.ToString();
                throw new CalDavException($"{method} refused cross-origin request to {where}");
            }

            return resolved.PathAndQuery;
        }
        catch (UriFormatException ex)
        {
            throw new CalDavException($"{method} invalid request target={path}: {ex.Message}");
        }
    }

    private static bool LooksLikeHttpOrHttpsAbsolute(string path) =>
        path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the string begins with a URI scheme (e.g. <c>file:</c>, <c>data:</c>).</summary>
    private static bool LooksLikeSchemeAbsolute(string path)
    {
        var colon = path.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        // Avoid treating "C:\…" style or bare path segments as schemes; require scheme chars only.
        for (var i = 0; i < colon; i++)
        {
            var ch = path[i];
            if (!(char.IsLetterOrDigit(ch) || ch is '+' or '-' or '.'))
            {
                return false;
            }
        }

        // Path-only absolute paths on Unix start with '/' — not a scheme.
        return path[0] != '/';
    }

    private static bool IsHttpOrHttps(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private bool IsSameOriginUri(Uri uri) =>
        string.Equals(uri.Scheme, _origin.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, _origin.Host, StringComparison.OrdinalIgnoreCase)
        && uri.Port == _origin.Port;

    /// <summary>Pull the DAV precondition token from a 412 response (e.g.
    /// <c>Precondition: valid-sync-token</c>), if present.</summary>
    private static string? ExtractPrecondition(HttpResponseMessage resp)
    {
        foreach (var value in resp.Headers.Where(h => string.Equals(h.Key, "Precondition", StringComparison.OrdinalIgnoreCase))
                                          .SelectMany(h => h.Value))
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }
        return null;
    }

    private void Log(string message)
    {
        if (_log is null)
        {
            return;
        }

        try
        {
            _log(message);
        }
        catch
        {
            // Discovery logging must never break the client.
        }
    }

    // ---- path helpers ------------------------------------------------------

    private string Absolute(string href) => CalDavClientFactory.Combine(_origin, href).AbsoluteUri;

    private static string ToPath(string remoteUrl)
    {
        var u = new Uri(remoteUrl);
        return EnsureTrailingSlash(u.AbsolutePath);
    }

    private static string CombinePath(string remoteUrl, string filename)
    {
        var base0 = EnsureTrailingSlash(new Uri(remoteUrl).AbsolutePath);
        return base0 + filename;
    }

    private static string EnsureTrailingSlash(string path) => path.EndsWith('/') ? path : path + "/";

    private static string UidToFilename(string uid)
    {
        var sb = new StringBuilder();
        foreach (var ch in uid)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        }
        return sb.ToString();
    }

    private static string? NormalizeEtag(string? etag) => string.IsNullOrWhiteSpace(etag) ? null : etag.Trim();

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class CalDavException : Exception
{
    /// <summary>HTTP status code associated with the failure, when known. Used by callers
    /// (e.g. <see cref="Synchronization.SyncEngine"/>) to distinguish a rejected sync-token
    /// (403/409) from transient/network failures without parsing <see cref="Exception.Message"/>.</summary>
    public int? StatusCode { get; }

    /// <summary>DAV precondition token (e.g. <c>valid-sync-token</c>) when the server reported
    /// one via the <c>Precondition</c> header in a 412 response. Null when not applicable.</summary>
    public string? Precondition { get; }

    public CalDavException(string message) : base(message) { }

    public CalDavException(string message, int? statusCode, string? precondition = null) : base(message)
    {
        StatusCode = statusCode;
        Precondition = precondition;
    }
}
