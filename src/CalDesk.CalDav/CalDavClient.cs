using System.Globalization;
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

    private readonly HttpClient _http;
    private readonly Uri _origin;

    public Account Account { get; }

    public CalDavClient(Account account, HttpClient http, Uri origin)
    {
        Account = account;
        _http = http;
        _origin = origin;
    }

    public async Task<IReadOnlyList<CalendarInfo>> DiscoverCalendarsAsync(CancellationToken ct = default)
    {
        var found = new List<CalendarInfo>();

        // 1. well-known -> principal -> calendar-home-set -> enumerate
        try
        {
            var principal = await PropfindSingleValueAsync("/.well-known/caldav", RequestBodies.Principal, "current-user-principal", 0, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(principal))
            {
                var home = await PropfindSingleValueAsync(principal, RequestBodies.HomeSet, "calendar-home-set", 0, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(home))
                {
                    found.AddRange(await EnumerateCalendarsAsync(home, ct).ConfigureAwait(false));
                }
            }
        }
        catch (Exception)
        {
            // discovery chain failed; try fallbacks
        }

        // 2. fallback: treat ServerUrl path as the calendar home and enumerate siblings
        if (found.Count == 0)
        {
            try
            {
                var path = EnsureTrailingSlash(new Uri(Account.ServerUrl).AbsolutePath);
                found.AddRange(await EnumerateCalendarsAsync(path, ct).ConfigureAwait(false));
            }
            catch (Exception)
            {
                // ignore
            }
        }

        // 3. fallback: ServerUrl is itself a single calendar
        if (found.Count == 0)
        {
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

        using var req = new HttpRequestMessage(new HttpMethod("PUT"), path)
        {
            Content = new StringContent(payload, Encoding.UTF8)
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("text/calendar") { CharSet = "utf-8" };
        if (ifMatchEtag is null)
        {
            req.Headers.TryAddWithoutValidation("If-None-Match", "*");
        }
        else
        {
            req.Headers.IfMatch.Add(new EntityTagHeaderValue(ifMatchEtag));
        }

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new CalDavException($"PUT {path} failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }

        var etag = resp.Headers.ETag?.Tag;
        return new PutResult(path, etag);
    }

    public async Task DeleteEventAsync(CalendarInfo calendar, string href, string ifMatchEtag, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, href);
        req.Headers.IfMatch.Add(new EntityTagHeaderValue(ifMatchEtag));

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            throw new CalDavException($"DELETE {href} failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");
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
            info.SyncToken = token;
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
        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (depth is not null)
        {
            req.Headers.TryAddWithoutValidation("Depth", depth);
        }
        if (body is not null)
        {
            req.Content = new StringContent(body, Encoding.UTF8);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml") { CharSet = "utf-8" };
            _ = contentType; // reserved for non-xml bodies
        }

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new CalDavException($"{method} {path} failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }

        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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

public sealed class CalDavException(string message) : Exception(message);
