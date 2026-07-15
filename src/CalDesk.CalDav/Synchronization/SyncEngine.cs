using CalDesk.CalDav.Calendar;
using CalDesk.Core.Models;
using CalDesk.Core.Services;

namespace CalDesk.CalDav.Synchronization;

/// <summary>Summary of a single sync run.</summary>
public sealed class SyncReport
{
    public int Pulled { get; set; }
    public int Pushed { get; set; }
    public int RemoteDeleted { get; set; }
    public List<Guid> ConflictEventIds { get; } = new();
    public List<string> Errors { get; } = new();

    public bool HasConflicts => ConflictEventIds.Count > 0;
}

/// <summary>Coordinates pull (server → local) and push (local → server) for a single account.
/// Conflict policy (G4-16): never silently overwrite — when both sides changed, mark the
/// local event <see cref="SyncStatus.Conflict"/> and surface it for user resolution.</summary>
public sealed class SyncEngine(
    IEventRepository events,
    ICalendarRepository calendars,
    ISyncStateRepository syncState,
    ISyncOperationRepository operations,
    ICalDavClient client)
{
    private static readonly TimeSpan PullWindowBack = TimeSpan.FromDays(180);
    private static readonly TimeSpan PullWindowForward = TimeSpan.FromDays(180);

    public async Task<SyncReport> SyncAsync(CancellationToken ct = default)
    {
        var report = new SyncReport();
        var cals = await calendars.GetAllAsync(ct).ConfigureAwait(false);

        foreach (var cal in cals)
        {
            await PullAsync(cal, report, ct).ConfigureAwait(false);
        }

        await PushAsync(report, ct).ConfigureAwait(false);
        return report;
    }

    // ---- pull ---------------------------------------------------------------

    private async Task PullAsync(CalendarInfo cal, SyncReport report, CancellationToken ct)
    {
        // 1) Incremental: we already hold a baseline token from a previous sync.
        if (!string.IsNullOrEmpty(cal.SyncToken) && cal.SupportsSyncToken)
        {
            var sync = await client.SyncCollectionAsync(cal, cal.SyncToken, ct).ConfigureAwait(false);
            if (sync.Supported)
            {
                foreach (var entry in sync.Changed)
                {
                    await ApplyRemoteAsync(cal, entry, report, ct).ConfigureAwait(false);
                }

                foreach (var href in sync.Deleted)
                {
                    await ApplyRemoteDeletionAsync(href, report, ct).ConfigureAwait(false);
                }

                if (!string.IsNullOrEmpty(sync.NewSyncToken))
                {
                    cal.SyncToken = sync.NewSyncToken;
                    cal.UpdatedAt = DateTimeOffset.UtcNow;
                    await calendars.UpsertAsync(cal, ct).ConfigureAwait(false);
                    await syncState.UpsertSyncTokenAsync(cal.Id, cal.SyncToken, ct).ConfigureAwait(false);
                }

                return;
            }
        }

        // 2) Initial population: sync-collection with an empty token returns the full current
        //    state plus a token we save for future incremental syncs (detects deletions later).
        if (cal.SupportsSyncToken)
        {
            var init = await client.SyncCollectionAsync(cal, null, ct).ConfigureAwait(false);
            if (init.Supported)
            {
                foreach (var entry in init.Changed)
                {
                    await ApplyRemoteAsync(cal, entry, report, ct).ConfigureAwait(false);
                }

                foreach (var href in init.Deleted)
                {
                    await ApplyRemoteDeletionAsync(href, report, ct).ConfigureAwait(false);
                }

                if (!string.IsNullOrEmpty(init.NewSyncToken))
                {
                    cal.SyncToken = init.NewSyncToken;
                    cal.UpdatedAt = DateTimeOffset.UtcNow;
                    await calendars.UpsertAsync(cal, ct).ConfigureAwait(false);
                    await syncState.UpsertSyncTokenAsync(cal.Id, cal.SyncToken, ct).ConfigureAwait(false);
                }

                return;
            }
        }

        // 3) Fallback for servers without sync-collection: wide calendar-query (no deletion detection).
        var start = DateTimeOffset.UtcNow - PullWindowBack;
        var end = DateTimeOffset.UtcNow + PullWindowForward;
        var query = await client.QueryEventsAsync(cal, start, end, ct).ConfigureAwait(false);
        foreach (var entry in query.Entries)
        {
            await ApplyRemoteAsync(cal, entry, report, ct).ConfigureAwait(false);
        }
    }

    private async Task ApplyRemoteAsync(CalendarInfo cal, RemoteEventEntry entry, SyncReport report, CancellationToken ct)
    {
        var parsed = IcalMapper.ToCalendarEvents(entry.ICalendarData, cal.Id, entry.Href, entry.ETag);
        if (parsed.Count == 0)
        {
            return;
        }

        var remote = parsed[0];
        var local = await events.FindByRemoteHrefAsync(entry.Href, ct).ConfigureAwait(false);

        if (local is null)
        {
            // A locally-created event with the same UID (created on another device)? => conflict.
            var byUid = await events.FindByUidAsync(cal.Id, remote.Uid, ct).ConfigureAwait(false);
            if (byUid is { SyncStatus: SyncStatus.NewLocal or SyncStatus.ModifiedLocal })
            {
                byUid.SyncStatus = SyncStatus.Conflict;
                byUid.UpdatedAt = DateTimeOffset.UtcNow;
                await events.UpsertAsync(byUid, ct).ConfigureAwait(false);
                report.ConflictEventIds.Add(byUid.Id);
                return;
            }

            remote.SyncStatus = SyncStatus.Clean;
            await events.UpsertAsync(remote, ct).ConfigureAwait(false);
            report.Pulled++;
            return;
        }

        switch (local.SyncStatus)
        {
            case SyncStatus.DeletedLocal:
                // Pending local delete; let push handle it. Do not resurrect.
                return;

            case SyncStatus.Clean:
            case SyncStatus.Error:
                if (!string.Equals(entry.ETag, local.ETag, StringComparison.Ordinal))
                {
                    CopyEventFields(local, remote);
                    local.SyncStatus = SyncStatus.Clean;
                    local.UpdatedAt = DateTimeOffset.UtcNow;
                    await events.UpsertAsync(local, ct).ConfigureAwait(false);
                    report.Pulled++;
                }

                return;

            case SyncStatus.NewLocal:
            case SyncStatus.ModifiedLocal:
                if (!string.Equals(entry.ETag, local.ETag, StringComparison.Ordinal))
                {
                    local.SyncStatus = SyncStatus.Conflict;
                    local.UpdatedAt = DateTimeOffset.UtcNow;
                    await events.UpsertAsync(local, ct).ConfigureAwait(false);
                    report.ConflictEventIds.Add(local.Id);
                }

                return;
        }
    }

    private async Task ApplyRemoteDeletionAsync(string href, SyncReport report, CancellationToken ct)
    {
        var local = await events.FindByRemoteHrefAsync(href, ct).ConfigureAwait(false);
        if (local is null)
        {
            return;
        }

        if (local.SyncStatus is SyncStatus.Clean or SyncStatus.Error)
        {
            await events.HardDeleteAsync(local.Id, ct).ConfigureAwait(false);
            report.RemoteDeleted++;
        }
        else if (local.SyncStatus is SyncStatus.NewLocal or SyncStatus.ModifiedLocal)
        {
            local.SyncStatus = SyncStatus.Conflict;
            local.UpdatedAt = DateTimeOffset.UtcNow;
            await events.UpsertAsync(local, ct).ConfigureAwait(false);
            report.ConflictEventIds.Add(local.Id);
        }
    }

    // ---- push --------------------------------------------------------------

    private async Task PushAsync(SyncReport report, CancellationToken ct)
    {
        var due = await operations.GetDueAsync(100, ct).ConfigureAwait(false);
        foreach (var op in due)
        {
            var evt = await events.GetAsync(op.EventId, ct).ConfigureAwait(false);
            if (evt is null)
            {
                await operations.RemoveAsync(op.Id, ct).ConfigureAwait(false);
                continue;
            }

            var cal = await calendars.GetAsync(evt.CalendarId, ct).ConfigureAwait(false);
            if (cal is null)
            {
                await FailOpAsync(op, "calendar not found", report, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                if (op.Kind == SyncOperationKind.Delete)
                {
                    if (!string.IsNullOrEmpty(evt.RemoteHref) && evt.ETag is not null)
                    {
                        await client.DeleteEventAsync(cal, evt.RemoteHref, evt.ETag, ct).ConfigureAwait(false);
                    }

                    await events.HardDeleteAsync(evt.Id, ct).ConfigureAwait(false);
                    await operations.RemoveAsync(op.Id, ct).ConfigureAwait(false);
                    report.Pushed++;
                    continue;
                }

                var ifMatch = op.Kind == SyncOperationKind.Update ? evt.ETag : null;
                var result = await client.PutEventAsync(cal, evt, ifMatch, ct).ConfigureAwait(false);
                evt.RemoteHref = result.Href;
                if (!string.IsNullOrEmpty(result.ETag))
                {
                    evt.ETag = result.ETag;
                }

                evt.SyncStatus = SyncStatus.Clean;
                evt.UpdatedAt = DateTimeOffset.UtcNow;
                await events.UpsertAsync(evt, ct).ConfigureAwait(false);
                await operations.RemoveAsync(op.Id, ct).ConfigureAwait(false);
                report.Pushed++;
            }
            catch (CalDavException ex)
            {
                if (ex.Message.Contains("412", StringComparison.Ordinal))
                {
                    // Optimistic-concurrency mismatch: another device changed it first.
                    evt.SyncStatus = SyncStatus.Conflict;
                    evt.UpdatedAt = DateTimeOffset.UtcNow;
                    await events.UpsertAsync(evt, ct).ConfigureAwait(false);
                    await operations.RemoveAsync(op.Id, ct).ConfigureAwait(false);
                    report.ConflictEventIds.Add(evt.Id);
                }
                else
                {
                    evt.SyncStatus = SyncStatus.Error;
                    evt.UpdatedAt = DateTimeOffset.UtcNow;
                    await events.UpsertAsync(evt, ct).ConfigureAwait(false);
                    await FailOpAsync(op, ex.Message, report, ct).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task FailOpAsync(SyncOperation op, string error, SyncReport report, CancellationToken ct)
    {
        op.AttemptCount++;
        op.LastError = error;
        op.NextAttemptAt = DateTimeOffset.UtcNow + Backoff(op.AttemptCount);
        report.Errors.Add(error);
        await operations.MarkAttemptedAsync(op.Id, error, op.NextAttemptAt, ct).ConfigureAwait(false);
    }

    private static TimeSpan Backoff(int attempt)
    {
        var minutes = Math.Min(Math.Pow(2, attempt), 30);
        return TimeSpan.FromMinutes(minutes);
    }

    private static void CopyEventFields(CalendarEvent target, CalendarEvent source)
    {
        target.Uid = source.Uid;
        target.Summary = source.Summary;
        target.Description = source.Description;
        target.Location = source.Location;
        target.IsAllDay = source.IsAllDay;
        target.StartUtc = source.StartUtc;
        target.EndUtc = source.EndUtc;
        target.TimeZoneId = source.TimeZoneId;
        target.IsFloating = source.IsFloating;
        target.RecurrenceRule = source.RecurrenceRule;
        target.RecurrenceId = source.RecurrenceId;
        target.ICalendarData = source.ICalendarData;
        target.ETag = source.ETag;
        if (!string.IsNullOrEmpty(source.RemoteHref))
        {
            target.RemoteHref = source.RemoteHref;
        }
    }
}
