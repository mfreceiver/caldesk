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
        //    This branch also serves as the RFC 6578 §3.2 recovery path after an invalid token.
        if (cal.SupportsSyncToken)
        {
            var init = await client.SyncCollectionAsync(cal, null, ct).ConfigureAwait(false);
            if (init.Supported)
            {
                // fullSync: the response lists the *current* members, so the engine also
                // reconciles local rows the server no longer has (CQ-009②).
                await ApplySyncCollectionAsync(cal, init, report, ct, fullSync: true).ConfigureAwait(false);
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

    private async Task ApplySyncCollectionAsync(CalendarInfo cal, SyncCollectionResult sync, SyncReport report, CancellationToken ct, bool fullSync = false)
    {
        // CQ-009②: a full (empty-token) sync-collection response enumerates the *current*
        // members of the collection, so it doubles as the authoritative server state. Track
        // which hrefs are present — and which are unknown — so rows the server deleted while
        // we were offline can be reaped afterwards. Incremental responses are NOT
        // authoritative (an absent href is not a deletion there), so nothing is tracked.
        HashSet<string>? present = fullSync ? new(StringComparer.OrdinalIgnoreCase) : null;
        HashSet<string>? unresolved = fullSync ? new(StringComparer.OrdinalIgnoreCase) : null;

        foreach (var entry in sync.Changed)
        {
            await ApplyRemoteAsync(cal, entry, report, ct).ConfigureAwait(false);
            present?.Add(entry.Href);
        }

        foreach (var href in sync.Deleted)
        {
            await ApplyRemoteDeletionAsync(href, report, ct).ConfigureAwait(false);
        }

        if (sync.MissingDataHrefs is { Count: > 0 })
        {
            foreach (var href in sync.MissingDataHrefs)
            {
                // CQ-008: the server listed this href as changed but sent no calendar-data
                // (truncated response / non-compliant server). Skipping it here while still
                // adopting the new token would lose the change forever — the same G5-21
                // principle as the parse-failure quarantine in ApplyRemoteAsync. Refetch
                // the resource explicitly by href instead.
                RemoteEventEntry? entry;
                try
                {
                    entry = await client.GetEventAsync(cal, href, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Refetch failed: surface it like a parse failure. The token still
                    // advances (one unfetchable resource must not roll back the whole
                    // sync), but the href is recorded as *unresolved* so the full-sync
                    // reconciliation below cannot reap its local row — its server state is
                    // unknown, not "absent".
                    report.Errors.Add($"Refetch of '{href}' on calendar '{cal.DisplayName}' failed: {ex.Message}");
                    unresolved?.Add(href);
                    continue;
                }

                if (entry is null)
                {
                    // GET 404/410: the resource is gone after all — treat as a deletion.
                    await ApplyRemoteDeletionAsync(href, report, ct).ConfigureAwait(false);
                    continue;
                }

                await ApplyRemoteAsync(cal, entry, report, ct).ConfigureAwait(false);
                present?.Add(href);
            }
        }

        // Reconcile BEFORE adopting the new token: if reconciliation fails mid-way, the
        // token is not persisted and the next run repeats the full sync — the safe direction.
        // Persisting the token first would freeze unreaped zombies behind an incremental
        // token that never lists them again.
        if (fullSync)
        {
            await ReconcileFullSyncAsync(cal, present!, unresolved!, report, ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(sync.NewSyncToken))
        {
            cal.SyncToken = sync.NewSyncToken;
            cal.UpdatedAt = DateTimeOffset.UtcNow;
            await calendars.UpsertAsync(cal, ct).ConfigureAwait(false);
            await syncState.UpsertSyncTokenAsync(cal.Id, cal.SyncToken, ct).ConfigureAwait(false);
        }
    }

    /// <summary>CQ-009②: after a full (empty-token) sync, reap local rows the server no
    /// longer has. RFC 6578 full responses contain only current members, so a local row
    /// whose <c>RemoteHref</c> is absent from the response corresponds to a server-side
    /// deletion that happened while this client was offline (or held an invalidated token).
    /// Candidates are strictly guarded: only <see cref="SyncStatus.Clean"/> rows with a known
    /// RemoteHref — dirty rows (New/Modified/Conflict) carry pending local state that push
    /// owns, DeletedLocal rows have their own delete flow, Error rows are quarantine, and
    /// never-uploaded rows (RemoteHref null) have no server counterpart at all. Hrefs whose
    /// body-refetch failed are excluded (state unknown — reaping them would destroy data on
    /// a transient error).</summary>
    private async Task ReconcileFullSyncAsync(CalendarInfo cal, HashSet<string> present, HashSet<string> unresolved, SyncReport report, CancellationToken ct)
    {
        // All non-deleted rows of this calendar. The ±infinite window works around the
        // repository not having an unbounded query: ISO-8601 round-trip timestamps compare
        // chronologically as TEXT, so MinValue/MaxValue also cover rows outside the ±180d
        // pull window (GetInRangeAsync already excludes soft-deleted / DeletedLocal rows).
        var rows = await events.GetInRangeAsync([cal.Id], DateTimeOffset.MinValue, DateTimeOffset.MaxValue, ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            if (row.SyncStatus != SyncStatus.Clean || string.IsNullOrEmpty(row.RemoteHref))
            {
                continue;
            }

            if (present.Contains(row.RemoteHref) || unresolved.Contains(row.RemoteHref))
            {
                continue;
            }

            await events.HardDeleteAsync(row.Id, ct).ConfigureAwait(false);
            report.RemoteDeleted++;
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
            // CQ-014: a row with a known RemoteHref must always get the server-side DELETE —
            // servers are allowed to omit getetag (RFC 4791), and the old ETag-guard skipped
            // the DELETE while still counting Pushed++, so the next pull silently
            // resurrected the event. Without an ETag the DELETE is unconditional (null
            // If-Match); DeleteEventAsync already treats 404 as already-gone. Rows with no
            // RemoteHref were never uploaded — a local hard delete completes them.
            if (!string.IsNullOrEmpty(evt.RemoteHref))
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

        // CQ-003: the PUT round-trip spans hundreds of ms to seconds — long enough for the
        // user to edit or delete the event locally. Writing the pre-PUT snapshot back
        // unconditionally would clobber that edit AND (because EnqueueAsync REUSES the
        // still-pending op row) delete its outbox op in the same stroke — losing the edit on
        // both sides with no trace. Re-read the row and only write back when nothing moved.
        var current = await events.GetAsync(evt.Id, ct).ConfigureAwait(false);
        if (current is null)
        {
            // Hard-deleted while the PUT was in flight (EventService hard-deletes never-uploaded
            // rows; the events→outbox FK cascade already dropped the pending Create op). Do NOT
            // write the snapshot back — that would resurrect the event the user just deleted.
            // Retract the just-uploaded server copy instead: without this compensation the next
            // pull would re-import the event we just pushed, resurrecting it anyway.
            if (!string.IsNullOrEmpty(result.Href) && !string.IsNullOrEmpty(result.ETag))
            {
                try
                {
                    await client.DeleteEventAsync(cal, result.Href, result.ETag, ct).ConfigureAwait(false);
                }
                catch (CalDavException ex)
                {
                    // Best effort: the local row is gone either way; surface the orphan instead
                    // of silently pretending the server state is clean.
                    report.Errors.Add($"Compensating delete after mid-PUT deletion of '{evt.Uid}' failed: {ex.Message}");
                }
            }

            // No-op when the FK cascade already removed it; keeps fake/in-memory stores tidy.
            await operations.RemoveAsync(op.Id, ct).ConfigureAwait(false);
            return;
        }

        if (current.UpdatedAt > evt.UpdatedAt || current.SyncStatus == SyncStatus.DeletedLocal)
        {
            // Concurrent edit (or soft delete — its UpdatedAt bump covers the same check, the
            // status test is defense in depth). Keep the user's row exactly as stored; merge
            // only the server assignment (href/etag) and KEEP the reused op row so the edit is
            // pushed on the next run. Marking Clean / removing the op here is precisely the
            // silent two-sided loss CQ-003 describes.
            var etag = !string.IsNullOrEmpty(result.ETag) ? result.ETag : current.ETag;
            await events.UpdateSyncFieldsAsync(evt.Id, result.Href, etag, ct).ConfigureAwait(false);
            return;
        }

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
        // first. Surface as conflict rather than retrying. The typed StatusCode is the only
        // signal — never sniff exception message text (CalDavException always carries the code).
        if (ex.StatusCode == 412)
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
