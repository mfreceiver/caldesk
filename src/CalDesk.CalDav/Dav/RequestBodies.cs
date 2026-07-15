using System.Globalization;

namespace CalDesk.CalDav.Dav;

/// <summary>WebDAV/CalDAV request bodies (PROPFIND / REPORT).</summary>
public static class RequestBodies
{
    public const string Principal =
        """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:current-user-principal/></d:prop></d:propfind>""";

    public const string HomeSet =
        """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:prop><c:calendar-home-set/></d:prop></d:propfind>""";

    public const string CalendarProps =
        """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav" xmlns:cs="http://calendarserver.org/ns/" xmlns:ical="http://apple.com/ns/ical/"><d:prop><d:resourcetype/><d:displayname/><d:current-user-privilege-set/><cs:getctag/><d:sync-token/><ical:calendar-color/></d:prop></d:propfind>""";

    /// <summary>REPORT calendar-query body for VEVENTs in [start, end).</summary>
    public static string CalendarQuery(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        var s = FormatUtc(startUtc);
        var e = FormatUtc(endUtc);
        return
            $"""<?xml version="1.0" encoding="utf-8"?><c:calendar-query xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:prop><d:getetag/><c:calendar-data/></d:prop><c:filter><c:comp-filter name="VCALENDAR"><c:comp-filter name="VEVENT"><c:time-range start="{s}" end="{e}"/></c:comp-filter></c:comp-filter></c:filter></c:calendar-query>""";
    }

    /// <summary>REPORT sync-collection body. Pass empty token for initial sync.</summary>
    public static string SyncCollection(string? syncToken)
    {
        var token = string.IsNullOrEmpty(syncToken) ? string.Empty : syncToken;
        return
            $"""<?xml version="1.0" encoding="utf-8"?><d:sync-collection xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:sync-token>{System.Security.SecurityElement.Escape(token)}</d:sync-token><d:sync-level>1</d:sync-level><d:prop><d:getetag/><c:calendar-data/></d:prop></d:sync-collection>""";
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
}
