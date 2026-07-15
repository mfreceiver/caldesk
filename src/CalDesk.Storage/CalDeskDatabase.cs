using System.Data.Common;
using System.Globalization;
using CalDesk.Core.Models;
using Microsoft.Data.Sqlite;

namespace CalDesk.Storage;

/// <summary>Owns the SQLite schema for CalDesk and produces open connections
/// for the repositories to use. Repositories open (and dispose) a fresh
/// connection per call so that consumers never have to think about lifetime.</summary>
public sealed class CalDeskDatabase
{
    private readonly string _connectionString;

    public CalDeskDatabase(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("Database path must not be null or whitespace.", nameof(dbPath));
        _connectionString = GetConnectionString(dbPath);
    }

    /// <summary>Stable connection string form used by both the constructor and tests.</summary>
    public static string GetConnectionString(string dbPath) => $"Data Source={dbPath}";

    /// <summary>Creates and opens a connection. The caller is responsible for disposing it.</summary>
    public SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    /// <summary>Creates every table and index required by the storage layer. Idempotent.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await using var tx = await conn.BeginTransactionAsync(ct);

        await ExecAsync(conn, tx, """
            CREATE TABLE IF NOT EXISTS accounts (
                id TEXT PRIMARY KEY,
                server_url TEXT NOT NULL,
                username TEXT NOT NULL,
                credential_target TEXT NOT NULL,
                display_name TEXT,
                created_at TEXT NOT NULL
            )
            """, ct);

        await ExecAsync(conn, tx, """
            CREATE TABLE IF NOT EXISTS calendars (
                id TEXT PRIMARY KEY,
                account_id TEXT NOT NULL,
                remote_url TEXT NOT NULL,
                display_name TEXT NOT NULL,
                color TEXT,
                sort_order INTEGER NOT NULL DEFAULT 0,
                supports_sync_token INTEGER NOT NULL DEFAULT 0,
                ctag TEXT,
                sync_token TEXT,
                is_selected INTEGER NOT NULL DEFAULT 1,
                updated_at TEXT NOT NULL
            )
            """, ct);

        await ExecAsync(conn, tx, """
            CREATE TABLE IF NOT EXISTS events (
                id TEXT PRIMARY KEY,
                calendar_id TEXT NOT NULL,
                uid TEXT NOT NULL,
                remote_href TEXT,
                etag TEXT,
                icalendar_data TEXT NOT NULL DEFAULT '',
                summary TEXT NOT NULL DEFAULT '',
                description TEXT,
                location TEXT,
                is_all_day INTEGER NOT NULL DEFAULT 0,
                start_utc TEXT NOT NULL,
                end_utc TEXT NOT NULL,
                time_zone_id TEXT,
                is_floating INTEGER NOT NULL DEFAULT 0,
                recurrence_rule TEXT,
                recurrence_id TEXT,
                sync_status TEXT NOT NULL DEFAULT 'Clean',
                deleted_at TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            )
            """, ct);

        await ExecAsync(conn, tx, """
            CREATE TABLE IF NOT EXISTS sync_state (
                calendar_id TEXT PRIMARY KEY,
                sync_token TEXT,
                ctag TEXT
            )
            """, ct);

        await ExecAsync(conn, tx, """
            CREATE TABLE IF NOT EXISTS sync_operations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id TEXT NOT NULL,
                operation TEXT NOT NULL,
                attempt_count INTEGER NOT NULL DEFAULT 0,
                last_error TEXT,
                created_at TEXT NOT NULL,
                next_attempt_at TEXT
            )
            """, ct);

        await ExecAsync(conn, tx, "CREATE INDEX IF NOT EXISTS ix_events_calendar_id ON events(calendar_id)", ct);
        await ExecAsync(conn, tx, "CREATE INDEX IF NOT EXISTS ix_events_uid_calendar_id ON events(uid, calendar_id)", ct);
        await ExecAsync(conn, tx, "CREATE INDEX IF NOT EXISTS ix_events_remote_href ON events(remote_href)", ct);
        await ExecAsync(conn, tx, "CREATE INDEX IF NOT EXISTS ix_events_start_utc ON events(start_utc)", ct);
        await ExecAsync(conn, tx, "CREATE INDEX IF NOT EXISTS ix_events_sync_status ON events(sync_status)", ct);

        await tx.CommitAsync(ct);
    }

    private static async Task ExecAsync(SqliteConnection conn, DbTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>Shared helpers for the repositories: type mapping per the project conventions
/// (Guid&lt;-&gt;TEXT, DateTimeOffset&lt;-&gt;ISO-8601 round-trip TEXT, bool&lt;-&gt;INTEGER 0/1, enum&lt;-&gt;TEXT).</summary>
internal static class DbConvert
{
    public static string ToText(Guid value) => value.ToString();

    public static Guid ToGuid(object? value) =>
        value is null or DBNull ? Guid.Empty : Guid.Parse((string)value);

    public static string ToTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset ToDateTimeOffset(object? value)
    {
        if (value is null or DBNull)
            return DateTimeOffset.MinValue;
        return DateTimeOffset.Parse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    public static string? ToTimestamp(DateTimeOffset? value) =>
        value is null ? null : ToTimestamp(value.Value);

    public static DateTimeOffset? ToNullableDateTimeOffset(object? value)
    {
        if (value is null or DBNull)
            return null;
        return DateTimeOffset.Parse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    public static int ToInt(bool value) => value ? 1 : 0;

    public static bool ToBool(object? value)
    {
        if (value is null or DBNull)
            return false;
        return Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
    }

    public static string EnumToText<T>(T value) where T : struct, Enum => value.ToString();

    public static T TextToEnum<T>(object? value) where T : struct, Enum
    {
        if (value is null or DBNull)
            return default;
        return (T)Enum.Parse(typeof(T), (string)value);
    }

    public static string? StringOrNull(string? value) => value;
    public static string StringOrEmpty(object? value) =>
        value is null or DBNull ? string.Empty : (string)value;
    public static string? NullableString(object? value) =>
        value is null or DBNull ? null : (string)value;

    public static long ToLong(object? value)
    {
        if (value is null or DBNull)
            return 0L;
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}
