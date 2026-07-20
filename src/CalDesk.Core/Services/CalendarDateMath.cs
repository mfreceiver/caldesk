namespace CalDesk.Core.Services;

/// <summary>Shared date arithmetic used by both <see cref="CalendarViewService"/> (Core)
/// and the App view models, so that week/month grid ranges are computed identically
/// on both sides (fixes the 35-day query vs 42-day render mismatch).</summary>
public static class CalendarDateMath
{
    /// <summary>0 for Monday .. 6 for Sunday.</summary>
    public static int DaysSinceMonday(DateOnly date) => ((int)date.DayOfWeek + 6) % 7;

    /// <summary>UTC instant of local midnight (00:00) for <paramref name="date"/> in <paramref name="tz"/>.
    /// Uses <see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime, TimeZoneInfo)"/> so the right offset is
    /// applied per-date (DST-active summer vs DST-inactive winter differ by the DST delta), matching
    /// the day-boundary semantics expected by <see cref="CalendarViewService"/> and the App view models.
    /// <para><b>DST gap safety (T2-C1):</b> when local midnight falls inside a spring-forward gap
    /// (clock jumps 00:00 → 01:00, so 00:00 does not exist — historical America/Sao_Paulo pattern),
    /// <c>ConvertTimeToUtc</c> would throw <c>ArgumentException</c>. We guard with
    /// <see cref="TimeZoneInfo.IsInvalidTime(DateTime)"/> and fall back to constructing the offset
    /// directly via <see cref="TimeZoneInfo.GetUtcOffset(DateTime)"/>. <b>The offset direction returned
    /// by <c>GetUtcOffset</c> for an invalid time is platform-dependent</b> — it may be the pre-gap
    /// standard offset or the post-gap DST offset (Windows tzdata vs ICU on Linux differ), so this
    /// doc deliberately does <em>not</em> assert a specific direction. The fallback's sole purpose is
    /// to avoid the throw and produce a UTC instant within one DST delta (≤1h) of the base-offset
    /// midnight, which is an acceptable bound for a half-open storage query window edge (a ≤1h overlap
    /// or gap against the neighbouring day boundary is acceptable DST tolerance).</para>
    /// <para><b>Deliberate difference from <c>IcalMapper.ConvertCivilToUtc</c>:</b> that method probes
    /// backwards to the last valid pre-gap instant and uses its offset (per RFC 5545 §3.3.5) because
    /// it serves <em>event-time precision</em>. This helper serves day-boundary <em>window edges</em>,
    /// where the platform's <c>GetUtcOffset(invalid)</c> direction (whichever it is) is acceptable.</para>
    /// <para>Deviation from plan §T2 Step 3 literal code: that code would throw on midnight gaps
    /// (proven by failing test <c>MidnightSpringForwardGap_DoesNotThrowAndReturnsReasonableUtc</c>),
    /// violating T2-C1. This wrapper preserves <c>ConvertTimeToUtc</c> as the primary path for all
    /// valid times; only the rare gap-day edge case takes the fallback.</para></summary>
    public static DateTimeOffset LocalMidnightToUtc(DateOnly date, TimeZoneInfo tz)
    {
        var local = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified);

        // Spring-forward gap at local midnight: ConvertTimeToUtc would throw. Fall back to direct
        // offset construction — GetUtcOffset's direction for invalid times is platform-dependent
        // (pre- or post-gap); see the XML doc above for why either direction is acceptable here.
        if (tz.IsInvalidTime(local))
        {
            return new DateTimeOffset(local, tz.GetUtcOffset(local)).ToUniversalTime();
        }

        var utc = TimeZoneInfo.ConvertTimeToUtc(local, tz); // DST-safe for valid times
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }
}
