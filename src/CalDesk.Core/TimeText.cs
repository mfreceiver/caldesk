using System.Globalization;

namespace CalDesk.Core;

/// <summary>
/// Culture-invariant time text parsing for QuickAdd entry fields.
/// Accepts <c>HH:mm</c>/<c>H:mm</c>, compact 3–4 digit <c>hhmi</c>, and bare hour digits.
/// Does not accept seconds, AM/PM, or locale-sensitive free-form times.
/// </summary>
public static class TimeText
{
    /// <summary>
    /// Try parse a user-entered time string.
    /// <list type="bullet">
    /// <item><c>"0930"</c> / <c>"930"</c> / <c>"09:30"</c> / <c>"9:30"</c> → 09:30</item>
    /// <item><c>"9"</c> → 09:00; <c>"09"</c> / <c>"23"</c> → hour:00</item>
    /// <item><c>"9:5"</c>, seconds, AM/PM, out-of-range → reject</item>
    /// </list>
    /// </summary>
    public static bool TryParse(string? text, out TimeOnly time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim();

        // Strict colon forms only (InvariantCulture). Minutes always 2 digits → rejects "9:5".
        if (TimeOnly.TryParseExact(
                s,
                ["HH:mm", "H:mm"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out time))
        {
            return true;
        }

        if (s.Length is < 1 or > 4 || !s.All(char.IsDigit))
        {
            return false;
        }

        int hour;
        int minute;

        switch (s.Length)
        {
            case 4: // hhmi
                hour = (s[0] - '0') * 10 + (s[1] - '0');
                minute = (s[2] - '0') * 10 + (s[3] - '0');
                break;
            case 3: // hmi
                hour = s[0] - '0';
                minute = (s[1] - '0') * 10 + (s[2] - '0');
                break;
            case 2: // HH → HH:00
                hour = (s[0] - '0') * 10 + (s[1] - '0');
                minute = 0;
                break;
            default: // 1 digit H → 0H:00
                hour = s[0] - '0';
                minute = 0;
                break;
        }

        if (hour is < 0 or > 23 || minute is < 0 or > 59)
        {
            return false;
        }

        time = new TimeOnly(hour, minute);
        return true;
    }

    /// <summary>Format as zero-padded 24h <c>HH:mm</c>.</summary>
    public static string Format(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);
}
