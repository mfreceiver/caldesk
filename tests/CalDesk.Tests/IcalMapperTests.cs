using System.IO;
using CalDesk.CalDav.Calendar;
using CalDesk.Core;
using CalDesk.Core.Models;
using Xunit;

namespace CalDesk.Tests;

public sealed class IcalMapperTests
{
    private const string VCalendarHead = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n";
    private const string VCalendarTail = "END:VCALENDAR\r\n";

    // Deterministic tz for the injectable CalDateTimeToUtc overload (machine-local independent).
    private static readonly TimeZoneInfo Shanghai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");

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

        var         list = IcalMapper.ToCalendarEvents(malformed, Guid.NewGuid(), null, null);
        Assert.Empty(list);
    }

    // ---- Issue: FLOATING / UTC-Z / DATE classification (CalDateTimeToUtc) -------------

    [Fact]
    public void Floating_DtStart_Is_Interpreted_As_Local_Not_Relabeled_As_Utc()
    {
        // The real-world bug: DTSTART:20260722T100000 (no Z, no TZID) is FLOATING. Ical.Net 5.2.3's
        // AsUtc keeps the wall-clock value and stamps it UTC (10:00Z), so CalDesk displayed 18:00
        // (+8) instead of 10:00. Floating must be interpreted in the user's local zone:
        // 10:00 Shanghai → 02:00Z. Uses the injectable overload so the test is machine-independent.
        var ics = VCalendarHead +
                  "BEGIN:VEVENT\r\n" +
                  "UID:floating\r\n" +
                  "DTSTART:20260722T100000\r\n" +
                  "DTEND:20260722T110000\r\n" +
                  "SUMMARY:floating\r\n" +
                  "END:VEVENT\r\n" +
                  VCalendarTail;

        var ve = Assert.Single(Ical.Net.Calendar.Load(new StringReader(ics))!.Events);

        Assert.True(ve.DtStart.HasTime);
        Assert.True(ve.DtStart.IsFloating);
        Assert.False(ve.DtStart.IsUtc);
        Assert.Null(ve.DtStart.TzId);

        Assert.Equal(
            new DateTime(2026, 7, 22, 2, 0, 0, DateTimeKind.Utc),
            IcalMapper.CalDateTimeToUtc(ve.DtStart, Shanghai));
        Assert.Equal(
            new DateTime(2026, 7, 22, 3, 0, 0, DateTimeKind.Utc),
            IcalMapper.CalDateTimeToUtc(ve.DtEnd, Shanghai));
    }

    [Fact]
    public void UtcZ_DtStart_Stays_The_Same_Utc_Instant()
    {
        // Z-suffixed UTC must NOT be shifted. Ical.Net parses it with TzId=="UTC", IsUtc==true;
        // dt.Value already holds the UTC instant and we only attach Utc kind.
        var ics = VCalendarHead +
                  "BEGIN:VEVENT\r\n" +
                  "UID:utc-z\r\n" +
                  "DTSTART:20260722T100000Z\r\n" +
                  "DTEND:20260722T110000Z\r\n" +
                  "SUMMARY:utc\r\n" +
                  "END:VEVENT\r\n" +
                  VCalendarTail;

        var ve = Assert.Single(Ical.Net.Calendar.Load(new StringReader(ics))!.Events);

        Assert.True(ve.DtStart.IsUtc);
        Assert.False(ve.DtStart.IsFloating);
        Assert.Equal("UTC", ve.DtStart.TzId);

        Assert.Equal(
            new DateTime(2026, 7, 22, 10, 0, 0, DateTimeKind.Utc),
            IcalMapper.CalDateTimeToUtc(ve.DtStart, Shanghai));
    }

    [Fact]
    public void Date_Value_Maps_To_Local_Midnight_Not_Treated_As_Timed_Floating()
    {
        // VALUE=DATE also reports IsFloating==true (no TZID), so HasTime must gate the classification
        // BEFORE the IsFloating check. 2026-07-22 Shanghai midnight → 2026-07-21 16:00Z.
        var ics = VCalendarHead +
                  "BEGIN:VEVENT\r\n" +
                  "UID:allday-cls\r\n" +
                  "DTSTART;VALUE=DATE:20260722\r\n" +
                  "DTEND;VALUE=DATE:20260723\r\n" +
                  "SUMMARY:allday\r\n" +
                  "END:VEVENT\r\n" +
                  VCalendarTail;

        var ve = Assert.Single(Ical.Net.Calendar.Load(new StringReader(ics))!.Events);

        Assert.False(ve.DtStart.HasTime);

        Assert.Equal(
            new DateTime(2026, 7, 21, 16, 0, 0, DateTimeKind.Utc),
            IcalMapper.CalDateTimeToUtc(ve.DtStart, Shanghai));
        Assert.Equal(
            new DateTime(2026, 7, 22, 16, 0, 0, DateTimeKind.Utc),
            IcalMapper.CalDateTimeToUtc(ve.DtEnd, Shanghai));
    }
}
