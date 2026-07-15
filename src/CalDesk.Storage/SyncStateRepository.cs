using CalDesk.Core.Services;
using Microsoft.Data.Sqlite;

namespace CalDesk.Storage;

internal sealed class SyncStateRepository : ISyncStateRepository
{
    private readonly CalDeskDatabase _db;

    public SyncStateRepository(CalDeskDatabase db) => _db = db;

    /// <summary>Upserts the sync_token column for a calendar, preserving ctag when a row already exists.
    /// When inserting a fresh row, ctag is left null until <see cref="SaveCtagAsync"/> writes one.</summary>
    public async Task UpsertSyncTokenAsync(Guid calendarId, string? syncToken, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sync_state (calendar_id, sync_token, ctag)
            VALUES (@calendar_id, @sync_token, NULL)
            ON CONFLICT(calendar_id) DO UPDATE SET
                sync_token = excluded.sync_token
            """;
        cmd.Parameters.AddWithValue("@calendar_id", DbConvert.ToText(calendarId));
        cmd.Parameters.AddWithValue("@sync_token", (object?)syncToken ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetSyncTokenAsync(Guid calendarId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sync_token FROM sync_state WHERE calendar_id = @calendar_id";
        cmd.Parameters.AddWithValue("@calendar_id", DbConvert.ToText(calendarId));
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : (string)result;
    }

    public async Task SaveCtagAsync(Guid calendarId, string? ctag, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sync_state (calendar_id, sync_token, ctag)
            VALUES (@calendar_id, NULL, @ctag)
            ON CONFLICT(calendar_id) DO UPDATE SET
                ctag = excluded.ctag
            """;
        cmd.Parameters.AddWithValue("@calendar_id", DbConvert.ToText(calendarId));
        cmd.Parameters.AddWithValue("@ctag", (object?)ctag ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetCtagAsync(Guid calendarId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ctag FROM sync_state WHERE calendar_id = @calendar_id";
        cmd.Parameters.AddWithValue("@calendar_id", DbConvert.ToText(calendarId));
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : (string)result;
    }
}
