using CalDesk.Core.Models;
using CalDesk.Core.Services;
using Microsoft.Data.Sqlite;

namespace CalDesk.Storage;

internal sealed class SyncOperationRepository : ISyncOperationRepository
{
    private readonly CalDeskDatabase _db;

    public SyncOperationRepository(CalDeskDatabase db) => _db = db;

    /// <summary>Enqueues a sync operation. Assigns the auto-generated row id back to <paramref name="op"/>.</summary>
    public async Task EnqueueAsync(SyncOperation op, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sync_operations (event_id, operation, attempt_count, last_error, created_at, next_attempt_at)
            VALUES (@event_id, @operation, @attempt_count, @last_error, @created_at, @next_attempt_at);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@event_id", DbConvert.ToText(op.EventId));
        cmd.Parameters.AddWithValue("@operation", DbConvert.EnumToText(op.Kind));
        cmd.Parameters.AddWithValue("@attempt_count", op.AttemptCount);
        cmd.Parameters.AddWithValue("@last_error", (object?)op.LastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@created_at", DbConvert.ToTimestamp(op.CreatedAt));
        cmd.Parameters.AddWithValue("@next_attempt_at", (object?)DbConvert.ToTimestamp(op.NextAttemptAt) ?? DBNull.Value);

        var rowid = await cmd.ExecuteScalarAsync(ct);
        op.Id = DbConvert.ToLong(rowid);
    }

    public async Task<IReadOnlyList<SyncOperation>> GetDueAsync(int limit, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await using var cmd = conn.CreateCommand();
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
        await using var conn = _db.CreateConnection();
        await using var cmd = conn.CreateCommand();
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
        await using var conn = _db.CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sync_operations WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
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
