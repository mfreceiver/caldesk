namespace CalDesk.Core.Models;

/// <summary>A queued outbound sync operation targeting the remote calendar.</summary>
public sealed class SyncOperation
{
    public long Id { get; set; }

    public Guid EventId { get; set; }

    public SyncOperationKind Kind { get; set; }

    public int AttemptCount { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Next allowed attempt time (exponential backoff).</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }
}
