namespace CalDesk.Core.Models;

/// <summary>Local sync state of an event relative to the remote calendar.</summary>
public enum SyncStatus
{
    /// <summary>In sync with the remote calendar.</summary>
    Clean,

    /// <summary>Created locally, not yet uploaded.</summary>
    NewLocal,

    /// <summary>Modified locally, pending upload.</summary>
    ModifiedLocal,

    /// <summary>Deleted locally, pending remote delete.</summary>
    DeletedLocal,

    /// <summary>Both local and remote changed since last sync — needs user resolution.</summary>
    Conflict,

    /// <summary>Last sync attempt failed (see SyncOperation.LastError).</summary>
    Error,
}

/// <summary>Kind of a queued sync operation.</summary>
public enum SyncOperationKind
{
    Create,
    Update,
    Delete,
}
