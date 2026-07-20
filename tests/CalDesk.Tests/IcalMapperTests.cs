using CalDesk.CalDav.Calendar;
using CalDesk.Core;
using CalDesk.Core.Models;
using Xunit;

namespace CalDesk.Tests;

public sealed class IcalMapperTests
{
    private const string VCalendarHead = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n";
    private const string VCalendarTail = "END:VCALENDAR\r\n";

    [Fact]
    public void Tzid_DtStart_Converts_WallClock_To_Real_Utc()
    {
        var ics = VCalendarHead +
                  "BEGIN:VTIMEZONE\r\n" +
                  "TZID:Asia/Shanghai\r\n" +
                  "BEGIN:STANDARD\r\n" +
                  "DTSTART:19700101T000000\r\n" +
                  "TZOFFSETFROM:+0800\r\n" +
                  "TZOFFSETTO:+0800\r\n" +
                  "END:STANDARD\r\n" +
                  "END:VTIMEZONE\r\n" +
                  "BEGIN:VEVENT\r\n" +
                  "UID:tz-1\r\n" +
                  "DTSTART;TZID=Asia/Shanghai:20260715T080000\r\n" +
                  "DTEND;TZID=Asia/Shanghai:20260715T090000\r\n" +
                  "SUMMARY:Shanghai 8am\r\n" +
                  "END:VEVENT\r\n" +
                  VCalendarTail;

        var evt = Assert.Single(IcalMapper.ToCalendarEvents(ics, Guid.NewGuid(), null, null));

        Assert.Equal(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero), evt.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 1, 0, 0, TimeSpan.Zero), evt.EndUtc);
    }

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
        // DATE maps to local midnight (TimeZoneInfo.Local), exclusive end = next local midnight.
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
        var expectedStart = AllDayDate.ToUtcInstant(new DateOnly(2026, 7, 15), TimeZoneInfo.Local);
        var expectedEnd = AllDayDate.ToUtcInstant(new DateOnly(2026, 7, 16), TimeZoneInfo.Local);
        Assert.Equal(expectedStart, evt.StartUtc);
        Assert.Equal(expectedEnd, evt.EndUtc);
    }

    [Fact]
    public void AllDay_Serialize_Uses_Local_Date_Not_Utc_Date()
    {
        // +08 local midnight 7/15 = 7/14 16:00Z. Emitting utc.Date would corrupt to 20260714.
        var start = AllDayDate.ToUtcInstant(new DateOnly(2026, 7, 15), TimeZoneInfo.Local);
        var end = AllDayDate.ToUtcInstant(new DateOnly(2026, 7, 16), TimeZoneInfo.Local);
        var evt = new CalendarEvent
        {
            Uid = "allday-local-date",
            Summary = "Local DATE",
            IsAllDay = true,
            StartUtc = start,
            EndUtc = end,
        };

        var ics = IcalMapper.Serialize(evt);

        Assert.Contains("DTSTART;VALUE=DATE:20260715", ics);
        Assert.Contains("DTEND;VALUE=DATE:20260716", ics);
        Assert.DoesNotContain("DTSTART;VALUE=DATE:20260714", ics);
    }

    [Fact]
    public void AllDay_RoundTrip_Serialize_Parse_Preserves_Local_Date()
    {
        var startDate = new DateOnly(2026, 7, 15);
        var endExclusive = startDate.AddDays(1);
        var evt = new CalendarEvent
        {
            Uid = "allday-rt",
            Summary = "Round trip",
            IsAllDay = true,
            StartUtc = AllDayDate.ToUtcInstant(startDate, TimeZoneInfo.Local),
            EndUtc = AllDayDate.ToUtcInstant(endExclusive, TimeZoneInfo.Local),
        };

        var ics = IcalMapper.Serialize(evt);
        var parsed = Assert.Single(IcalMapper.ToCalendarEvents(ics, Guid.NewGuid(), null, null));

        Assert.True(parsed.IsAllDay);
        Assert.Equal(startDate, AllDayDate.ToLocalDate(parsed.StartUtc, TimeZoneInfo.Local));
        Assert.Equal(endExclusive, AllDayDate.ToLocalDate(parsed.EndUtc, TimeZoneInfo.Local));
        Assert.Equal(evt.StartUtc, parsed.StartUtc);
        Assert.Equal(evt.EndUtc, parsed.EndUtc);
    }

    [Fact]
    public void AllDay_Parse_Date_Maps_To_Local_Midnight_Instant()
    {
        var ics = VCalendarHead +
                  "BEGIN:VEVENT\r\n" +
                  "UID:allday-parse\r\n" +
                  "DTSTART;VALUE=DATE:20260715\r\n" +
                  "DTEND;VALUE=DATE:20260716\r\n" +
                  "SUMMARY:All-day\r\n" +
                  "END:VEVENT\r\n" +
                  VCalendarTail;

        var evt = Assert.Single(IcalMapper.ToCalendarEvents(ics, Guid.NewGuid(), null, null));
        Assert.True(evt.IsAllDay);
        Assert.Equal(
            AllDayDate.ToUtcInstant(new DateOnly(2026, 7, 15), TimeZoneInfo.Local),
            evt.StartUtc);
        Assert.Equal(
            AllDayDate.ToUtcInstant(new DateOnly(2026, 7, 16), TimeZoneInfo.Local),
            evt.EndUtc);
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
