using System.Globalization;

namespace CalDesk.Core.Recurrence;

/// <summary>iCalendar recurrence frequency.</summary>
public enum Frequency
{
    Daily,
    Weekly,
    Monthly,
    Yearly,
}

/// <summary>A simple recurrence pattern (subset of RFC 5545 RRULE) supported for creation.
/// Existing complex rules are preserved verbatim in <see cref="Models.CalendarEvent.RecurrenceRule"/>
/// and expanded by <see cref="IRecurrenceExpander"/> for display.</summary>
public sealed class RecurrencePattern
{
    public Frequency Frequency { get; set; } = Frequency.Weekly;

    /// <summary>Repeat every N intervals (default 1).</summary>
    public int Interval { get; set; } = 1;

    /// <summary>Total number of occurrences, or null if unbounded.</summary>
    public int? Count { get; set; }

    /// <summary>Inclusive recurrence end (the UNTIL value). When <see cref="UntilHasTime"/>
    /// is false only the date part is significant (DATE form). Null when unbounded.</summary>
    public DateTime? Until { get; set; }

    /// <summary>True when UNTIL must serialize in the DATE-TIME form. RFC 5545 §3.3.10
    /// requires the UNTIL value type to match DTSTART, so the parsed form is preserved
    /// through Parse→ToRrule instead of truncating DATE-TIME values to midnight — which
    /// silently dropped the final occurrence of bounded series (CQ-007).</summary>
    public bool UntilHasTime { get; set; }

    /// <summary>For weekly recurrence: the weekdays to repeat on.</summary>
    public List<DayOfWeek> ByDay { get; set; } = new();

    private static readonly Dictionary<DayOfWeek, string> DayCodes = new()
    {
        [DayOfWeek.Sunday] = "SU",
        [DayOfWeek.Monday] = "MO",
        [DayOfWeek.Tuesday] = "TU",
        [DayOfWeek.Wednesday] = "WE",
        [DayOfWeek.Thursday] = "TH",
        [DayOfWeek.Friday] = "FR",
        [DayOfWeek.Saturday] = "SA",
    };

    private static readonly Dictionary<string, DayOfWeek> CodeDays =
        DayCodes.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Serialize to an RRULE value (without the leading <c>RRULE:</c>).</summary>
    public string ToRrule()
    {
        var parts = new List<string>
        {
            $"FREQ={Frequency.ToString().ToUpperInvariant()}",
            $"INTERVAL={Math.Max(1, Interval)}",
        };

        if (Count.HasValue)
        {
            parts.Add($"COUNT={Count.Value}");
        }

        if (Until is { } until)
        {
            if (UntilHasTime)
            {
                // Keep the DATE-TIME form — and the UTC marker when the value carried one —
                // so a bounded series does not lose its final occurrence (CQ-007).
                var z = until.Kind == DateTimeKind.Utc ? "Z" : string.Empty;
                parts.Add($"UNTIL={until:yyyyMMdd'T'HHmmss}{z}");
            }
            else
            {
                parts.Add($"UNTIL={until:yyyyMMdd}");
            }
        }

        if (Frequency == Frequency.Weekly && ByDay.Count > 0)
        {
            var codes = ByDay.Select(d => DayCodes[d]).Distinct();
            parts.Add("BYDAY=" + string.Join(",", codes));
        }

        return string.Join(";", parts);
    }

    /// <summary>Parse an RRULE value. Returns null when <paramref name="rrule"/> is blank or unparseable.</summary>
    public static RecurrencePattern? Parse(string? rrule)
    {
        if (string.IsNullOrWhiteSpace(rrule))
        {
            return null;
        }

        var value = rrule.Trim();
        if (value.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["RRULE:".Length..];
        }

        var result = new RecurrencePattern();
        Frequency? frequency = null;
        foreach (var token in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = token.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }

            var key = token[..eq].Trim().ToUpperInvariant();
            var val = token[(eq + 1)..].Trim();

            switch (key)
            {
                case "FREQ" when Enum.TryParse<Frequency>(val, ignoreCase: true, out var f):
                    frequency = f;
                    break;
                case "INTERVAL" when int.TryParse(val, out var iv):
                    result.Interval = iv <= 0 ? 1 : iv;
                    break;
                case "COUNT" when int.TryParse(val, out var c):
                    result.Count = c;
                    break;
                case "UNTIL":
                    (result.Until, result.UntilHasTime) = ParseUntil(val);
                    break;
                case "BYDAY":
                    result.ByDay = val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(CodeDays.ContainsKey)
                        .Select(c => CodeDays[c])
                        .ToList();
                    break;
            }
        }

        // Unknown (HOURLY/MINUTELY/SECONDLY/…) or missing FREQ → not a rule this subset can
        // represent. Return null rather than silently keeping the Weekly default: callers
        // compare Parse(rule) output against presets, and a defaulted Frequency would
        // misclassify the rule (CQ-001 trap).
        if (frequency is null)
        {
            return null;
        }

        result.Frequency = frequency.Value;
        return result;
    }

    /// <summary>Parses an UNTIL value in either RFC 5545 form: DATE (YYYYMMDD) or
    /// DATE-TIME (…THHMMSS, optionally Z-suffixed). The form flag travels with the value
    /// so <see cref="ToRrule"/> re-emits the same shape (CQ-007). An unparseable value
    /// yields (null, false) and is treated as "no bound set".</summary>
    private static (DateTime? Until, bool HasTime) ParseUntil(string val)
    {
        var s = val.Trim();
        var isUtc = s.EndsWith('Z');
        if (isUtc)
        {
            s = s[..^1];
        }

        if (s.Length == 8 && DateTime.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return (date, HasTime: false);
        }

        if (DateTime.TryParseExact(s, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTime))
        {
            // Keep the UTC marker (if any) as the DateTime kind so ToRrule can restore it.
            return (DateTime.SpecifyKind(dateTime, isUtc ? DateTimeKind.Utc : DateTimeKind.Unspecified), HasTime: true);
        }

        return (null, HasTime: false);
    }
}
