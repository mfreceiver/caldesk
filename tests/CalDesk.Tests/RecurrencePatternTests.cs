using CalDesk.Core.Recurrence;
using Xunit;

namespace CalDesk.Tests;

// CQ-001 / CQ-007 regression coverage for the Core recurrence subset model.
// All assertions go through Parse/ToRrule strings (never the Until property shape) so the
// tests document behavior, not the internal representation.
public sealed class RecurrencePatternTests
{
    [Fact]
    public void Parse_DateTimeUntil_ToRrule_KeepsDateTimeForm()
    {
        // UNTIL must round-trip in its DATE-TIME form: truncating to a DATE reinterprets the
        // bound as midnight and drops the final occurrence of a bounded series (CQ-007,
        // RFC 5545 §3.3.10 — UNTIL value type must match DTSTART).
        var rrule = RecurrencePattern.Parse("FREQ=DAILY;UNTIL=20260715T230000Z");

        Assert.NotNull(rrule);
        Assert.Equal("FREQ=DAILY;INTERVAL=1;UNTIL=20260715T230000Z", rrule.ToRrule());
    }

    [Fact]
    public void Parse_DateUntil_ToRrule_KeepsDateForm()
    {
        var rrule = RecurrencePattern.Parse("FREQ=DAILY;UNTIL=20260715");

        Assert.NotNull(rrule);
        Assert.Equal("FREQ=DAILY;INTERVAL=1;UNTIL=20260715", rrule.ToRrule());
    }

    [Fact]
    public void Parse_Blank_ReturnsNull()
    {
        Assert.Null(RecurrencePattern.Parse(null));
        Assert.Null(RecurrencePattern.Parse(string.Empty));
        Assert.Null(RecurrencePattern.Parse("   "));
    }

    [Fact]
    public void Parse_UnknownFrequency_ReturnsNull()
    {
        // HOURLY / MINUTELY / SECONDLY are legal RFC 5545 but outside the creatable subset.
        // Parse must NOT silently keep the Weekly default: callers compare Parse(rule)
        // output against presets, and a defaulted Frequency misclassifies the rule
        // (CQ-001 trap — e.g. HOURLY would look like a plain WEEKLY).
        Assert.Null(RecurrencePattern.Parse("FREQ=HOURLY;INTERVAL=2"));
        Assert.Null(RecurrencePattern.Parse("FREQ=MINUTELY"));
        Assert.Null(RecurrencePattern.Parse("FREQ=SECONDLY"));
    }

    [Fact]
    public void Parse_MissingOrBogusFrequency_ReturnsNull()
    {
        Assert.Null(RecurrencePattern.Parse("INTERVAL=2"));
        Assert.Null(RecurrencePattern.Parse("FREQ=BOGUS"));
    }

    [Fact]
    public void Parse_KnownFrequency_StillParses()
    {
        var daily = RecurrencePattern.Parse("FREQ=DAILY;INTERVAL=1");
        Assert.NotNull(daily);
        Assert.Equal(Frequency.Daily, daily.Frequency);

        var yearly = RecurrencePattern.Parse("FREQ=YEARLY");
        Assert.NotNull(yearly);
        Assert.Equal(Frequency.Yearly, yearly.Frequency);
        Assert.Equal("FREQ=YEARLY;INTERVAL=1", yearly.ToRrule());
    }

    [Fact]
    public void Parse_WeeklyByDay_RoundTrips()
    {
        var rrule = RecurrencePattern.Parse("FREQ=WEEKLY;INTERVAL=1;BYDAY=MO,WE");

        Assert.NotNull(rrule);
        Assert.Equal("FREQ=WEEKLY;INTERVAL=1;BYDAY=MO,WE", rrule.ToRrule());
    }
}
