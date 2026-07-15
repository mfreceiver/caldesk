using System.Globalization;
using System.Xml.Linq;

namespace CalDesk.CalDav.Dav;

/// <summary>A single DAV multistatus &lt;response&gt; element.</summary>
public sealed class DavResponse
{
    public required string Href { get; init; }

    /// <summary>Property values keyed by element local name (case-insensitive).
    /// Only properties from <c>2xx</c> propstats are included.</summary>
    public required IReadOnlyDictionary<string, string> Props { get; init; }

    /// <summary>The response-level <c>&lt;d:status&gt;</c> value (direct child of
    /// <c>&lt;d:response&gt;</c>, sibling of <c>&lt;d:propstat&gt;</c>). Null when absent.
    /// In a sync-collection reply, a deleted resource is reported here (e.g.
    /// <c>HTTP/1.1 404 Not Found</c>), not inside <c>propstat</c>.</summary>
    public string? Status { get; init; }

    /// <summary>True when the response-level status indicates the resource no longer exists
    /// (HTTP 404 Not Found or 410 Gone) — i.e. it should be treated as a remote deletion.</summary>
    public bool IsNotFound => StatusMatches(Status, 404) || StatusMatches(Status, 410);

    private static bool StatusMatches(string? status, int code)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        // Per RFC 7230 the status line is "<HTTP-version> <code> <reason>", so we split and
        // look for a standalone integer token. This avoids false-positives from version
        // strings like "HTTP/1.1" (which contains "1") or reason phrases mentioning numbers.
        foreach (var token in status.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, CultureInfo.InvariantCulture, out var c) && c == code)
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>Parsed DAV multistatus body.</summary>
public sealed record Multistatus(IReadOnlyList<DavResponse> Responses, string? SyncToken);

public static class MultistatusReader
{
    public static Multistatus Read(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return new Multistatus(Array.Empty<DavResponse>(), null);
        }

        var doc = XDocument.Parse(xml);
        var responses = new List<DavResponse>();

        foreach (var resp in Descendants(doc.Root, "response"))
        {
            var href = Descendants(resp, "href").FirstOrDefault()?.Value ?? string.Empty;

            // Response-level status is a *direct child* of <response>, separate from <propstat>.
            // (RFC 4918 §13.1.4 — used by sync-collection to mark deletions: HTTP/1.1 404.)
            var responseStatus = resp.Elements().FirstOrDefault(e => e.Name.LocalName == "status")?.Value;

            var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var propstat in Descendants(resp, "propstat"))
            {
                var statusEl = Descendants(propstat, "status").FirstOrDefault();
                // Only accept properties from propstats whose status is 2xx. A 404/410 propstat
                // means "this property is unknown on this resource" — using its (empty) values
                // would silently clobber good values from a sibling 200 propstat (RFC 4918 §13.1).
                if (statusEl is not null && !IsSuccessStatus(statusEl.Value))
                {
                    continue;
                }

                var propEl = Descendants(propstat, "prop").FirstOrDefault();
                if (propEl is null)
                {
                    continue;
                }

                foreach (var p in propEl.Elements())
                {
                    var name = p.Name.LocalName;
                    if (name == "resourcetype")
                    {
                        props[name] = string.Join(" ", p.Elements().Select(e => e.Name.LocalName));
                    }
                    else
                    {
                        props[name] = p.Value;
                    }
                }
            }

            responses.Add(new DavResponse { Href = href, Props = props, Status = responseStatus });
        }

        var syncToken = Descendants(doc.Root, "sync-token").FirstOrDefault()?.Value;
        return new Multistatus(responses, syncToken);
    }

    public static bool IsCalendar(DavResponse r) =>
        r.Props.TryGetValue("resourcetype", out var rt) && rt.Split(' ').Contains("calendar");

    public static bool IsAddressbook(DavResponse r) =>
        r.Props.TryGetValue("resourcetype", out var rt) && rt.Split(' ').Contains("addressbook");

    /// <summary>True for any 2xx status line. A null/empty status is treated as success to stay
    /// lenient with servers that omit it inside propstat.</summary>
    private static bool IsSuccessStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return true;
        }

        foreach (var token in status.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, CultureInfo.InvariantCulture, out var code)
                && code >= 200 && code < 300)
            {
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<XElement> Descendants(XElement? el, string localName) =>
        el is null
            ? Enumerable.Empty<XElement>()
            : el.Descendants().Where(e => e.Name.LocalName == localName);
}
