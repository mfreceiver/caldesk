using CalDesk.Core.Models;

namespace CalDesk.Core.Services;

/// <summary>Application service for local-first event mutations.
/// Writes hit SQLite immediately (UI can refresh at once), enqueue a sync operation,
/// and leave the actual remote PUT/DELETE to the sync engine.</summary>
public sealed class EventService(IEventRepository events, ISyncOperationRepository operations)
{
    /// <summary>Create a new event locally and queue it for upload.</summary>
    public async Task<CalendarEvent> CreateAsync(CalendarEvent evt, CancellationToken ct = default)
    {
        if (evt.Id == Guid.Empty)
        {
            evt.Id = Guid.NewGuid();
        }

        if (string.IsNullOrEmpty(evt.Uid))
        {
            evt.Uid = $"{evt.Id}@caldesk";
        }

        evt.SyncStatus = SyncStatus.NewLocal;
        evt.DeletedAt = null;
        var now = DateTimeOffset.UtcNow;
        evt.CreatedAt = now;
        evt.UpdatedAt = now;

        await events.UpsertAsync(evt, ct).ConfigureAwait(false);
        await operations.EnqueueAsync(new SyncOperation { EventId = evt.Id, Kind = SyncOperationKind.Create }, ct).ConfigureAwait(false);
        return evt;
    }

    /// <summary>Update an existing event locally and queue it for upload.</summary>
    public async Task<CalendarEvent> UpdateAsync(CalendarEvent evt, CancellationToken ct = default)
    {
        evt.SyncStatus = SyncStatus.ModifiedLocal;
        evt.UpdatedAt = DateTimeOffset.UtcNow;

        await events.UpsertAsync(evt, ct).ConfigureAwait(false);
        await operations.EnqueueAsync(new SyncOperation { EventId = evt.Id, Kind = SyncOperationKind.Update }, ct).ConfigureAwait(false);
        return evt;
    }

    /// <summary>Soft-delete locally and queue a remote delete.
    /// Events that never reached the server are hard-deleted immediately.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var evt = await events.GetAsync(id, ct).ConfigureAwait(false);
        if (evt is null)
        {
            return;
        }

        if (evt.SyncStatus == SyncStatus.NewLocal)
        {
            await events.HardDeleteAsync(id, ct).ConfigureAwait(false);
            return;
        }

        evt.SyncStatus = SyncStatus.DeletedLocal;
        evt.DeletedAt = DateTimeOffset.UtcNow;
        evt.UpdatedAt = evt.DeletedAt.Value;
        await events.UpsertAsync(evt, ct).ConfigureAwait(false);
        await operations.EnqueueAsync(new SyncOperation { EventId = evt.Id, Kind = SyncOperationKind.Delete }, ct).ConfigureAwait(false);
    }
}
