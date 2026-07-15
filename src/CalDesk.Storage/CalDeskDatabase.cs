using System.Data.Common;
using System.Globalization;
using CalDesk.Core.Models;
using Microsoft.Data.Sqlite;

namespace CalDesk.Storage;

/// <summary>Owns the SQLite schema for CalDesk and produces open connections
/// for the repositories to use. Repositories lease a command per call; when an
/// ambient transaction is active (see <see cref="RunInTransactionAsync"/>) the
/// command binds to the shared connection + transaction, otherwise a fresh
/// connection is opened and disposed with the command.</summary>
public sealed class CalDeskDatabase
{
    private readonly string _connectionString;

    // Ambient transaction scope (async-local so it flows correctly across awaits without
    // bleeding across concurrent requests). Set only by RunInTransactionAsync.
    private static readonly AsyncLocal<ConnectionScope?> _currentScope = new();

    public CalDeskDatabase(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("Database path must not be null or whitespace.", nameof(dbPath));
        _connectionString = GetConnectionString(dbPath);
    }

    /// <summary>Stable connection string form used by both the constructor and tests.</summary>
    public static string GetConnectionString(string dbPath) => $"Data Source={dbPath}";

    internal static ConnectionScope? CurrentScope => _currentScope.Value;

    /// <summary>Creates and opens a connection with the per-connection pragmas applied
    /// (busy_timeout so writers briefly block instead of erroring, foreign_keys to enforce
    /// the declared FK cascades). The caller is responsible for disposing it.</summary>
    public SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        ExecPragma(conn, "PRAGMA busy_timeout=5000;");
        ExecPragma(conn, "PRAGMA foreign_keys=ON;");
        return conn;
    }

    /// <summary>Runs all pending schema migrations forward to the latest version.
    /// Idempotent: re-running is a no-op once <c>user_version</c> is current.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = CreateConnection();

        // WAL is persistent for the DB file (stored in the header) and must be set
        // OUTSIDE a transaction. It gives concurrent readers + a single writer without
        // "database is locked" under the app's read/write mix.
        await ExecPragmaAsync(conn, "PRAGMA journal_mode=WAL;", ct);

        var currentVersion = await GetUserVersionAsync(conn, ct);
        var target = Migrations.Length;

        for (var v = currentVersion + 1; v <= target; v++)
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            await Migrations[v - 1](conn, tx, ct);
            await SetUserVersionAsync(conn, tx, v, ct);
            await tx.CommitAsync(ct);
        }
    }

    /// <summary>Runs <paramref name="work"/> inside a single SQLite transaction on a single
    /// connection. Repository calls made inside <paramref name="work"/> automatically enlist
    /// on this connection/transaction (via the async-local <see cref="CurrentScope"/>), so an
    /// event write + its outbox enqueue commit atomically. Nested calls enlist in the outer
    /// transaction. If <paramref name="work"/> throws, the transaction is rolled back.</summary>
    public async Task RunInTransactionAsync(Func<Task> work, CancellationToken ct = default)
    {
        if (work is null)
            throw new ArgumentNullException(nameof(work));

        // Already inside a transaction: just enlist, the outer scope owns commit/rollback.
        if (_currentScope.Value is not null)
        {
            await work().ConfigureAwait(false);
            return;
        }

        await using var conn = CreateConnection();
        await using var tx = await conn.BeginTransactionAsync(ct);
        var scope = new ConnectionScope(conn, tx);
        _currentScope.Value = scope;
        try
        {
            await work().ConfigureAwait(false);
            await tx.CommitAsync(ct);
        }
        catch
        {
            try { await tx.RollbackAsync(ct).ConfigureAwait(false); }
            catch { /* swallow secondary rollback errors while unwinding */ }
            throw;
        }
        finally
        {
            _currentScope.Value = null;
        }
    }

    /// <summary>Leases an execution context bound to the ambient transaction's connection (if any),
    /// or a freshly opened connection otherwise. Callers create commands via <see cref="CommandLease.CreateCommand"/>;
    /// disposing the lease disposes the connection only when this lease created it (i.e. not in a scope).</summary>
    internal async Task<CommandLease> LeaseCommandAsync()
    {
        var scope = _currentScope.Value;
        if (scope is not null)
            return new CommandLease(scope.Connection, (SqliteTransaction)scope.Transaction, ownsConnection: false);
        var conn = CreateConnection();
        return new CommandLease(conn, transaction: null, ownsConnection: true);
    }

    // ---- migrations ------------------------------------------------------------

    /// <summary>Ordered migrations; index i brings user_version from i to i+1.</summary>
    private static readonly Func<SqliteConnection, DbTransaction, CancellationToken, Task>[] Migrations =
    {
        MigrateV1Async,
    };

    /// <summary>Migration v1: the full initial schema (foreign keys with cascade,
    /// all serving + unique indexes). Uses IF NOT EXISTS so it is a no-op on tables
    /// that pre-date the migration system (existing data loads are not disturbed).</summary>
    private static async Task MigrateV1Async(SqliteConnection conn, DbTransaction tx, CancellationToken ct)
    {
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
                updated_at TEXT NOT NULL,
                FOREIGN KEY (account_id) REFERENCES accounts(id) ON DELETE CASCADE
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
                updated_at TEXT NOT NULL,
                FOREIGN KEY (calendar_id) REFERENCES calendars(id) ON DELETE CASCADE
            )
            """, ct);

        await ExecAsync(conn, tx, """
            CREATE TABLE IF NOT EXISTS sync_state (
                calendar_id TEXT PRIMARY KEY,
                sync_token TEXT,
                ctag TEXT,
                FOREIGN KEY (calendar_id) REFERENCES calendars(id) ON DELETE CASCADE
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
                next_attempt_at TEXT,
                FOREIGN KEY (event_id) REFERENCES events(id) ON DELETE CASCADE
            )
            """, ct);

        // Legacy single-column indexes (kept for compatibility).
        await ExecAsync(conn, tx, "CREATE INDEX IF NOT EXISTS ix_events_calendar_id ON events(calendar_id)", ct);
        await ExecAsync(conn, tx, "CREATE INDEX IF NOT EXISTS ix_events_uid_calendar_id ON events(uid, calendar_id)", ct);
        await ExecAsync(conn, tx, "CREATE INDEX IF NOT EXISTS ix_events_remote_href ON events(remote_href)", ct);
        await ExecAsync(conn, tx, "CREATE INDEX IF NOT EXISTS ix_events_start_utc ON events(start_utc)", ct);
        await ExecAsync(conn, tx, "CREATE INDEX IF NOT EXISTS ix_events_sync_status ON events(sync_status)", ct);

        // Composite indexes tuned to the real read paths.
        // Range query: calendar_id equality + start/end overlap window.
        await ExecAsync(conn, tx, "CREATE INDEX IF NOT EXISTS idx_events_range ON events(calendar_id, start_utc, end_utc)", ct);
        // Recurring-series horizon: pull-in where recurrence_rule IS NOT NULL AND start_utc < @end.
        await ExecAsync(conn, tx, "CREATE INDEX IF NOT EXISTS idx_events_recurring ON events(calendar_id, start_utc) WHERE recurrence_rule IS NOT NULL", ct);
        // Outbox "what is due now" scan.
        await ExecAsync(conn, tx, "CREATE INDEX IF NOT EXISTS idx_outbox_due ON sync_operations(next_attempt_at, created_at)", ct);
        // Calendars listed per account.
        await ExecAsync(conn, tx, "CREATE INDEX IF NOT EXISTS idx_calendars_account ON calendars(account_id)", ct);

        // One event per server href within a calendar. Partial (NULL hrefs, i.e. events
        // that have never been uploaded, are exempt) so locally-created events never clash.
        await ExecAsync(conn, tx, "CREATE UNIQUE INDEX IF NOT EXISTS uniq_events_calendar_href ON events(calendar_id, remote_href) WHERE remote_href IS NOT NULL", ct);
    }

    private static async Task<int> GetUserVersionAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task SetUserVersionAsync(SqliteConnection conn, DbTransaction tx, int version, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        // Integer literal is safe here — value is an internally-controlled schema version.
        cmd.CommandText = $"PRAGMA user_version = {version};";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecAsync(SqliteConnection conn, DbTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void ExecPragma(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static async Task ExecPragmaAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>Ambient connection + transaction shared by repository calls inside
/// <see cref="CalDeskDatabase.RunInTransactionAsync(Func{Task}, CancellationToken)"/>.</summary>
internal sealed class ConnectionScope
{
    public SqliteConnection Connection { get; }
    public DbTransaction Transaction { get; }

    public ConnectionScope(SqliteConnection connection, DbTransaction transaction)
    {
        Connection = connection;
        Transaction = transaction;
    }
}

/// <summary>Holds the connection (and optional ambient transaction) for a unit of repository work.
/// Disposing disposes the connection only when this lease owns it (i.e. when it was freshly opened,
/// not when enlisted in an ambient <see cref="CalDeskDatabase.RunInTransactionAsync"/> scope).</summary>
internal sealed class CommandLease : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction? _transaction;
    private readonly bool _ownsConnection;

    public CommandLease(SqliteConnection connection, SqliteTransaction? transaction, bool ownsConnection)
    {
        _connection = connection;
        _transaction = transaction;
        _ownsConnection = ownsConnection;
    }

    /// <summary>Creates a command bound to this lease's connection (and ambient transaction, if any).</summary>
    public SqliteCommand CreateCommand()
    {
        var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        return cmd;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsConnection)
            await _connection.DisposeAsync();
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
