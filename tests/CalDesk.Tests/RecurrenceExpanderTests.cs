using CalDesk.CalDav.Calendar;
using CalDesk.CalDav.Recurrence;
using CalDesk.Core.Models;
using CalDesk.Core.Recurrence;
using Xunit;

namespace CalDesk.Tests;

public sealed class RecurrenceExpanderTests
{
    private readonly IRecurrenceExpander _expander = new IcalRecurrenceExpander();

    private static CalendarEvent MakeRecurring(string rrule)
    {
        var start = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var evt = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Uid = "rec-1",
            Summary = "Recurring",
            StartUtc = start,
            EndUtc = start.AddHours(1),
        };
        evt.RecurrenceRule = rrule;
        evt.ICalendarData = IcalMapper.Serialize(evt);
        return evt;
    }

    [Fact]
    public void DailyExpandsAcrossWeek()
    {
        var evt = MakeRecurring("FREQ=DAILY;INTERVAL=1");
        var rangeStart = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);
        var rangeEnd = rangeStart.AddDays(7);

        var occs = _expander.Expand(evt, new CalendarInfo(), rangeStart, rangeEnd).ToList();

        Assert.True(occs.Count >= 7, $"expected >=7 daily occurrences, got {occs.Count}");
        // Each occurrence on a distinct day.
        var days = occs.Select(o => o.StartUtc.Date).Distinct().Count();
        Assert.Equal(occs.Count, days);
    }

    [Fact]
    public void WeeklyByDayExpandsOnlyMatchingDays()
    {
        var evt = MakeRecurring("FREQ=WEEKLY;INTERVAL=1;BYDAY=MO,WE,FR");
        var rangeStart = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero); // Monday
        var rangeEnd = rangeStart.AddDays(7);

        var occs = _expander.Expand(evt, new CalendarInfo(), rangeStart, rangeEnd).ToList();

        // Mon (13), Wed (15), Fri (17) => 3 within the week.
        Assert.Equal(3, occs.Count);
    }

    [Fact]
    public void NonRecurringSingleEventYieldsOneWhenOverlapping()
    {
        var evt = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Uid = "s1",
            Summary = "Single",
            StartUtc = new DateTimeOffset(2026, 7, 15, 14, 0, 0, TimeSpan.Zero),
            EndUtc = new DateTimeOffset(2026, 7, 15, 15, 0, 0, TimeSpan.Zero),
        };

        var occs = _expander.Expand(evt, new CalendarInfo(),
            new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero)).ToList();

        Assert.Single(occs);
    }

    // ---- Issue 6: range-overlap for long events ----------------------------

    [Fact]
    public void LongEvent_Starting_Before_Range_Is_Included_When_Overlapping()
    {
        // A single 48-hour event starting the day before the range. The naive implementation
        // asks Ical.Net for occurrences starting at rangeStart, dropping anything that started
        // earlier — but this event still overlaps the range and must be returned.
        var start = new DateTimeOffset(2026, 7, 12, 9, 0, 0, TimeSpan.Zero);
        var evt = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Uid = "long-single",
            Summary = "48-hour event",
            StartUtc = start,
            EndUtc = start.AddHours(48), // 2026-07-14 09:00
        };
        evt.ICalendarData = IcalMapper.Serialize(evt);

        var rangeStart = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);
        var rangeEnd = rangeStart.AddDays(7);

        var occs = _expander.Expand(evt, new CalendarInfo(), rangeStart, rangeEnd).ToList();

        var occ = Assert.Single(occs);
        Assert.True(occ.StartUtc < rangeStart, "occurrence should start before range");
        Assert.True(occ.EndUtc > rangeStart, "occurrence should still overlap range");
        Assert.Equal(start.AddHours(48), occ.EndUtc);
    }

    [Fact]
    public void Recurring_LongEvent_Occurrences_Overlapping_Range_Are_Returned_With_Correct_EndTime()
    {
        // A daily 48-hour event whose first occurrence starts the day before the range. Each
        // overlapping occurrence must be returned with its correct end time (start + 48h).
        var firstStart = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
        var evt = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Uid = "long-recur",
            Summary = "48h-daily",
            StartUtc = firstStart,
            EndUtc = firstStart.AddHours(48),
        };
        evt.RecurrenceRule = "FREQ=DAILY;INTERVAL=1;COUNT=5";
        evt.ICalendarData = IcalMapper.Serialize(evt);

        var rangeStart = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);
        var rangeEnd = rangeStart.AddDays(7); // up to 2026-07-20

        var occs = _expander.Expand(evt, new CalendarInfo(), rangeStart, rangeEnd).ToList();

        // Occurrence on 07-12 (overlaps range via [07-12, 07-14)) — must be present.
        Assert.Contains(occs, o => o.StartUtc == firstStart && o.EndUtc == firstStart.AddHours(48));
        // Each overlapping occurrence must carry the right end time (no zero-length corruption).
        Assert.All(occs, o => Assert.Equal(o.StartUtc.AddHours(48), o.EndUtc));
        // And no occurrence should end at-or-before rangeStart (would mean it doesn't overlap).
        Assert.All(occs, o => Assert.True(o.EndUtc > rangeStart));
        // No occurrence starts on/after rangeEnd.
        Assert.All(occs, o => Assert.True(o.StartUtc < rangeEnd));
    }

    // ---- CQ-004: same-resource RECURRENCE-ID overrides ---------------------

    private const string VCalHead = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//t//EN\r\n";
    private const string VCalTail = "END:VCALENDAR\r\n";

    /// <summary>Master DAILY 09:00Z from 7/13 plus an override that MOVES the 7/15
    /// occurrence to 14:00Z (same resource, RECURRENCE-ID form).</summary>
    private static string IcsWithMovedOverride(bool overrideFirst)
    {
        var master =
            "BEGIN:VEVENT\r\nUID:ovr\r\nDTSTART:20260713T090000Z\r\nDTEND:20260713T100000Z\r\n" +
            "RRULE:FREQ=DAILY;INTERVAL=1\r\nSUMMARY:master\r\nEND:VEVENT\r\n";
        var ov =
            "BEGIN:VEVENT\r\nUID:ovr\r\nRECURRENCE-ID:20260715T090000Z\r\n" +
            "DTSTART:20260715T140000Z\r\nDTEND:20260715T150000Z\r\nSUMMARY:moved\r\nEND:VEVENT\r\n";
        return VCalHead + (overrideFirst ? ov + master : master + ov) + VCalTail;
    }

    private static CalendarEvent MakeRecurringFromRaw(string raw)
    {
        var evt = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Uid = "ovr",
            Summary = "Recurring",
            // Any non-blank rule routes through the raw-ICS expansion path; the actual
            // series comes from the ICS itself.
            StartUtc = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero),
            EndUtc = new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero),
        };
        evt.RecurrenceRule = "FREQ=DAILY;INTERVAL=1";
        evt.ICalendarData = raw;
        return evt;
    }

    [Fact]
    public void SameResourceOverride_MovedInstance_ShowsAtNewTimeOnly()
    {
        // The override must REPLACE the master occurrence it targets: 7/15 09:00 (old slot)
        // disappears, 7/15 14:00 (new slot) appears, and the untouched master occurrences
        // stay. Previously the expander dropped the override VEVENT entirely: the moved
        // instance showed at its old time and never at the new one.
        var evt = MakeRecurringFromRaw(IcsWithMovedOverride(overrideFirst: false));
        var rangeStart = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);
        var rangeEnd = rangeStart.AddDays(7);

        var occs = _expander.Expand(evt, new CalendarInfo(), rangeStart, rangeEnd).ToList();

        var oldSlot = new DateTimeOffset(2026, 7, 15, 9, 0, 0, TimeSpan.Zero);
        var newSlot = new DateTimeOffset(2026, 7, 15, 14, 0, 0, TimeSpan.Zero);
        Assert.DoesNotContain(occs, o => o.StartUtc == oldSlot);
        Assert.Contains(occs, o => o.StartUtc == newSlot);
        var moved = occs.Single(o => o.StartUtc == newSlot);
        Assert.Equal(newSlot.AddHours(1), moved.EndUtc);
        // Untouched master occurrences remain.
        Assert.Contains(occs, o => o.StartUtc == new DateTimeOffset(2026, 7, 14, 9, 0, 0, TimeSpan.Zero));
        Assert.Contains(occs, o => o.StartUtc == new DateTimeOffset(2026, 7, 16, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void SameResourceOverride_FirstInFile_MasterIsStillSelected()
    {
        // Audit layout 3: when the override VEVENT precedes the master in the file, the
        // "first VEVENT with matching UID" pick grabs the override and the whole series
        // vanishes. Master selection must skip RECURRENCE-ID components.
        var evt = MakeRecurringFromRaw(IcsWithMovedOverride(overrideFirst: true));
        var rangeStart = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);
        var rangeEnd = rangeStart.AddDays(7);

        var occs = _expander.Expand(evt, new CalendarInfo(), rangeStart, rangeEnd).ToList();

        Assert.True(occs.Count >= 7, $"series vanished: {occs.Count} occurrences");
        Assert.DoesNotContain(occs, o => o.StartUtc == new DateTimeOffset(2026, 7, 15, 9, 0, 0, TimeSpan.Zero));
        Assert.Contains(occs, o => o.StartUtc == new DateTimeOffset(2026, 7, 15, 14, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void SameResourceOverride_Cancelled_RemovesInstance()
    {
        // RFC 5545 semantics used by Google & co. to delete a single instance: an override
        // with STATUS:CANCELLED (DTSTART mirroring the RECURRENCE-ID slot). The instance
        // must disappear entirely — neither the old master slot nor a new one.
        var ics = VCalHead +
                  "BEGIN:VEVENT\r\nUID:ovr\r\nDTSTART:20260713T090000Z\r\nDTEND:20260713T100000Z\r\n" +
                  "RRULE:FREQ=DAILY;INTERVAL=1\r\nSUMMARY:master\r\nEND:VEVENT\r\n" +
                  "BEGIN:VEVENT\r\nUID:ovr\r\nRECURRENCE-ID:20260715T090000Z\r\n" +
                  "DTSTART:20260715T090000Z\r\nDTEND:20260715T100000Z\r\nSTATUS:CANCELLED\r\nSUMMARY:gone\r\nEND:VEVENT\r\n" +
                  VCalTail;
        var evt = MakeRecurringFromRaw(ics);
        var rangeStart = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);
        var rangeEnd = rangeStart.AddDays(7);

        var occs = _expander.Expand(evt, new CalendarInfo(), rangeStart, rangeEnd).ToList();

        Assert.DoesNotContain(occs, o => o.StartUtc.Date == new DateTime(2026, 7, 15));
        // Series continues around the hole.
        Assert.Contains(occs, o => o.StartUtc == new DateTimeOffset(2026, 7, 14, 9, 0, 0, TimeSpan.Zero));
        Assert.Contains(occs, o => o.StartUtc == new DateTimeOffset(2026, 7, 16, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void PureMaster_NoOverride_UnchangedSeries()
    {
        // Regression lock: a plain recurring event (no overrides in the resource) expands
        // exactly as before the CQ-004 change.
        var evt = MakeRecurringFromRaw(VCalHead +
                                       "BEGIN:VEVENT\r\nUID:ovr\r\nDTSTART:20260713T090000Z\r\nDTEND:20260713T100000Z\r\n" +
                                       "RRULE:FREQ=DAILY;INTERVAL=1\r\nSUMMARY:master\r\nEND:VEVENT\r\n" + VCalTail);
        var rangeStart = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);
        var rangeEnd = rangeStart.AddDays(7);

        var occs = _expander.Expand(evt, new CalendarInfo(), rangeStart, rangeEnd).ToList();

        Assert.Equal(7, occs.Count);
        Assert.All(occs, o => Assert.Equal(9, o.StartUtc.Hour));
    }
}
