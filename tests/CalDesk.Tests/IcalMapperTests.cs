using CalDesk.CalDav.Calendar;
using CalDesk.Core.Models;
using Xunit;

namespace CalDesk.Tests;

public sealed class IcalMapperTests
{
    private const string VCalendarHead = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n";
    private const string VCalendarTail = "END:VCALENDAR\r\n";

    // ---- Issue 4: DURATION fallback ----------------------------------------

    [Fact]
    public void Duration_Fallback_Yields_StartPlusDuration_When_DtEnd_Absent()
    {
        var ics = VCalendarHead +
                  "BEGIN:VEVENT\r\n" +
                  "UID:dur-1\r\n" +
                  "DTSTART:20260715T100000Z\r\n" +
                  "DURATION:PT90M\r\n" +
                  "SUMMARY:90-minute meeting\r\n" +
                  "END:VEVENT\r\n" +
                  VCalendarTail;

        var parsed = IcalMapper.ToCalendarEvents(ics, Guid.NewGuid(), "/x.ics", "\"v1\"");

        var evt = Assert.Single(parsed);
        var expectedStart = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(expectedStart, evt.StartUtc);
        Assert.Equal(expectedStart.AddMinutes(90), evt.EndUtc);
    }

    [Fact]
    public void No_DtEnd_No_Duration_Timed_Defaults_To_ZeroLength()
    {
        // RFC 5545 §3.6.1: a timed VEVENT without DTEND/DURATION is zero-length (EndUtc == StartUtc).
        var ics = VCalendarHead +
                  "BEGIN:VEVENT\r\n" +
                  "UID:zero\r\n" +
                  "DTSTART:20260715T140000Z\r\n" +
                  "SUMMARY:Instant\r\n" +
                  "END:VEVENT\r\n" +
                  VCalendarTail;

        var parsed = IcalMapper.ToCalendarEvents(ics, Guid.NewGuid(), null, null);

        var evt = Assert.Single(parsed);
        Assert.False(evt.IsAllDay);
        Assert.Equal(evt.StartUtc, evt.EndUtc);
    }

    [Fact]
    public void No_DtEnd_No_Duration_AllDay_Defaults_To_NextDay()
    {
        // RFC 5545 §3.6.1: a DATE-valued (all-day) VEVENT without DTEND/DURATION spans one day.
        var ics = VCalendarHead +
                  "BEGIN:VEVENT\r\n" +
                  "UID:allday\r\n" +
                  "DTSTART;VALUE=DATE:20260715\r\n" +
                  "SUMMARY:All-day\r\n" +
                  "END:VEVENT\r\n" +
                  VCalendarTail;

        var parsed = IcalMapper.ToCalendarEvents(ics, Guid.NewGuid(), null, null);

        var evt = Assert.Single(parsed);
        Assert.True(evt.IsAllDay);
        var expectedStart = new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(expectedStart.AddDays(1), evt.EndUtc);
    }

    [Fact]
    public void Explicit_DtEnd_Still_Wins_Over_Duration()
    {
        var ics = VCalendarHead +
                  "BEGIN:VEVENT\r\n" +
                  "UID:both\r\n" +
                  "DTSTART:20260715T100000Z\r\n" +
                  "DTEND:20260715T110000Z\r\n" +
                  "DURATION:PT5H\r\n" +
                  "END:VEVENT\r\n" +
                  VCalendarTail;

        var parsed = IcalMapper.ToCalendarEvents(ics, Guid.NewGuid(), null, null);

        var evt = Assert.Single(parsed);
        // DTEND wins (1-hour event), DURATION is ignored.
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero), evt.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 11, 0, 0, TimeSpan.Zero), evt.EndUtc);
    }

    // ---- Issue 5: Parse-failure quarantine ---------------------------------

    [Fact]
    public void TryToCalendarEvents_Malformed_VEVENT_Reports_Error_Not_Empty()
    {
        // Truly unparseable body — Ical.Net throws SerializationException. The old behavior was
        // to swallow the exception and return an empty list, which let sync advance its token
        // past the lost resource.
        var malformed = "this is not even an iCalendar object";

        var result = IcalMapper.TryToCalendarEvents(malformed, Guid.NewGuid(), "/x/broken.ics", "\"e1\"");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Events);
        Assert.Equal("/x/broken.ics", result.Href);
        Assert.Equal("\"e1\"", result.ETag);
    }

    [Fact]
    public void TryToCalendarEvents_Valid_VEVENT_Reports_Success_With_Events()
    {
        var ics = VCalendarHead +
                  "BEGIN:VEVENT\r\n" +
                  "UID:ok-1\r\n" +
                  "DTSTART:20260715T100000Z\r\n" +
                  "DTEND:20260715T110000Z\r\n" +
                  "SUMMARY:OK\r\n" +
                  "END:VEVENT\r\n" +
                  VCalendarTail;

        var result = IcalMapper.TryToCalendarEvents(ics, Guid.NewGuid(), "/x/ok.ics", "\"e2\"");

        Assert.True(result.Success);
        Assert.Null(result.Error);
        var evt = Assert.Single(result.Events);
        Assert.Equal("ok-1", evt.Uid);
        Assert.Equal("/x/ok.ics", evt.RemoteHref);
        Assert.Equal("\"e2\"", evt.ETag);
    }

    [Fact]
    public void ToCalendarEvents_Legacy_Method_Still_Returns_Empty_For_Malformed()
    {
        // Backward-compat: the legacy method keeps returning an empty list (it must not throw).
        var malformed = "this is not even an iCalendar object";

        var list = IcalMapper.ToCalendarEvents(malformed, Guid.NewGuid(), null, null);
        Assert.Empty(list);
    }
}
