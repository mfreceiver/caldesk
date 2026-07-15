using CalDesk.Core.Models;
using CalDesk.Core.Services;
using Microsoft.Data.Sqlite;

namespace CalDesk.Storage;

internal sealed class AccountRepository : IAccountRepository
{
    private readonly CalDeskDatabase _db;

    public AccountRepository(CalDeskDatabase db) => _db = db;

    public async Task<IReadOnlyList<Account>> GetAllAsync(CancellationToken ct = default)
    {
        await using var lease = await _db.LeaseCommandAsync();
        await using var cmd = lease.CreateCommand();
        cmd.CommandText = """
            SELECT id, server_url, username, credential_target, display_name, created_at
            FROM accounts
            ORDER BY created_at
            """;

        var results = new List<Account>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(Map(reader));
        }
        return results;
    }

    public async Task<Account?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var lease = await _db.LeaseCommandAsync();
        await using var cmd = lease.CreateCommand();
        cmd.CommandText = """
            SELECT id, server_url, username, credential_target, display_name, created_at
            FROM accounts
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", DbConvert.ToText(id));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return Map(reader);
    }

    public async Task UpsertAsync(Account account, CancellationToken ct = default)
    {
        await using var lease = await _db.LeaseCommandAsync();
        await using var cmd = lease.CreateCommand();
        cmd.CommandText = """
            INSERT INTO accounts (id, server_url, username, credential_target, display_name, created_at)
            VALUES (@id, @server_url, @username, @credential_target, @display_name, @created_at)
            ON CONFLICT(id) DO UPDATE SET
                server_url = excluded.server_url,
                username = excluded.username,
                credential_target = excluded.credential_target,
                display_name = excluded.display_name,
                created_at = excluded.created_at
            """;
        cmd.Parameters.AddWithValue("@id", DbConvert.ToText(account.Id));
        cmd.Parameters.AddWithValue("@server_url", account.ServerUrl);
        cmd.Parameters.AddWithValue("@username", account.Username);
        cmd.Parameters.AddWithValue("@credential_target", account.CredentialTarget);
        cmd.Parameters.AddWithValue("@display_name", (object?)account.DisplayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@created_at", DbConvert.ToTimestamp(account.CreatedAt));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var lease = await _db.LeaseCommandAsync();
        await using var cmd = lease.CreateCommand();
        cmd.CommandText = "DELETE FROM accounts WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", DbConvert.ToText(id));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static Account Map(SqliteDataReader r) => new()
    {
        Id = DbConvert.ToGuid(r["id"]),
        ServerUrl = DbConvert.StringOrEmpty(r["server_url"]),
        Username = DbConvert.StringOrEmpty(r["username"]),
        CredentialTarget = DbConvert.StringOrEmpty(r["credential_target"]),
        DisplayName = DbConvert.NullableString(r["display_name"]),
        CreatedAt = DbConvert.ToDateTimeOffset(r["created_at"]),
    };
}
