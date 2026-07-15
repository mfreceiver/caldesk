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

    /// <summary>Inclusive end date, or null if unbounded.</summary>
    public DateOnly? Until { get; set; }

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

        if (Until.HasValue)
        {
            parts.Add($"UNTIL={Until.Value:yyyyMMdd}");
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
                    result.Frequency = f;
                    break;
                case "INTERVAL" when int.TryParse(val, out var iv):
                    result.Interval = iv <= 0 ? 1 : iv;
                    break;
                case "COUNT" when int.TryParse(val, out var c):
                    result.Count = c;
                    break;
                case "UNTIL":
                    result.Until = ParseUntil(val);
                    break;
                case "BYDAY":
                    result.ByDay = val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(CodeDays.ContainsKey)
                        .Select(c => CodeDays[c])
                        .ToList();
                    break;
            }
        }

        return result;
    }

    private static DateOnly? ParseUntil(string val)
    {
        // YYYYMMDD or YYYYMMDDThhmmss[Z]
        var s = val.Trim();
        if (s.EndsWith('Z'))
        {
            s = s[..^1];
        }

        if (s.Length == 8 && DateOnly.TryParseExact(s, "yyyyMMdd", out var d))
        {
            return d;
        }

        var datePart = s.Length >= 8 ? s[..8] : s;
        return DateOnly.TryParseExact(datePart, "yyyyMMdd", out var dd) ? dd : null;
    }
}
