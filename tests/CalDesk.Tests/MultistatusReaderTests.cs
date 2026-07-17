using CalDesk.CalDav.Calendar;
using CalDesk.Core.Models;
using Xunit;

namespace CalDesk.Tests;

public sealed class MultistatusReaderTests
{
    private const string DavNs = "DAV:";
    private const string CalNs = "urn:ietf:params:xml:ns:caldav";

    private static string Wrap(string innerResponses, string? syncToken = null)
    {
        var token = syncToken is null ? "" : $"<d:sync-token>{syncToken}</d:sync-token>";
        return
            $"""<?xml version="1.0" encoding="utf-8"?><d:multistatus xmlns:d="{DavNs}" xmlns:c="{CalNs}">{innerResponses}{token}</d:multistatus>""";
    }

    [Fact]
    public void ResponseLevel_404_Status_Is_Detected_As_Deletion()
    {
        // Sync-collection deletion marker: response-level <status>404</status>, separate from propstat.
        var xml = Wrap("""
            <d:response>
              <d:href>/user/work/abc.ics</d:href>
              <d:status>HTTP/1.1 404 Not Found</d:status>
              <d:propstat>
                <d:status>HTTP/1.1 404 Not Found</d:status>
                <d:prop/>
              </d:propstat>
            </d:response>
            """);

        var ms = CalDav.Dav.MultistatusReader.Read(xml);

        var r = Assert.Single(ms.Responses);
        Assert.Equal("/user/work/abc.ics", r.Href);
        Assert.True(r.IsNotFound);
    }

    [Fact]
    public void ResponseLevel_410_Status_Is_Detected_As_Deletion()
    {
        var xml = Wrap("""
            <d:response>
              <d:href>/user/work/gone.ics</d:href>
              <d:status>HTTP/1.1 410 Gone</d:status>
            </d:response>
            """);

        var ms = CalDav.Dav.MultistatusReader.Read(xml);

        Assert.True(Assert.Single(ms.Responses).IsNotFound);
    }

    [Fact]
    public void Propstat_200_Props_Are_Parsed_Normally()
    {
        var ics = "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n";
        var xml = Wrap(
            $"""
            <d:response>
              <d:href>/user/work/x.ics</d:href>
              <d:propstat>
                <d:status>HTTP/1.1 200 OK</d:status>
                <d:prop>
                  <d:getetag>"v1"</d:getetag>
                  <c:calendar-data>{System.Security.SecurityElement.Escape(ics)}</c:calendar-data>
                </d:prop>
              </d:propstat>
            </d:response>
            """);

        var ms = CalDav.Dav.MultistatusReader.Read(xml);

        var r = Assert.Single(ms.Responses);
        Assert.False(r.IsNotFound);
        Assert.Equal("\"v1\"", r.Props["getetag"]);
        Assert.Contains("BEGIN:VCALENDAR", r.Props["calendar-data"]);
    }

    [Fact]
    public void Propstat_404_Does_Not_Clobber_Propstat_200()
    {
        // Some servers return two propstats: one 200 with the calendar-data and one 404 for a
        // missing property (e.g. current-user-privilege-set). The 404 propstat must not erase
        // the values from the 200 propstat, and must not flip IsNotFound.
        var ics = "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n";
        var xml = Wrap(
            $"""
            <d:response>
              <d:href>/user/work/mixed.ics</d:href>
              <d:propstat>
                <d:status>HTTP/1.1 200 OK</d:status>
                <d:prop>
                  <d:getetag>"v2"</d:getetag>
                  <c:calendar-data>{System.Security.SecurityElement.Escape(ics)}</c:calendar-data>
                </d:prop>
              </d:propstat>
              <d:propstat>
                <d:status>HTTP/1.1 404 Not Found</d:status>
                <d:prop>
                  <cs:getctag xmlns:cs="http://calendarserver.org/ns/"/>
                </d:prop>
              </d:propstat>
            </d:response>
            """);

        var ms = CalDav.Dav.MultistatusReader.Read(xml);

        var r = Assert.Single(ms.Responses);
        Assert.False(r.IsNotFound);
        Assert.Equal("\"v2\"", r.Props["getetag"]);
        Assert.Contains("BEGIN:VCALENDAR", r.Props["calendar-data"]);
        // The ctag from the 404 propstat must NOT be present (would be empty / wrong).
        Assert.False(r.Props.ContainsKey("getctag"));
    }

    [Fact]
    public void SyncToken_Is_Parsed()
    {
        var xml = Wrap("<d:response><d:href>/x</d:href></d:response>", syncToken: "#abc/123");
        var ms = CalDav.Dav.MultistatusReader.Read(xml);
        Assert.Equal("#abc/123", ms.SyncToken);
    }
}
