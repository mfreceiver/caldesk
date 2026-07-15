using System.Text.RegularExpressions;
using CalDesk.App.Models;
using CalDesk.Core.Models;
using CalDesk.Core.Recurrence;
using CalDesk.Storage;

namespace CalDesk.App.Services;

/// <summary>Fires desktop notifications before events start. Honors the event's first
/// VALARM TRIGGER when present, otherwise the configured default reminder (G6-26).</summary>
public sealed class ReminderService : IDisposable
{
    private static readonly Regex TriggerRegex = new(
        @"TRIGGER[^:\r\n]*:(-?)(P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly StorageServices _store;
    private readonly IRecurrenceExpander _expander;
    private readonly Func<AppSettings> _settings;
    private readonly Action<Notification> _notify;
    private readonly Timer _timer;
    private readonly HashSet<string> _fired = new();

    public ReminderService(StorageServices store, IRecurrenceExpander expander, Func<AppSettings> settings, Action<Notification> notify)
    {
        _store = store;
        _expander = expander;
        _settings = settings;
        _notify = notify;
        _timer = new Timer(_ => _ = CheckAsync(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30));
    }

    public sealed record Notification(string Title, string Body, Guid EventId);

    private async Task CheckAsync()
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var horizon = now.AddHours(36);
            var calendars = await _store.Calendars.GetAllAsync();
            var calIds = calendars.Select(c => c.Id).ToArray();
            if (calIds.Length == 0)
            {
                return;
            }

            var calMap = calendars.ToDictionary(c => c.Id);
            var events = await _store.Events.GetInRangeAsync(calIds, now, horizon);
            var defaultMinutes = Math.Max(0, _settings().DefaultReminderMinutes);

            foreach (var evt in events)
            {
                if (evt.SyncStatus == SyncStatus.DeletedLocal)
                {
                    continue;
                }

                calMap.TryGetValue(evt.CalendarId, out var cal);
                var offsetMinutes = ParseTriggerMinutes(evt) ?? defaultMinutes;
                if (offsetMinutes <= 0)
                {
                    continue;
                }

                foreach (var occ in _expander.Expand(evt, cal ?? new CalendarInfo(), now, horizon))
                {
                    var fire = occ.StartUtc.AddMinutes(-offsetMinutes);
                    if (fire <= now && now < fire.AddMinutes(2) && now <= occ.StartUtc)
                    {
                        var key = $"{evt.Uid}@{occ.StartUtc:O}";
                        if (_fired.Add(key))
                        {
                            var local = TimeZoneInfo.ConvertTime(occ.StartUtc, TimeZoneInfo.Local);
                            _notify(new Notification(evt.Summary, local.ToString("HH:mm") + " 开始", evt.Id));
                        }
                    }
                }
            }
        }
        catch
        {
            // Reminders are best-effort; never crash the host.
        }
    }

    /// <summary>Best-effort parse of the first VALARM TRIGGER (ISO-8601 duration) → minutes before start.</summary>
    internal static int? ParseTriggerMinutes(CalendarEvent evt)
    {
        var raw = evt.ICalendarData;
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        var m = TriggerRegex.Match(raw);
        if (!m.Success || string.IsNullOrEmpty(m.Groups[2].Value))
        {
            return null;
        }

        var sign = m.Groups[1].Value == "-" ? -1 : 1;
        if (!TryDurationMinutes(m, out var body))
        {
            return null;
        }

        // A TRIGGER before start is negative; "minutes before start" is its magnitude.
        return sign < 0 ? body : -body;
    }

    private static bool TryDurationMinutes(Match m, out int minutes)
    {
        minutes = 0;
        int weeks = Group(m, 3), days = Group(m, 4), hours = Group(m, 5), mins = Group(m, 6), secs = Group(m, 7);
        if (weeks == 0 && days == 0 && hours == 0 && mins == 0 && secs == 0)
        {
            return false;
        }

        minutes = weeks * 7 * 24 * 60 + days * 24 * 60 + hours * 60 + mins + (int)Math.Round(secs / 60.0);
        return true;
    }

    private static int Group(Match m, int index) =>
        int.TryParse(m.Groups[index].Value, out var v) ? v : 0;

    public void Dispose() => _timer.Dispose();
}
