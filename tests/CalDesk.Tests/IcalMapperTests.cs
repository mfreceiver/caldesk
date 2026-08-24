using System.IO;
using CalDesk.CalDav.Calendar;
using CalDesk.CalDav.Recurrence;
using CalDesk.Core;
using CalDesk.Core.Models;
using CalDesk.Core.Recurrence;
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

        Assert.True(ve.DtStart!.HasTime);
        Assert.True(ve.DtStart.IsFloating);
        Assert.False(ve.DtStart.IsUtc);
        Assert.Null(ve.DtStart.TzId);

        Assert.Equal(
            new DateTime(2026, 7, 22, 2, 0, 0, DateTimeKind.Utc),
            IcalMapper.CalDateTimeToUtc(ve.DtStart, Shanghai));
        Assert.Equal(
            new DateTime(2026, 7, 22, 3, 0, 0, DateTimeKind.Utc),
            IcalMapper.CalDateTimeToUtc(ve.DtEnd!, Shanghai));
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

        Assert.True(ve.DtStart!.IsUtc);
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

        Assert.False(ve.DtStart!.HasTime);

        Assert.Equal(
            new DateTime(2026, 7, 21, 16, 0, 0, DateTimeKind.Utc),
            IcalMapper.CalDateTimeToUtc(ve.DtStart, Shanghai));
        Assert.Equal(
            new DateTime(2026, 7, 22, 16, 0, 0, DateTimeKind.Utc),
            IcalMapper.CalDateTimeToUtc(ve.DtEnd!, Shanghai));
    }

    // ---- CQ-001 / CQ-007: RRULE round-trip fidelity on edit -----------------------
    //
    // The push path rewrites the VEVENT in place from the locally stored rule. These tests
    // lock the invariant: editing anything except the recurrence itself must keep the raw
    // RRULE (including parts our model cannot represent) and its expansion unchanged.

    private static readonly IRecurrenceExpander Expander = new IcalRecurrenceExpander();

    private static string RawIcs(string uid, string dtstart, string dtend, string rrule) =>
        VCalendarHead +
        "BEGIN:VEVENT\r\n" +
        $"UID:{uid}\r\n" +
        $"DTSTART:{dtstart}\r\n" +
        $"DTEND:{dtend}\r\n" +
        $"RRULE:{rrule}\r\n" +
        "SUMMARY:Original\r\n" +
        "END:VEVENT\r\n" +
        VCalendarTail;

    /// <summary>Simulates the real edit flow: pull (the mapper stores the pull-side
    /// projection of the raw rule) → user changes only the summary → push (Serialize).</summary>
    private static (string Output, CalendarEvent Reparsed) EditSummaryOnly(string ics)
    {
        var evt = Assert.Single(IcalMapper.ToCalendarEvents(ics, Guid.NewGuid(), "/x/edit.ics", "\"e1\""));
        evt.Summary = "Edited";
        var output = IcalMapper.Serialize(evt);
        var reparsed = Assert.Single(IcalMapper.ToCalendarEvents(output, Guid.NewGuid(), null, null));
        return (output, reparsed);
    }

    /// <summary>Counts occurrences by expanding raw ICS directly — Ical.Net expands the
    /// RRULE inside ICalendarData, so the stored rule string does not matter here.</summary>
    private static int ExpandCount(string ics, DateTimeOffset from, DateTimeOffset to)
    {
        var evt = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Uid = "expand-probe",
            Summary = "probe",
            RecurrenceRule = "FREQ=DAILY;INTERVAL=1", // any non-blank rule forces the recurring path
            ICalendarData = ics,
            StartUtc = from,
            EndUtc = from,
        };
        return Expander.Expand(evt, new CalendarInfo(), from, to).Count();
    }

    [Fact]
    public void EditOnlySummary_PreservesWeeklyInterval2Rrule()
    {
        var ics = RawIcs("wk2", "20260713T090000Z", "20260713T100000Z", "FREQ=WEEKLY;INTERVAL=2");
        var from = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddDays(28);

        var (output, reparsed) = EditSummaryOnly(ics);

        Assert.Equal("Edited", reparsed.Summary);
        Assert.Contains("FREQ=WEEKLY", output);
        Assert.Contains("INTERVAL=2", output);
        Assert.Equal(2, ExpandCount(ics, from, to));
        Assert.Equal(ExpandCount(ics, from, to), ExpandCount(output, from, to));
    }

    [Fact]
    public void EditOnlySummary_PreservesCountRrule()
    {
        var ics = RawIcs("cnt", "20260713T090000Z", "20260713T100000Z", "FREQ=DAILY;COUNT=5");
        var from = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddDays(7);

        var (output, reparsed) = EditSummaryOnly(ics);

        Assert.Equal("Edited", reparsed.Summary);
        Assert.Contains("FREQ=DAILY", output);
        Assert.Contains("COUNT=5", output);
        Assert.Equal(5, ExpandCount(output, from, to));
    }

    [Fact]
    public void EditOnlySummary_PreservesDateTimeUntil_And_LastOccurrence()
    {
        // CQ-007: the bound must stay a DATE-TIME. Truncating UNTIL=…T230000Z to the DATE
        // form reinterprets the bound as midnight and drops the 7/15 occurrence (audit
        // repro: 4 → 3).
        var ics = RawIcs("until-dt", "20260712T230000Z", "20260712T233000Z", "FREQ=DAILY;UNTIL=20260715T230000Z");
        var from = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddDays(6);

        var (output, reparsed) = EditSummaryOnly(ics);

        Assert.Equal("Edited", reparsed.Summary);
        Assert.Contains("UNTIL=20260715T230000Z", output);
        // 7/12, 7/13, 7/14 AND 7/15 — the 23:00 occurrence still satisfies the 23:00 bound.
        Assert.Equal(4, ExpandCount(ics, from, to));
        Assert.Equal(ExpandCount(ics, from, to), ExpandCount(output, from, to));
    }

    [Fact]
    public void EditOnlySummary_PreservesByMonthDayRrule()
    {
        // CQ-001 variant (b): BYMONTHDAY is outside our subset; the pull-side projection
        // already drops it from the stored rule, so the push side must keep the raw RRULE
        // node instead of rewriting from the (lossy) stored rule.
        var ics = RawIcs("bmd", "20260715T090000Z", "20260715T100000Z", "FREQ=MONTHLY;BYMONTHDAY=15");
        var from = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddDays(92); // through early October

        var (output, reparsed) = EditSummaryOnly(ics);

        Assert.Equal("Edited", reparsed.Summary);
        Assert.Contains("FREQ=MONTHLY", output);
        Assert.Contains("BYMONTHDAY=15", output);
        // 7/15, 8/15, 9/15 — the month-day must not silently move to the DTSTART-day form.
        Assert.Equal(3, ExpandCount(ics, from, to));
        Assert.Equal(ExpandCount(ics, from, to), ExpandCount(output, from, to));
    }

    [Fact]
    public void EditOnlySummary_PreservesYearlyRrule()
    {
        var ics = RawIcs("yr", "20260715T090000Z", "20260715T100000Z", "FREQ=YEARLY");
        var from = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddYears(3);

        var (output, reparsed) = EditSummaryOnly(ics);

        Assert.Equal("Edited", reparsed.Summary);
        Assert.Contains("FREQ=YEARLY", output);
        Assert.Equal(3, ExpandCount(output, from, to));
    }

    [Fact]
    public void EditOnlySummary_PreservesHourlyRrule()
    {
        // Since the CQ-005 fix the pull side PASSES THROUGH unknown frequencies, so the
        // stored rule is non-null and the mapper-level projection already round-trips. The
        // push side must still keep the raw RRULE node untouched on a mere title edit.
        var ics = RawIcs("hr", "20260713T000000Z", "20260713T010000Z", "FREQ=HOURLY;INTERVAL=2");
        var from = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddHours(12);

        var evt = Assert.Single(IcalMapper.ToCalendarEvents(ics, Guid.NewGuid(), "/x/hr.ics", "\"e1\""));
        Assert.True(evt.IsRecurring); // CQ-005: HOURLY is passed through, not degraded to single
        evt.Summary = "Edited";

        var output = IcalMapper.Serialize(evt);

        Assert.Contains("FREQ=HOURLY", output);
        Assert.Contains("INTERVAL=2", output);
        Assert.Equal(6, ExpandCount(ics, from, to));
        Assert.Equal(ExpandCount(ics, from, to), ExpandCount(output, from, to));
    }

    // ---- CQ-005: pull-side pass-through of frequencies outside the creatable subset ----

    [Fact]
    public void PullUnknownFrequency_RuleIsPassedThroughNotNulled()
    {
        // FREQ=HOURLY/MINUTELY/SECONDLY are RFC-5545-legal but outside our preset subset.
        // They must be stored verbatim (canonical Ical.Net form) so IsRecurring is true and
        // the expander walks the raw-ICS path — instead of silently degrading the series to
        // a single event (CQ-005).
        var ics = RawIcs("hourly", "20260713T000000Z", "20260713T010000Z", "FREQ=HOURLY;INTERVAL=2");

        var result = IcalMapper.TryToCalendarEvents(ics, Guid.NewGuid(), "/x/h.ics", "\"e1\"");

        Assert.True(result.Success, result.Error);
        var evt = Assert.Single(result.Events);
        Assert.NotNull(evt.RecurrenceRule);
        Assert.Contains("FREQ=HOURLY", evt.RecurrenceRule);
        Assert.Contains("INTERVAL=2", evt.RecurrenceRule);
        Assert.True(evt.IsRecurring);
    }

    [Fact]
    public void PullUnknownFrequency_ExpandsHourlyWindow()
    {
        // End-to-end effect of the pass-through: a 12-hour window over an INTERVAL=2 hourly
        // series yields 6 occurrences via the raw-ICS Ical.Net path.
        var ics = RawIcs("hourly2", "20260713T000000Z", "20260713T010000Z", "FREQ=HOURLY;INTERVAL=2");
        var evt = Assert.Single(IcalMapper.ToCalendarEvents(ics, Guid.NewGuid(), "/x/h2.ics", "\"e1\""));

        var occs = Expander.Expand(evt, new CalendarInfo(),
            new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero)).ToList();

        Assert.Equal(6, occs.Count);
    }

    [Fact]
    public void PullNoRrule_StillYieldsNullRule()
    {
        // Regression lock for the old `_ => null` path: absence of RRULE keeps RecurrenceRule
        // null (the pass-through only applies when a rule EXISTS but its FREQ is unknown).
        var ics = VCalendarHead +
                  "BEGIN:VEVENT\r\nUID:norule\r\nDTSTART:20260713T090000Z\r\nDTEND:20260713T100000Z\r\n" +
                  "SUMMARY:plain\r\nEND:VEVENT\r\n" +
                  VCalendarTail;

        var evt = Assert.Single(IcalMapper.ToCalendarEvents(ics, Guid.NewGuid(), null, null));

        Assert.Null(evt.RecurrenceRule);
        Assert.False(evt.IsRecurring);
    }

    // ---- CQ-006: legacy global-form TZID prefixes must not quarantine the resource ----

    private static string VEventWithTzid(string tzid, bool withVtimezoneDefinition, string vtimezoneOffset = "+0530")
    {
        var vtz = withVtimezoneDefinition
            ? "BEGIN:VTIMEZONE\r\n" +
              $"TZID:{tzid}\r\n" +
              "BEGIN:STANDARD\r\n" +
              "DTSTART:19700101T000000\r\n" +
              $"TZOFFSETFROM:{vtimezoneOffset}\r\n" +
              $"TZOFFSETTO:{vtimezoneOffset}\r\n" +
              "END:STANDARD\r\n" +
              "END:VTIMEZONE\r\n"
            : string.Empty;

        return VCalendarHead + vtz +
               "BEGIN:VEVENT\r\n" +
               $"UID:tz-{tzid.GetHashCode():x}\r\n" +
               $"DTSTART;TZID={tzid}:20260715T090000\r\n" +
               $"DTEND;TZID={tzid}:20260715T100000\r\n" +
               "SUMMARY:tzprobe\r\n" +
               "END:VEVENT\r\n" +
               VCalendarTail;
    }

    [Fact]
    public void PrefixedTzid_MozillaGlobalForm_ResolvesInsteadOfQuarantining()
    {
        // Audit repro: Mozilla Sunbird/old KOrganizer emit global-form TZIDs
        // (/mozilla.org/<build>/<iana id>). The system lookup must strip the legacy site
        // prefix and resolve the IANA remainder — 09:00 Berlin in July (CEST, UTC+2) is
        // 07:00Z. Previously this threw TimeZoneNotFoundException → whole-resource quarantine.
        var ics = VEventWithTzid("/mozilla.org/20050126_1/Europe/Berlin", withVtimezoneDefinition: false);

        var result = IcalMapper.TryToCalendarEvents(ics, Guid.NewGuid(), "/x/tz1.ics", "\"e1\"");

        Assert.True(result.Success, result.Error);
        var evt = Assert.Single(result.Events);
        Assert.Equal(new DateTime(2026, 7, 15, 7, 0, 0, DateTimeKind.Utc), evt.StartUtc.UtcDateTime);
    }

    [Fact]
    public void PrefixedTzid_WithVTimeZoneDefinition_AlsoResolves()
    {
        // Same global-form TZID, now with a full VTIMEZONE block in the resource (the control
        // group from the audit). Prefix stripping alone resolves it via the system tzdb —
        // the VTIMEZONE definition is the fallback for when even the stripped ID is unknown.
        var ics = VEventWithTzid("/mozilla.org/20050126_1/Europe/Berlin", withVtimezoneDefinition: true);

        var result = IcalMapper.TryToCalendarEvents(ics, Guid.NewGuid(), "/x/tz2.ics", "\"e1\"");

        Assert.True(result.Success, result.Error);
        var evt = Assert.Single(result.Events);
        Assert.Equal(new DateTime(2026, 7, 15, 7, 0, 0, DateTimeKind.Utc), evt.StartUtc.UtcDateTime);
    }

    [Fact]
    public void UnprefixedTzid_StillResolves_Directly()
    {
        // Regression lock: the normal, unprefixed TZID path must keep working unchanged.
        var ics = VEventWithTzid("Asia/Shanghai", withVtimezoneDefinition: false);

        var result = IcalMapper.TryToCalendarEvents(ics, Guid.NewGuid(), "/x/tz3.ics", "\"e1\"");

        Assert.True(result.Success, result.Error);
        var evt = Assert.Single(result.Events);
        Assert.Equal(new DateTime(2026, 7, 15, 1, 0, 0, DateTimeKind.Utc), evt.StartUtc.UtcDateTime);
    }

    [Fact]
    public void UnknownTzid_WithoutVTimeZone_StillQuarantines()
    {
        // A genuinely unknown TZID with no VTIMEZONE definition must STILL fail the parse
        // (quarantine) — the fix must not silently reinterpret unknown zones as local.
        var ics = VEventWithTzid("/mozilla.org/20050126_1/Custom/Zone_999", withVtimezoneDefinition: false);

        var result = IcalMapper.TryToCalendarEvents(ics, Guid.NewGuid(), "/x/tz4.ics", "\"e1\"");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void UnknownTzid_FallsBackToVTimeZoneOffset()
    {
        // Last link of the fallback chain: even the stripped ID is unknown to the system,
        // but the resource carries a VTIMEZONE defining a fixed offset (+05:30). The civil
        // time must convert using that offset: 09:00 - 05:30 = 03:30Z — not quarantine.
        var ics = VEventWithTzid("/mozilla.org/20050126_1/Custom/Zone_999", withVtimezoneDefinition: true, vtimezoneOffset: "+0530");

        var result = IcalMapper.TryToCalendarEvents(ics, Guid.NewGuid(), "/x/tz5.ics", "\"e1\"");

        Assert.True(result.Success, result.Error);
        var evt = Assert.Single(result.Events);
        Assert.Equal(new DateTime(2026, 7, 15, 3, 30, 0, DateTimeKind.Utc), evt.StartUtc.UtcDateTime);
    }

    [Fact]
    public void EditClearedRule_RemovesRruleFromOutput()
    {
        // None is an explicit user choice: clearing must keep working. The destruction bug
        // was never this path — it was clearing rules the user never touched.
        var ics = RawIcs("clear", "20260713T090000Z", "20260713T100000Z", "FREQ=WEEKLY;INTERVAL=1;BYDAY=MO");
        var evt = Assert.Single(IcalMapper.ToCalendarEvents(ics, Guid.NewGuid(), "/x/c.ics", "\"e1\""));
        evt.RecurrenceRule = null; // user picked "does not repeat"
        evt.Summary = "Edited";

        var output = IcalMapper.Serialize(evt);
        var reparsed = Assert.Single(IcalMapper.ToCalendarEvents(output, Guid.NewGuid(), null, null));

        Assert.DoesNotContain("RRULE", output);
        Assert.False(reparsed.IsRecurring);
    }

    [Fact]
    public void EditChangedPresetRule_RewritesRrule()
    {
        // The user actively picked a preset that differs from the raw rule → rewrite path.
        var ics = RawIcs("preset", "20260713T090000Z", "20260713T100000Z", "FREQ=WEEKLY;INTERVAL=2");
        var evt = Assert.Single(IcalMapper.ToCalendarEvents(ics, Guid.NewGuid(), "/x/p.ics", "\"e1\""));
        evt.RecurrenceRule = "FREQ=DAILY;INTERVAL=1"; // user switched to the Daily preset
        evt.Summary = "Edited";

        var output = IcalMapper.Serialize(evt);
        var reparsed = Assert.Single(IcalMapper.ToCalendarEvents(output, Guid.NewGuid(), null, null));

        Assert.Contains("FREQ=DAILY", output);
        Assert.Equal("FREQ=DAILY;INTERVAL=1", reparsed.RecurrenceRule);
    }
}
