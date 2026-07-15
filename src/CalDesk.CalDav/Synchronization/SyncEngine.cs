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

    /// <summary>DAV precondition token signalling that the supplied sync-token is no longer
    /// valid and the client must perform a full re-sync (RFC 6578 §3.2).</summary>
    private const string ValidSyncTokenPrecondition = "valid-sync-token";

    public async Task<SyncReport> SyncAsync(CancellationToken ct = default)
    {
        var report = new SyncReport();
        var cals = await calendars.GetAllAsync(ct).ConfigureAwait(false);

        // Per-calendar isolation (G5-22): one calendar failing must not abort the whole run.
        foreach (var cal in cals)
        {
            try
            {
                await PullAsync(cal, report, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                report.Errors.Add($"Calendar '{cal.DisplayName}' pull failed: {ex.Message}");
            }
        }

        try
        {
            await PushAsync(report, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A fatal push-side failure is recorded but does not invalidate the pull results.
            report.Errors.Add($"Push failed: {ex.Message}");
        }
        return report;
    }

    // ---- pull ---------------------------------------------------------------

    private async Task PullAsync(CalendarInfo cal, SyncReport report, CancellationToken ct)
    {
        // 1) Incremental: we already hold a baseline token from a previous sync.
        if (!string.IsNullOrEmpty(cal.SyncToken) && cal.SupportsSyncToken)
        {
            SyncCollectionResult? sync;
            try
            {
                sync = await client.SyncCollectionAsync(cal, cal.SyncToken, ct).ConfigureAwait(false);
            }
            catch (CalDavException ex) when (IsInvalidTokenError(ex))
            {
                // The server rejected the stored token (DAV precondition `valid-sync-token`
                // failed, typically HTTP 403/409). Per RFC 6578 §3.2 we MUST discard the token
                // and re-pull the full collection. Transient/network errors do NOT enter this
                // branch — they propagate up so the per-calendar isolation handler records
                // them and we retry on the next run without losing the token.
                await ClearSyncTokenAsync(cal, ct).ConfigureAwait(false);
                report.Errors.Add(
                    $"Calendar '{cal.DisplayName}' sync token rejected ({ex.StatusCode ?? 0}); full re-sync performed.");
                sync = null;
            }

            if (sync is not null)
            {
                if (sync.Supported)
                {
                    await ApplySyncCollectionAsync(cal, sync, report, ct).ConfigureAwait(false);
                    return;
                }
                // else: server no longer advertises sync-collection — fall through to fallback.
            }
            // else: token was invalid → fall through to the initial-pull branch below.
        }

        // 2) Initial population: sync-collection with an empty token returns the full current
        //    state plus a token we save for future incremental syncs (detects deletions later).
        if (cal.SupportsSyncToken)
        {
            var init = await client.SyncCollectionAsync(cal, null, ct).ConfigureAwait(false);
            if (init.Supported)
            {
                await ApplySyncCollectionAsync(cal, init, report, ct).ConfigureAwait(false);
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

    private async Task ApplySyncCollectionAsync(CalendarInfo cal, SyncCollectionResult sync, SyncReport report, CancellationToken ct)
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
    }

    /// <summary>Discard the stored sync-token (both on the calendar row and in the sync-state
    /// store) so the next pull falls back to a full re-sync.</summary>
    private async Task ClearSyncTokenAsync(CalendarInfo cal, CancellationToken ct)
    {
        cal.SyncToken = null;
        cal.UpdatedAt = DateTimeOffset.UtcNow;
        await calendars.UpsertAsync(cal, ct).ConfigureAwait(false);
        await syncState.UpsertSyncTokenAsync(cal.Id, null, ct).ConfigureAwait(false);
    }

    /// <summary>True for a DAV <c>valid-sync-token</c> precondition failure (HTTP 403/409, or
    /// an explicit precondition header). Detection is based on the typed
    /// <see cref="CalDavException.StatusCode"/> / <see cref="CalDavException.Precondition"/>
    /// properties — never on the exception message text.</summary>
    private static bool IsInvalidTokenError(CalDavException ex)
    {
        if (!string.IsNullOrEmpty(ex.Precondition)
            && ex.Precondition.Contains(ValidSyncTokenPrecondition, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ex.StatusCode == 403 || ex.StatusCode == 409;
    }

    private async Task ApplyRemoteAsync(CalendarInfo cal, RemoteEventEntry entry, SyncReport report, CancellationToken ct)
    {
        // Parse-failure quarantine (G5-21): a malformed VEVENT must NOT be silently dropped —
        // doing so would let sync advance its token and permanently lose the resource. Surface
        // it as an error and persist the raw resource so the user can inspect / repair it.
        var parsed = IcalMapper.TryToCalendarEvents(entry.ICalendarData, cal.Id, entry.Href, entry.ETag);
        if (!parsed.Success)
        {
            report.Errors.Add(
                $"Failed to parse resource '{entry.Href}' on calendar '{cal.DisplayName}': {parsed.Error}");
            await events.UpsertAsync(new CalendarEvent
            {
                CalendarId = cal.Id,
                Uid = Guid.NewGuid().ToString("N"),
                RemoteHref = entry.Href,
                ETag = entry.ETag,
                ICalendarData = entry.ICalendarData,
                Summary = "(unparseable resource)",
                StartUtc = DateTimeOffset.UtcNow,
                EndUtc = DateTimeOffset.UtcNow,
                SyncStatus = SyncStatus.Error,
            }, ct).ConfigureAwait(false);
            return;
        }

        if (parsed.Events.Count == 0)
        {
            return;
        }

        var remote = parsed.Events[0];
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
            // Per-operation isolation: a failure here (HTTP, timeout, XML, codec) is recorded
            // but does not abort the whole push loop.
            try
            {
                await PushOneAsync(op, report, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (CalDavException ex)
            {
                await HandlePushDavErrorAsync(op, ex, report, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await FailOpAsync(op, ex.Message, report, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task PushOneAsync(SyncOperation op, SyncReport report, CancellationToken ct)
    {
        var evt = await events.GetAsync(op.EventId, ct).ConfigureAwait(false);
        if (evt is null)
        {
            await operations.RemoveAsync(op.Id, ct).ConfigureAwait(false);
            return;
        }

        var cal = await calendars.GetAsync(evt.CalendarId, ct).ConfigureAwait(false);
        if (cal is null)
        {
            await FailOpAsync(op, "calendar not found", report, ct).ConfigureAwait(false);
            return;
        }

        if (op.Kind == SyncOperationKind.Delete)
        {
            if (!string.IsNullOrEmpty(evt.RemoteHref) && evt.ETag is not null)
            {
                await client.DeleteEventAsync(cal, evt.RemoteHref, evt.ETag, ct).ConfigureAwait(false);
            }

            await events.HardDeleteAsync(evt.Id, ct).ConfigureAwait(false);
            await operations.RemoveAsync(op.Id, ct).ConfigureAwait(false);
            report.Pushed++;
            return;
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

    private async Task HandlePushDavErrorAsync(SyncOperation op, CalDavException ex, SyncReport report, CancellationToken ct)
    {
        var evt = await events.GetAsync(op.EventId, ct).ConfigureAwait(false);
        if (evt is null)
        {
            await FailOpAsync(op, ex.Message, report, ct).ConfigureAwait(false);
            return;
        }

        // 412 Precondition Failed ⇒ optimistic-concurrency mismatch: another device changed it
        // first. Surface as conflict rather than retrying.
        if (ex.StatusCode == 412 || ex.Message.Contains(" 412 ", StringComparison.Ordinal))
        {
            evt.SyncStatus = SyncStatus.Conflict;
            evt.UpdatedAt = DateTimeOffset.UtcNow;
            await events.UpsertAsync(evt, ct).ConfigureAwait(false);
            await operations.RemoveAsync(op.Id, ct).ConfigureAwait(false);
            report.ConflictEventIds.Add(evt.Id);
            return;
        }

        evt.SyncStatus = SyncStatus.Error;
        evt.UpdatedAt = DateTimeOffset.UtcNow;
        await events.UpsertAsync(evt, ct).ConfigureAwait(false);
        await FailOpAsync(op, ex.Message, report, ct).ConfigureAwait(false);
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
