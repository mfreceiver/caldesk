using System.Xml.Linq;

namespace CalDesk.CalDav.Dav;

/// <summary>A single DAV multistatus &lt;response&gt; element.</summary>
public sealed class DavResponse
{
    public required string Href { get; init; }

    /// <summary>Property values keyed by element local name (case-insensitive).</summary>
    public required IReadOnlyDictionary<string, string> Props { get; init; }

    public string? Status { get; init; }

    public bool IsNotFound => Status is not null && Status.Contains("404");
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
            string? status = null;
            var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var propstat in Descendants(resp, "propstat"))
            {
                var statusEl = Descendants(propstat, "status").FirstOrDefault();
                if (statusEl is not null)
                {
                    status = statusEl.Value;
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

            responses.Add(new DavResponse { Href = href, Props = props, Status = status });
        }

        var syncToken = Descendants(doc.Root, "sync-token").FirstOrDefault()?.Value;
        return new Multistatus(responses, syncToken);
    }

    public static bool IsCalendar(DavResponse r) =>
        r.Props.TryGetValue("resourcetype", out var rt) && rt.Split(' ').Contains("calendar");

    public static bool IsAddressbook(DavResponse r) =>
        r.Props.TryGetValue("resourcetype", out var rt) && rt.Split(' ').Contains("addressbook");

    private static IEnumerable<XElement> Descendants(XElement? el, string localName) =>
        el is null
            ? Enumerable.Empty<XElement>()
            : el.Descendants().Where(e => e.Name.LocalName == localName);
}
