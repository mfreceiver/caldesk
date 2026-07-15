using CalDesk.Core.Models;
using CalDesk.Core.Services;
using Microsoft.Data.Sqlite;

namespace CalDesk.Storage;

internal sealed class CalendarRepository : ICalendarRepository
{
    private readonly CalDeskDatabase _db;

    public CalendarRepository(CalDeskDatabase db) => _db = db;

    public async Task<IReadOnlyList<CalendarInfo>> GetAllAsync(CancellationToken ct = default)
    {
        await using var lease = await _db.LeaseCommandAsync();
        await using var cmd = lease.CreateCommand();
        cmd.CommandText = """
            SELECT id, account_id, remote_url, display_name, color, sort_order,
                   supports_sync_token, ctag, sync_token, is_selected, updated_at
            FROM calendars
            ORDER BY sort_order, display_name
            """;

        var results = new List<CalendarInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(Map(reader));
        return results;
    }

    public async Task<IReadOnlyList<CalendarInfo>> GetByAccountAsync(Guid accountId, CancellationToken ct = default)
    {
        await using var lease = await _db.LeaseCommandAsync();
        await using var cmd = lease.CreateCommand();
        cmd.CommandText = """
            SELECT id, account_id, remote_url, display_name, color, sort_order,
                   supports_sync_token, ctag, sync_token, is_selected, updated_at
            FROM calendars
            WHERE account_id = @account_id
            ORDER BY sort_order, display_name
            """;
        cmd.Parameters.AddWithValue("@account_id", DbConvert.ToText(accountId));

        var results = new List<CalendarInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(Map(reader));
        return results;
    }

    public async Task<CalendarInfo?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var lease = await _db.LeaseCommandAsync();
        await using var cmd = lease.CreateCommand();
        cmd.CommandText = """
            SELECT id, account_id, remote_url, display_name, color, sort_order,
                   supports_sync_token, ctag, sync_token, is_selected, updated_at
            FROM calendars
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", DbConvert.ToText(id));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return Map(reader);
    }

    public async Task<CalendarInfo?> FindByRemoteUrlAsync(string remoteUrl, CancellationToken ct = default)
    {
        await using var lease = await _db.LeaseCommandAsync();
        await using var cmd = lease.CreateCommand();
        cmd.CommandText = """
            SELECT id, account_id, remote_url, display_name, color, sort_order,
                   supports_sync_token, ctag, sync_token, is_selected, updated_at
            FROM calendars
            WHERE remote_url = @remote_url
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@remote_url", remoteUrl);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return Map(reader);
    }

    public async Task UpsertAsync(CalendarInfo calendar, CancellationToken ct = default)
    {
        await using var lease = await _db.LeaseCommandAsync();
        await using var cmd = lease.CreateCommand();
        cmd.CommandText = """
            INSERT INTO calendars (id, account_id, remote_url, display_name, color, sort_order,
                                   supports_sync_token, ctag, sync_token, is_selected, updated_at)
            VALUES (@id, @account_id, @remote_url, @display_name, @color, @sort_order,
                    @supports_sync_token, @ctag, @sync_token, @is_selected, @updated_at)
            ON CONFLICT(id) DO UPDATE SET
                account_id = excluded.account_id,
                remote_url = excluded.remote_url,
                display_name = excluded.display_name,
                color = excluded.color,
                sort_order = excluded.sort_order,
                supports_sync_token = excluded.supports_sync_token,
                ctag = excluded.ctag,
                sync_token = excluded.sync_token,
                is_selected = excluded.is_selected,
                updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("@id", DbConvert.ToText(calendar.Id));
        cmd.Parameters.AddWithValue("@account_id", DbConvert.ToText(calendar.AccountId));
        cmd.Parameters.AddWithValue("@remote_url", calendar.RemoteUrl);
        cmd.Parameters.AddWithValue("@display_name", calendar.DisplayName);
        cmd.Parameters.AddWithValue("@color", (object?)calendar.Color ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sort_order", calendar.SortOrder);
        cmd.Parameters.AddWithValue("@supports_sync_token", DbConvert.ToInt(calendar.SupportsSyncToken));
        cmd.Parameters.AddWithValue("@ctag", (object?)calendar.Ctag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sync_token", (object?)calendar.SyncToken ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@is_selected", DbConvert.ToInt(calendar.IsSelected));
        cmd.Parameters.AddWithValue("@updated_at", DbConvert.ToTimestamp(calendar.UpdatedAt));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var lease = await _db.LeaseCommandAsync();
        await using var cmd = lease.CreateCommand();
        cmd.CommandText = "DELETE FROM calendars WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", DbConvert.ToText(id));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static CalendarInfo Map(SqliteDataReader r) => new()
    {
        Id = DbConvert.ToGuid(r["id"]),
        AccountId = DbConvert.ToGuid(r["account_id"]),
        RemoteUrl = DbConvert.StringOrEmpty(r["remote_url"]),
        DisplayName = DbConvert.StringOrEmpty(r["display_name"]),
        Color = DbConvert.NullableString(r["color"]),
        SortOrder = Convert.ToInt32(r["sort_order"], System.Globalization.CultureInfo.InvariantCulture),
        SupportsSyncToken = DbConvert.ToBool(r["supports_sync_token"]),
        Ctag = DbConvert.NullableString(r["ctag"]),
        SyncToken = DbConvert.NullableString(r["sync_token"]),
        IsSelected = DbConvert.ToBool(r["is_selected"]),
        UpdatedAt = DbConvert.ToDateTimeOffset(r["updated_at"]),
    };
}
