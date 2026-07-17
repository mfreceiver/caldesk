using CalDesk.Core.Models;
using CalDesk.Core.Services;
using Microsoft.Data.Sqlite;

namespace CalDesk.Storage;

internal sealed class SyncOperationRepository : ISyncOperationRepository
{
    private readonly CalDeskDatabase _db;

    public SyncOperationRepository(CalDeskDatabase db) => _db = db;

    /// <summary>Enqueues a sync operation, coalescing against any already-pending op for the same
    /// event so repeated edits do not stack duplicates (Create+Update→Create, Update+Update→Update,
    /// Update+Delete→Delete, Create+Delete→drop). When a row already exists it is reused: its kind is
    /// set to the merged value and its retry state is reset so the new edit is retried promptly.
    /// Assigns the resulting row id back onto <paramref name="op"/> (0 when the op was dropped).</summary>
    public async Task EnqueueAsync(SyncOperation op, CancellationToken ct = default)
    {
        await using var lease = await _db.LeaseCommandAsync();

        // 1) Is there already a pending op for this event?
        long? existingId = null;
        SyncOperationKind? existingKind = null;
        await using (var sel = lease.CreateCommand())
        {
            sel.CommandText = """
                SELECT id, operation
                FROM sync_operations
                WHERE event_id = @eid
                ORDER BY id DESC
                LIMIT 1
                """;
            sel.Parameters.AddWithValue("@eid", DbConvert.ToText(op.EventId));
            await using var reader = await sel.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                existingId = DbConvert.ToLong(reader["id"]);
                existingKind = DbConvert.TextToEnum<SyncOperationKind>(reader["operation"]);
            }
        }

        // 2) No pending op: insert a fresh row.
        if (existingId is null)
        {
            await using var ins = lease.CreateCommand();
            ins.CommandText = """
                INSERT INTO sync_operations (event_id, operation, attempt_count, last_error, created_at, next_attempt_at)
                VALUES (@event_id, @operation, @attempt_count, @last_error, @created_at, @next_attempt_at);
                SELECT last_insert_rowid();
                """;
            BindOp(ins, op);
            op.Id = DbConvert.ToLong(await ins.ExecuteScalarAsync(ct));
            return;
        }

        // 3) Coalesce with the existing row per the transition table.
        var merged = Merge(existingKind!.Value, op.Kind);
        if (merged is null)
        {
            // e.g. Create-then-Delete before any upload: nothing left to push.
            await using var del = lease.CreateCommand();
            del.CommandText = "DELETE FROM sync_operations WHERE id = @id";
            del.Parameters.AddWithValue("@id", existingId);
            await del.ExecuteNonQueryAsync(ct);
            op.Id = 0;
            return;
        }

        // Reuse the existing row, refresh the kind and reset retry state.
        await using var upd = lease.CreateCommand();
        upd.CommandText = """
            UPDATE sync_operations
            SET operation = @operation,
                attempt_count = 0,
                last_error = NULL,
                next_attempt_at = NULL
            WHERE id = @id
            """;
        upd.Parameters.AddWithValue("@operation", DbConvert.EnumToText(merged.Value));
        upd.Parameters.AddWithValue("@id", existingId);
        await upd.ExecuteNonQueryAsync(ct);
        op.Id = existingId.Value;
    }

    /// <summary>Transition table for coalescing a pending op's kind with a newly-requested kind.
    /// Returns null to drop the op entirely.</summary>
    private static SyncOperationKind? Merge(SyncOperationKind existing, SyncOperationKind next) => next switch
    {
        // A Create can't meaningfully replace an existing pending op — keep what's there.
        SyncOperationKind.Create => existing,
        // Editing an unuploaded event is still a create; otherwise it's an update.
        SyncOperationKind.Update => existing == SyncOperationKind.Create
            ? SyncOperationKind.Create
            : SyncOperationKind.Update,
        // Deleting an unuploaded event leaves nothing to push; otherwise it becomes a delete.
        SyncOperationKind.Delete => existing == SyncOperationKind.Create
            ? (SyncOperationKind?)null
            : SyncOperationKind.Delete,
        _ => next,
    };

    private static void BindOp(SqliteCommand cmd, SyncOperation op)
    {
        cmd.Parameters.AddWithValue("@event_id", DbConvert.ToText(op.EventId));
        cmd.Parameters.AddWithValue("@operation", DbConvert.EnumToText(op.Kind));
        cmd.Parameters.AddWithValue("@attempt_count", op.AttemptCount);
        cmd.Parameters.AddWithValue("@last_error", (object?)op.LastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@created_at", DbConvert.ToTimestamp(op.CreatedAt));
        cmd.Parameters.AddWithValue("@next_attempt_at", (object?)DbConvert.ToTimestamp(op.NextAttemptAt) ?? DBNull.Value);
    }

    public async Task<IReadOnlyList<SyncOperation>> GetDueAsync(int limit, CancellationToken ct = default)
    {
        await using var lease = await _db.LeaseCommandAsync();
        await using var cmd = lease.CreateCommand();
        cmd.CommandText = """
            SELECT id, event_id, operation, attempt_count, last_error, created_at, next_attempt_at
            FROM sync_operations
            WHERE (next_attempt_at IS NULL OR next_attempt_at <= @now)
            ORDER BY created_at
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@now", DbConvert.ToTimestamp(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<SyncOperation>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(Map(reader));
        return results;
    }

    public async Task MarkAttemptedAsync(long id, string? error, DateTimeOffset? nextAttemptAt, CancellationToken ct = default)
    {
        await using var lease = await _db.LeaseCommandAsync();
        await using var cmd = lease.CreateCommand();
        cmd.CommandText = """
            UPDATE sync_operations
            SET attempt_count = attempt_count + 1,
                last_error = @last_error,
                next_attempt_at = @next_attempt_at
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@last_error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@next_attempt_at", (object?)DbConvert.ToTimestamp(nextAttemptAt) ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveAsync(long id, CancellationToken ct = default)
    {
        await using var lease = await _db.LeaseCommandAsync();
        await using var cmd = lease.CreateCommand();
        cmd.CommandText = "DELETE FROM sync_operations WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<PendingSyncCounts> GetPendingCountsAsync(CancellationToken ct = default)
    {
        await using var lease = await _db.LeaseCommandAsync();
        await using var cmd = lease.CreateCommand();
        cmd.CommandText = """
            SELECT operation, COUNT(*) AS cnt
            FROM sync_operations
            GROUP BY operation
            """;

        var create = 0;
        var update = 0;
        var delete = 0;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var kind = DbConvert.TextToEnum<SyncOperationKind>(reader["operation"]);
            var count = Convert.ToInt32(reader["cnt"], System.Globalization.CultureInfo.InvariantCulture);
            switch (kind)
            {
                case SyncOperationKind.Create: create = count; break;
                case SyncOperationKind.Update: update = count; break;
                case SyncOperationKind.Delete: delete = count; break;
            }
        }

        return new PendingSyncCounts(create, update, delete);
    }

    private static SyncOperation Map(SqliteDataReader r) => new()
    {
        Id = DbConvert.ToLong(r["id"]),
        EventId = DbConvert.ToGuid(r["event_id"]),
        Kind = DbConvert.TextToEnum<SyncOperationKind>(r["operation"]),
        AttemptCount = Convert.ToInt32(r["attempt_count"], System.Globalization.CultureInfo.InvariantCulture),
        LastError = DbConvert.NullableString(r["last_error"]),
        CreatedAt = DbConvert.ToDateTimeOffset(r["created_at"]),
        NextAttemptAt = DbConvert.ToNullableDateTimeOffset(r["next_attempt_at"]),
    };
}
