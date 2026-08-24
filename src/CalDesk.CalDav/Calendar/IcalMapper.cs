using System.Globalization;
using CalDesk.Core;
using CalDesk.Core.Models;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using CalendarEvent = CalDesk.Core.Models.CalendarEvent;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace CalDesk.CalDav.Calendar;

/// <summary>Maps between raw iCalendar (via Ical.Net) and <see cref="CalendarEvent"/>.
/// Edits preserve the original raw object so unknown / X- properties survive (NFR-05).</summary>
public static class IcalMapper
{
    /// <summary>Parse a raw iCalendar object into one or more events. Returns an empty list
    /// when the resource is empty but parseable. <b>Does not</b> distinguish "parse failed"
    /// from "no events" — use <see cref="TryToCalendarEvents"/> from sync code where the
    /// distinction matters.</summary>
    public static List<CalendarEvent> ToCalendarEvents(string raw, Guid calendarId, string? href, string? etag)
    {
        return TryToCalendarEvents(raw, calendarId, href, etag).Events.ToList();
    }

    /// <summary>Parse with a typed outcome: a parse failure (G5-21 quarantine) is surfaced as
    /// <see cref="CalendarParseResult.Success"/> = false with <see cref="CalendarParseResult.Error"/>,
    /// rather than silently becoming an empty list. The caller (sync engine) must NOT advance
    /// past a resource that failed to parse — that would permanently lose the resource.</summary>
    public static CalendarParseResult TryToCalendarEvents(string raw, Guid calendarId, string? href, string? etag)
    {
        try
        {
            // Load is annotated nullable; a null here is a parse failure → caught below →
            // quarantined as CalendarParseResult (same outcome the old NRE path produced).
            var cal = Ical.Net.Calendar.Load(new StringReader(raw))
                      ?? throw new InvalidOperationException("Calendar.Load returned null.");
            var events = new List<CalendarEvent>();
            foreach (var ve in cal.Events)
            {
                events.Add(Map(ve, calendarId, href, etag, raw, cal.TimeZones.ToList()));
            }

            return new CalendarParseResult(Success: true, Events: events, Error: null, Href: href, ETag: etag);
        }
        catch (Exception ex)
        {
            // Covers BOTH calendar-load failures AND event-mapping failures. Mapping can now throw
            // when a TZID is not resolvable by .NET TimeZoneInfo (TimeZoneNotFoundException) — we no
            // longer silently reinterpret an unknown zone as Local. A malformed or unresolvable
            // resource must surface as a parse error so sync quarantines it, rather than crashing the
            // run or producing a silently-wrong time.
            return new CalendarParseResult(
                Success: false,
                Events: new List<CalendarEvent>(),
                Error: ex.Message,
                Href: href,
                ETag: etag);
        }
    }

    /// <summary>Serialize an event for upload. When raw data exists, edit it in place
    /// (preserving VTIMEZONE / X- / VALARM); otherwise build a fresh object.</summary>
    public static string Serialize(CalendarEvent evt)
    {
        if (!string.IsNullOrWhiteSpace(evt.ICalendarData))
        {
            return EditInPlace(evt.ICalendarData, evt);
        }

        return BuildICalendar(evt);
    }

    // ---- parse -------------------------------------------------------------

    private static CalendarEvent Map(IcalEvent ve, Guid calendarId, string? href, string? etag, string raw,
        IReadOnlyList<Ical.Net.CalendarComponents.VTimeZone> vTimeZones)
    {
        var start = ve.DtStart;
        var isAllDay = start is not null && !start.HasTime;
        var startUtc = ToUtc(start, vTimeZones);
        var endUtc = ResolveEnd(ve, start, startUtc, isAllDay, vTimeZones);

        return new CalendarEvent
        {
            CalendarId = calendarId,
            Uid = ve.Uid ?? string.Empty,
            RemoteHref = href,
            ETag = etag,
            ICalendarData = raw,
            Summary = ve.Summary ?? string.Empty,
            Description = string.IsNullOrEmpty(ve.Description) ? null : ve.Description,
            Location = string.IsNullOrEmpty(ve.Location) ? null : ve.Location,
            IsAllDay = isAllDay,
            StartUtc = startUtc,
            EndUtc = endUtc,
            TimeZoneId = start?.TzId,
            IsFloating = start is not null && start.IsFloating,
            RecurrenceRule = RuleToRrule(ve.RecurrenceRule),
            SyncStatus = SyncStatus.Clean,
        };
    }

    /// <summary>
    /// Converts an Ical.Net CalDateTime to a UTC DateTime WITHOUT relying on the library's
    /// AsUtc, which is unsafe for two of the three DATE-TIME forms in Ical.Net 5.2.3:
    ///  · FLOATING (no Z, no TZID): AsUtc keeps the wall-clock value and just stamps it UTC, so
    ///    a local 10:00 becomes 10:00Z and displays +offset. We interpret the wall-clock in the
    ///    user's local zone (ConvertTimeToUtc once) instead.
    ///  · TZID: AsUtc's behavior is platform/library-dependent (sometimes converts, sometimes
    ///    relabels), so deriving the time-of-day from it risks double conversion. We take
    ///    dt.Value (the raw civil fields, Unspecified) and convert exactly once via TimeZoneInfo.
    ///  · UTC-Z (T…Z): parsed with TzId=="UTC" and IsUtc==true; dt.Value is already the UTC
    ///    instant, we only attach Utc kind.
    /// Classification order matters: VALUE=DATE also has a null TzId (so Ical.Net reports
    /// IsFloating==true for it), therefore HasTime must be checked first. .NET 6+
    /// TimeZoneInfo accepts both IANA ("Asia/Shanghai") and Windows ("China Standard Time") IDs.
    /// </summary>
    internal static DateTime CalDateTimeToUtc(CalDateTime dt)
        => CalDateTimeToUtc(dt, TimeZoneInfo.Local);

    /// <summary>Overload with an injectable floating-timezone so tests are deterministic
    /// (independent of the machine's TimeZoneInfo.Local). Production callers use the single-arg
    /// entry point, which passes TimeZoneInfo.Local.</summary>
    internal static DateTime CalDateTimeToUtc(CalDateTime dt, TimeZoneInfo floatingTimeZone)
        => CalDateTimeToUtc(dt, floatingTimeZone, null);

    /// <summary>Full overload with the resource's VTIMEZONE components for the last-resort
    /// offset fallback (CQ-006).</summary>
    internal static DateTime CalDateTimeToUtc(CalDateTime dt, TimeZoneInfo floatingTimeZone, IReadOnlyList<Ical.Net.CalendarComponents.VTimeZone>? vTimeZones)
    {
        ArgumentNullException.ThrowIfNull(dt);
        ArgumentNullException.ThrowIfNull(floatingTimeZone);

        // VALUE=DATE: not a UTC DATE-TIME. Must be handled before IsFloating — Ical.Net reports
        // IsFloating==true for DATE because DATE carries no TZID.
        if (!dt.HasTime)
        {
            var date = new DateOnly(dt.Year, dt.Month, dt.Day);
            return AllDayDate.ToUtcInstant(date, floatingTimeZone).UtcDateTime;
        }

        // UTC-Z: Ical.Net 5.2.3 parses a Z-suffixed value with TzId=="UTC" and IsUtc==true.
        // dt.Value holds the original fields (Unspecified); just attach UTC semantics — no
        // timezone conversion.
        if (dt.IsUtc)
        {
            return DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc);
        }

        var civil = DateTime.SpecifyKind(dt.Value, DateTimeKind.Unspecified);

        // FLOATING DATE-TIME: interpret the wall-clock in the user's local timezone.
        if (dt.IsFloating)
        {
            return ConvertCivilToUtc(civil, floatingTimeZone);
        }

        // TZID DATE-TIME, three-tier resolution (CQ-006):
        //  1. system lookup on the literal ID (normal case),
        //  2. system lookup on the ID with a legacy global-form prefix stripped
        //     ("/mozilla.org/<build>/Europe/Berlin" → "Europe/Berlin"),
        //  3. fixed-offset conversion from the resource's own VTIMEZONE definition.
        // All three failing still throws — an unresolvable zone must surface as a parse
        // failure (quarantine), never silently reinterpret as the machine-local zone.
        if (TryResolveSystemTimeZone(dt.TzId!) is { } timeZone)
        {
            return ConvertCivilToUtc(civil, timeZone);
        }

        if (ConvertViaVTimeZone(dt, civil, vTimeZones) is { } viaVtz)
        {
            return viaVtz;
        }

        throw new TimeZoneNotFoundException($"Unknown TZID '{dt.TzId}' (no system match, no VTIMEZONE fallback).");
    }

    /// <summary>System time-zone lookup tolerating legacy global-form TZID prefixes
    /// (CQ-006). Returns null when no candidate resolves.</summary>
    internal static TimeZoneInfo? TryResolveSystemTimeZone(string tzId)
    {
        foreach (var candidate in TzIdCandidates(tzId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(candidate);
            }
            catch (Exception)
            {
                // TimeZoneNotFoundException (unknown id) or InvalidTimeZoneException —
                // try the next candidate.
            }
        }

        return null;
    }

    /// <summary>Candidate IDs to try against the system tzdb, most specific first. RFC 5545
    /// §3.8.7.5 allows a forward-slash-prefixed globally unique TZID; legacy producers
    /// (Mozilla Sunbird, old KOrganizer/freeassociation) embed their site — and sometimes a
    /// build number — in leading segments, e.g. "/mozilla.org/20050126_1/Europe/Berlin".
    /// Every trailing-segment suffix of the ID is tried, longest first, so any prefix depth
    /// resolves once the remainder is a plain IANA/Windows ID.</summary>
    private static IEnumerable<string> TzIdCandidates(string tzId)
    {
        yield return tzId;

        if (!tzId.StartsWith('/'))
        {
            yield break;
        }

        var rest = tzId.TrimStart('/');
        while (true)
        {
            var slash = rest.IndexOf('/');
            if (slash < 0)
            {
                yield break;
            }

            rest = rest[(slash + 1)..];
            if (rest.Length > 0)
            {
                yield return rest;
            }
        }
    }

    /// <summary>Last-resort conversion (CQ-006 tier 3): the TZID is unknown to the system
    /// even after prefix stripping, but the resource itself carries a VTIMEZONE defining the
    /// zone. Converts the civil time with the offset in effect at that instant per the
    /// VTIMEZONE's STANDARD/DAYLIGHT transitions. Returns null when no matching VTIMEZONE
    /// exists or its blocks yield no applicable offset.</summary>
    private static DateTime? ConvertViaVTimeZone(CalDateTime dt, DateTime civil, IReadOnlyList<Ical.Net.CalendarComponents.VTimeZone>? vTimeZones)
    {
        if (vTimeZones is null || vTimeZones.Count == 0)
        {
            return null;
        }

        var vtz = vTimeZones.FirstOrDefault(z => string.Equals(z.TzId, dt.TzId, StringComparison.Ordinal));
        if (vtz is null)
        {
            return null;
        }

        // Approximate the target instant as UTC (civil minus a provisional ±15h) — precise to
        // well within a DST-transition margin, which is months apart.
        var target = DateTime.SpecifyKind(civil.AddHours(-15), DateTimeKind.Utc);
        (DateTime Transition, TimeSpan Offset)? best = null;
        foreach (var block in vtz.TimeZoneInfos)
        {
            if (BlockLastTransitionUtc(block, target) is not { } transition)
            {
                continue;
            }

            if (best is null || transition > best.Value.Transition)
            {
                // A block without OffsetTo is malformed — treat as no offset (skip preference,
                // only chosen when no better block exists).
                best = (transition, block.OffsetTo is { } off ? BlockOffset(off) : TimeSpan.Zero);
            }
        }

        return best is { } applicable
            ? DateTime.SpecifyKind(civil.Subtract(applicable.Offset), DateTimeKind.Utc)
            : null;
    }

    /// <summary>Latest transition instant of a STANDARD/DAYLIGHT block at or before
    /// <paramref name="targetUtc"/> (UTC-approximate), or null when the block was never in
    /// effect by then. The block's own RRULE occurrences enumerate the transitions; blocks
    /// without a rule apply from their DTSTART. Enumeration is bounded (recent years + a
    /// hard iteration cap) because Ical.Net streams occurrences unbounded.</summary>
    private static DateTime? BlockLastTransitionUtc(Ical.Net.VTimeZoneInfo block, DateTime targetUtc)
    {
        var dtStartUtc = block.DtStart is { } ds
            ? DateTime.SpecifyKind(ds.Value, DateTimeKind.Utc)
            : DateTime.MinValue;
        if (dtStartUtc > targetUtc)
        {
            return null;
        }

        if (block.RecurrenceRule is null)
        {
            return dtStartUtc;
        }

        // DST cycles repeat at most yearly — looking back two years is always enough to find
        // the previous transition, and keeps the enumeration short for DAILY-ish edge rules.
        var windowStart = dtStartUtc > targetUtc.AddYears(-2) ? dtStartUtc : targetUtc.AddYears(-2);
        var last = (DateTime?)null;
        var guard = 0;
        foreach (var occ in block.GetOccurrences(
                     new CalDateTime(windowStart.Year, windowStart.Month, windowStart.Day, windowStart.Hour, windowStart.Minute, windowStart.Second, "UTC"),
                     new Ical.Net.Evaluation.EvaluationOptions()))
        {
            if (++guard > 2000)
            {
                break; // pathological rule — stop before this becomes a flood
            }

            var at = DateTime.SpecifyKind(occ.Period.StartTime.Value, DateTimeKind.Utc);
            if (at >= targetUtc)
            {
                break;
            }

            last = at;
        }

        // A rule window with no occurrence before target: the block was last in effect from
        // its own DTSTART (still ≤ target).
        return last ?? dtStartUtc;
    }

    /// <summary>Converts an iCalendar UTC offset value (+0530 / -0800) to a TimeSpan.</summary>
    private static TimeSpan BlockOffset(Ical.Net.DataTypes.UtcOffset offset)
    {
        var magnitude = new TimeSpan(Math.Abs(offset.Hours), Math.Abs(offset.Minutes), Math.Abs(offset.Seconds));
        return offset.Positive ? magnitude : magnitude.Negate();
    }

    /// <summary>Converts a civil (Unspecified-kind) local time to UTC, handling DST edge cases
    /// per RFC 5545 §3.3.5: ambiguous local times resolve to the FIRST occurrence (earlier UTC
    /// instant / larger offset); nonexistent (gap) local times use the offset in effect just
    /// before the gap. Plain ConvertTimeToUtc throws on invalid times, so we resolve explicitly.</summary>
    private static DateTime ConvertCivilToUtc(DateTime civil, TimeZoneInfo timeZone)
    {
        TimeSpan offset;

        if (timeZone.IsAmbiguousTime(civil))
        {
            // First occurrence = earlier UTC instant = the larger of the two offsets.
            var offsets = timeZone.GetAmbiguousTimeOffsets(civil);
            offset = offsets[0] >= offsets[1] ? offsets[0] : offsets[1];
        }
        else if (timeZone.IsInvalidTime(civil))
        {
            // Walk back to the last valid instant before the spring-forward gap and use its offset.
            var probe = civil.AddMinutes(-1);
            var floor = civil.AddHours(-2);
            while (timeZone.IsInvalidTime(probe) && probe > floor)
            {
                probe = probe.AddMinutes(-1);
            }

            if (timeZone.IsInvalidTime(probe))
            {
                // Pathological zone/gap — never crash the parse; best-effort direct conversion.
                return TimeZoneInfo.ConvertTimeToUtc(civil, timeZone);
            }

            offset = timeZone.GetUtcOffset(probe);
        }
        else
        {
            offset = timeZone.GetUtcOffset(civil);
        }

        return new DateTimeOffset(civil, offset).UtcDateTime;
    }

    private static DateTimeOffset ToUtc(CalDateTime? dt, IReadOnlyList<Ical.Net.CalendarComponents.VTimeZone>? vTimeZones)
    {
        if (dt is null)
        {
            return DateTimeOffset.UtcNow;
        }

        // All-day VALUE=DATE: map civil YYYYMMDD to local-midnight UTC so the date displays
        // as that same calendar day in TimeZoneInfo.Local (not floating UTC midnight).
        if (!dt.HasTime)
        {
            var date = new DateOnly(dt.Year, dt.Month, dt.Day);
            return AllDayDate.ToUtcInstant(date, TimeZoneInfo.Local);
        }

        return new DateTimeOffset(CalDateTimeToUtc(dt, TimeZoneInfo.Local, vTimeZones));
    }

    private static DateTimeOffset ResolveEnd(IcalEvent ve, CalDateTime? start, DateTimeOffset startUtc, bool isAllDay,
        IReadOnlyList<Ical.Net.CalendarComponents.VTimeZone>? vTimeZones)
    {
        // 1) Explicit DTEND wins.
        if (ve.DtEnd is not null)
        {
            return ToUtc(ve.DtEnd, vTimeZones);
        }

        // 2) DURATION: end = DTSTART + DURATION (RFC 5545 §3.6.1 — DTEND and DURATION are
        //    mutually exclusive but either may be present).
        if (ve.Duration is { } dur && dur.ToTimeSpanUnspecified() is { } ts && ts > TimeSpan.Zero)
        {
            return startUtc + ts;
        }

        // 3) RFC 5545 defaults when neither DTEND nor DURATION is present:
        //    all-day ⇒ exclusive next local midnight (DST-correct via AllDayDate), timed ⇒ zero-length.
        if (isAllDay && start is not null && !start.HasTime)
        {
            var next = new DateOnly(start.Year, start.Month, start.Day).AddDays(1);
            return AllDayDate.ToUtcInstant(next, TimeZoneInfo.Local);
        }

        return isAllDay ? startUtc.AddDays(1) : startUtc;
    }

    private static string? RuleToRrule(RecurrenceRule? p)
    {
        if (p is null)
        {
            return null;
        }

        var freq = p.Frequency switch
        {
            FrequencyType.Daily => "DAILY",
            FrequencyType.Weekly => "WEEKLY",
            FrequencyType.Monthly => "MONTHLY",
            FrequencyType.Yearly => "YEARLY",
            _ => null,
        };
        if (freq is null)
        {
            // CQ-005: HOURLY/MINUTELY/SECONDLY (and any other non-subset FREQ) are RFC-5545
            // legal but not creatable here. Pass the rule through in Ical.Net's canonical
            // form instead of nulling it: a non-null stored rule keeps IsRecurring true and
            // the expander walks the raw-ICS path, which supports every frequency. Fidelity
            // note: ToString() re-emits all parts but drops DEFAULT-valued ones (INTERVAL=1,
            // WKST=MO) and orders tokens canonically — semantically equivalent, not
            // byte-identical to the source text.
            var text = p.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        var tokens = new List<string> { $"FREQ={freq}", $"INTERVAL={Math.Max(1, p.Interval)}" };

        if (p.Count > 0)
        {
            tokens.Add($"COUNT={p.Count}");
        }
        else if (p.Until is { } until)
        {
            tokens.Add($"UNTIL={until.AsUtc:yyyyMMdd'T'HHmmss'Z'}");
        }

        if (p.ByDay is { Count: > 0 })
        {
            var days = p.ByDay
                .Where(d => d.Offset == 0)
                .Select(d => DayCode(d.DayOfWeek))
                .Distinct();
            tokens.Add("BYDAY=" + string.Join(",", days));
        }

        return string.Join(";", tokens);
    }

    private static string DayCode(DayOfWeek d) => d switch
    {
        DayOfWeek.Sunday => "SU",
        DayOfWeek.Monday => "MO",
        DayOfWeek.Tuesday => "TU",
        DayOfWeek.Wednesday => "WE",
        DayOfWeek.Thursday => "TH",
        DayOfWeek.Friday => "FR",
        DayOfWeek.Saturday => "SA",
        _ => "MO",
    };

    // ---- serialize ---------------------------------------------------------

    private static string BuildICalendar(CalendarEvent evt)
    {
        var cal = new Ical.Net.Calendar { Version = "2.0" };

        var ve = new IcalEvent
        {
            Uid = evt.Uid,
            Summary = evt.Summary,
            Description = evt.Description,
            Location = evt.Location,
            DtStart = BuildDateTime(evt, isStart: true),
            DtEnd = BuildDateTime(evt, isStart: false),
            DtStamp = new CalDateTime(DateTimeOffset.UtcNow.UtcDateTime, "UTC"),
        };

        if (evt.IsRecurring && CalDesk.Core.Recurrence.RecurrencePattern.Parse(evt.RecurrenceRule)?.ToRrule() is { } rrule)
        {
            ve.RecurrenceRule = new RecurrencePattern(rrule);
        }

        cal.Events.Add(ve);
        return new CalendarSerializer().SerializeToString(cal) ?? string.Empty;
    }

    private static string EditInPlace(string raw, CalendarEvent evt)
    {
        try
        {
            var cal = Ical.Net.Calendar.Load(new StringReader(raw));
            if (cal is null)
            {
                return BuildICalendar(evt);
            }

            var ve = cal.Events.FirstOrDefault(e => string.Equals(e.Uid, evt.Uid, StringComparison.Ordinal))
                     ?? cal.Events.FirstOrDefault();
            if (ve is null)
            {
                return BuildICalendar(evt);
            }

            ve.Summary = evt.Summary;
            ve.Description = evt.Description;
            ve.Location = evt.Location;
            ve.DtStart = BuildDateTime(evt, isStart: true);
            ve.DtEnd = BuildDateTime(evt, isStart: false);
            ApplyRecurrence(raw, ve, evt);

            return new CalendarSerializer().SerializeToString(cal) ?? string.Empty;
        }
        catch
        {
            return BuildICalendar(evt);
        }
    }

    /// <summary>
    /// Reconciles the VEVENT's RRULE with the locally stored rule (CQ-001/CQ-007). Invariant:
    /// only a rule the user actually changed may be rewritten; anything else keeps the raw
    /// RRULE node untouched so parts our subset cannot represent (BYMONTHDAY, unknown FREQ, …)
    /// survive the round-trip verbatim. "Unchanged" is recognized two ways:
    ///  1. the stored rule text equals the raw RRULE text (normalized: unfolded, uppercased,
    ///     token-sorted), or
    ///  2. the stored rule equals the pull-side projection of the raw rule — exactly what
    ///     RuleToRrule stored when sync pulled this resource, so a rule still equal to it
    ///     cannot have been edited since.
    /// Ambiguity (multiple RRULE lines, or textual extraction disagreeing with the parse)
    /// always resolves to "keep" — err toward preserving server data over rewriting it.
    /// </summary>
    private static void ApplyRecurrence(string raw, IcalEvent ve, CalendarEvent evt)
    {
        var rawTexts = ExtractRruleValues(raw, evt.Uid);
        var rawParsed = ve.RecurrenceRule;
        var projection = RuleToRrule(rawParsed);

        var ambiguous = rawTexts.Count > 1 || (rawTexts.Count == 0) != (rawParsed is null);
        if (ambiguous)
        {
            return;
        }

        if (!evt.IsRecurring)
        {
            if (rawParsed is null || projection is null)
            {
                // Either there is no rule anywhere, or the raw rule is unrepresentable
                // (e.g. FREQ=HOURLY, CQ-005): the pull side necessarily stored null, so a
                // null local rule means "unchanged since pull" — nulling here would destroy
                // the server-side series on a mere title edit.
                return;
            }

            ve.RecurrenceRule = null; // the user explicitly chose "does not repeat"
            return;
        }

        var localNorm = NormalizeRruleText(evt.RecurrenceRule);
        var unchanged = NormalizeRruleText(rawTexts.Count == 1 ? rawTexts[0] : null) == localNorm
                     || (projection is not null && NormalizeRruleText(projection) == localNorm);
        if (unchanged)
        {
            return;
        }

        // The rule genuinely changed → rewrite from our representable subset. A rule that
        // fails to parse leaves the raw node in place (conservative).
        if (CalDesk.Core.Recurrence.RecurrencePattern.Parse(evt.RecurrenceRule)?.ToRrule() is { } rrule)
        {
            ve.RecurrenceRule = new RecurrencePattern(rrule);
        }
    }

    /// <summary>Unfolds content lines per RFC 5545 §3.1: a line break followed by a single
    /// linear white-space character is removed (continuation). Line endings are normalized
    /// to '\n' first so CRLF, CR and LF inputs all work.</summary>
    private static string UnfoldLines(string s) =>
        s.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n ", string.Empty).Replace("\n\t", string.Empty);

    /// <summary>Normalizes an RRULE value for text comparison: unfold, drop a leading
    /// "RRULE:", trim and drop empty tokens, uppercase, sort. Order-insensitive on purpose —
    /// token order carries no semantics in RFC 5545.</summary>
    private static string? NormalizeRruleText(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule))
        {
            return null;
        }

        var value = UnfoldLines(rule.Trim());
        if (value.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["RRULE:".Length..];
        }

        var tokens = value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToUpperInvariant())
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
        return tokens.Count == 0 ? null : string.Join(";", tokens);
    }

    /// <summary>Extracts the RRULE values of the VEVENT selected the same way EditInPlace
    /// selects it (UID match, else the first VEVENT). Textual on purpose — the raw text is
    /// the only representation that still contains parts our model cannot parse.</summary>
    private static List<string> ExtractRruleValues(string raw, string uid)
    {
        var blocks = new List<(string? Uid, List<string> Rrules)>();
        string? curUid = null;
        List<string>? curRules = null;

        foreach (var rawLine in UnfoldLines(raw).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                curUid = null;
                curRules = new List<string>();
            }
            else if (line.StartsWith("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (curRules is not null)
                {
                    blocks.Add((curUid, curRules));
                }

                curUid = null;
                curRules = null;
            }
            else if (curRules is not null)
            {
                if (curUid is null && (line.StartsWith("UID:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("UID;", StringComparison.OrdinalIgnoreCase)))
                {
                    var colon = line.IndexOf(':');
                    if (colon >= 0)
                    {
                        curUid = line[(colon + 1)..].Trim();
                    }
                }
                else if (line.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
                {
                    curRules.Add(line["RRULE:".Length..].Trim());
                }
            }
        }

        var selected = blocks.Find(b => string.Equals(b.Uid, uid, StringComparison.Ordinal));
        if (selected.Rrules is null)
        {
            selected = blocks.Count > 0 ? blocks[0] : ((string?)null, new List<string>());
        }

        return selected.Rrules;
    }

    private static CalDateTime BuildDateTime(CalendarEvent evt, bool isStart)
    {
        var instant = isStart ? evt.StartUtc : evt.EndUtc;
        var utc = instant.UtcDateTime;

        if (evt.IsAllDay)
        {
            // DATE must come from the local civil day of the stored instant, not utc.Date
            // (e.g. +08:00 midnight 7/15 = 7/14 16:00Z → utc.Date would emit 20260714).
            var d = AllDayDate.ToLocalDate(instant, TimeZoneInfo.Local);
            return new CalDateTime(d.Year, d.Month, d.Day);
        }

        if (evt.IsFloating)
        {
            var local = utc.ToLocalTime();
            return new CalDateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, local.Second, null);
        }

        if (!string.IsNullOrEmpty(evt.TimeZoneId) && ResolveTz(evt.TimeZoneId) is { } tz)
        {
            var wall = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
            return new CalDateTime(wall.Year, wall.Month, wall.Day, wall.Hour, wall.Minute, wall.Second, evt.TimeZoneId);
        }

        return new CalDateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, "UTC");
    }

    private static TimeZoneInfo? ResolveTz(string tzId)
    {
        // Tolerates legacy global-form prefixed IDs too (CQ-006): an event edited after being
        // pulled from a legacy server keeps its original TZID; resolving it back keeps the
        // serialized DTSTART in the server's zone instead of silently relabeling it UTC.
        return string.IsNullOrEmpty(tzId) ? null : TryResolveSystemTimeZone(tzId);
    }
}

/// <summary>Typed outcome of <see cref="IcalMapper.TryToCalendarEvents"/>. When
/// <see cref="Success"/> is false, <see cref="Events"/> is empty and <see cref="Error"/>
/// describes the parse failure; the caller must NOT advance past this resource.</summary>
public sealed record CalendarParseResult(
    bool Success,
    IReadOnlyList<CalendarEvent> Events,
    string? Error,
    string? Href,
    string? ETag);
