using System.Globalization;
using System.Text;
using CalDesk.Core.Models;
using CalDesk.Core.Services;
using Microsoft.Data.Sqlite;

namespace CalDesk.Storage;

internal sealed class EventRepository : IEventRepository
{
    private readonly CalDeskDatabase _db;

    public EventRepository(CalDeskDatabase db) => _db = db;

    private const string SelectColumns = """
        id, calendar_id, uid, remote_href, etag, icalendar_data, summary, description,
        location, is_all_day, start_utc, end_utc, time_zone_id, is_floating,
        recurrence_rule, recurrence_id, sync_status, deleted_at, created_at, updated_at
        """;

    /// <summary>T7: lightweight projection — same columns as <see cref="SelectColumns"/> MINUS
    /// <c>icalendar_data</c>. Used by <see cref="GetInRangeLightweightAsync"/> for the display path
    /// so the heavy raw iCalendar payload is skipped for every event (recurring payloads are
    /// backfilled separately via <see cref="GetRecurringDataAsync"/>).</summary>
    private const string LightweightColumns = """
        id, calendar_id, uid, remote_href, etag, summary, description,
        location, is_all_day, start_utc, end_utc, time_zone_id, is_floating,
        recurrence_rule, recurrence_id, sync_status, deleted_at, created_at, updated_at
        """;

    public async Task<CalendarEvent?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var lease = await _db.LeaseCommandAsync();
        await using var cmd = lease.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM events WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", DbConvert.ToText(id));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return Map(reader);
    }

    public async Task<CalendarEvent?> FindByUidAsync(Guid calendarId, string uid, CancellationToken ct = default)
    {
        await using var lease = await _db.LeaseCommandAsync();
        await using var cmd = lease.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM events WHERE calendar_id = @calendar_id AND uid = @uid LIMIT 1";
        cmd.Parameters.AddWithValue("@calendar_id", DbConvert.ToText(calendarId));
        cmd.Parameters.AddWithValue("@uid", uid);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return Map(reader);
    }

    public async Task<CalendarEvent?> FindByRemoteHrefAsync(string href, CancellationToken ct = default)
    {
        await using var lease = await _db.LeaseCommandAsync();
        await using var cmd = lease.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM events WHERE remote_href = @href LIMIT 1";
        cmd.Parameters.AddWithValue("@href", href);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return Map(reader);
    }

    public async Task<IReadOnlyList<CalendarEvent>> GetInRangeAsync(
        IReadOnlyCollection<Guid> calendarIds,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken ct = default)
    {
        if (calendarIds.Count == 0)
            return Array.Empty<CalendarEvent>();

        await using var lease = await _db.LeaseCommandAsync();
        await using var cmd = lease.CreateCommand();

        var sb = new StringBuilder();
        sb.Append("SELECT ").Append(SelectColumns).Append(" FROM events ");
        BuildInRangeWhere(cmd, calendarIds, startUtc, endUtc, sb);
        sb.Append(" ORDER BY start_utc");

        cmd.CommandText = sb.ToString();

        var results = new List<CalendarEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(Map(reader));
        return results;
    }

    /// <summary>T7: lightweight display-path query — identical WHERE/ORDER to
    /// <see cref="GetInRangeAsync"/> but SELECTs <see cref="LightweightColumns"/> (no
    /// <c>icalendar_data</c>) and maps via <see cref="MapLight"/> (which leaves
    /// <see cref="CalendarEvent.ICalendarData"/> at the default ""). Recurring events returned here
    /// cannot be expanded until the caller backfills via <see cref="GetRecurringDataAsync"/>.</summary>
    public async Task<IReadOnlyList<CalendarEvent>> GetInRangeLightweightAsync(
        IReadOnlyCollection<Guid> calendarIds,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken ct = default)
    {
        if (calendarIds.Count == 0)
            return Array.Empty<CalendarEvent>();

        await using var lease = await _db.LeaseCommandAsync();
        await using var cmd = lease.CreateCommand();

        var sb = new StringBuilder();
        sb.Append("SELECT ").Append(LightweightColumns).Append(" FROM events ");
        BuildInRangeWhere(cmd, calendarIds, startUtc, endUtc, sb);
        sb.Append(" ORDER BY start_utc");

        cmd.CommandText = sb.ToString();

        var results = new List<CalendarEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(MapLight(reader));
        return results;
    }

    /// <summary>T7: batch fetch of <c>icalendar_data</c> by event ID. Empty input short-circuits
    /// (no SQL). Used by display VMs to backfill the recurring subset after a lightweight range
    /// query. Keyed by requested ID; rows missing from storage are simply absent from the result.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> GetRecurringDataAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return new Dictionary<Guid, string>();

        await using var lease = await _db.LeaseCommandAsync();
        await using var cmd = lease.CreateCommand();

        var idParams = new List<string>(ids.Count);
        int i = 0;
        foreach (var id in ids)
        {
            var name = $"@id{i++}";
            idParams.Add(name);
            cmd.Parameters.AddWithValue(name, DbConvert.ToText(id));
        }

        cmd.CommandText = "SELECT id, icalendar_data FROM events WHERE id IN ("
                          + string.Join(", ", idParams) + ")";

        var result = new Dictionary<Guid, string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = DbConvert.ToGuid(reader["id"]);
            result[id] = DbConvert.StringOrEmpty(reader["icalendar_data"]);
        }
        return result;
    }

    /// <summary>Shared WHERE-clause builder for <see cref="GetInRangeAsync"/> and
    /// <see cref="GetInRangeLightweightAsync"/> — guarantees the two queries match (T7-C1: same
    /// event set, just different column projection). Appends the WHERE fragment to
    /// <paramref name="sb"/> and adds the calendar-id / range parameters to <paramref name="cmd"/>.</summary>
    private static void BuildInRangeWhere(
        SqliteCommand cmd,
        IReadOnlyCollection<Guid> calendarIds,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        StringBuilder sb)
    {
        var idParams = new List<string>(calendarIds.Count);
        int i = 0;
        foreach (var cid in calendarIds)
        {
            var name = $"@id{i++}";
            idParams.Add(name);
            cmd.Parameters.AddWithValue(name, DbConvert.ToText(cid));
        }

        sb.Append("WHERE deleted_at IS NULL AND sync_status != 'DeletedLocal' ");
        sb.Append("  AND calendar_id IN (").Append(string.Join(", ", idParams)).Append(") ");
        sb.Append("  AND ( (recurrence_rule IS NULL AND end_utc > @start AND start_utc < @end) ");
        sb.Append("        OR (recurrence_rule IS NOT NULL AND start_utc < @end) )");

        cmd.Parameters.AddWithValue("@start", DbConvert.ToTimestamp(startUtc));
        cmd.Parameters.AddWithValue("@end", DbConvert.ToTimestamp(endUtc));
    }

    public async Task<IReadOnlyList<CalendarEvent>> GetPendingSyncAsync(CancellationToken ct = default)
    {
        await using var lease = await _db.LeaseCommandAsync();
        await using var cmd = lease.CreateCommand();
        // Hygiene #9: no `deleted_at IS NULL` filter here. A soft delete stamps deleted_at
        // AND sets sync_status='DeletedLocal' (MarkDeletedAsync), so that filter excluded
        // exactly the rows the 'DeletedLocal' IN-term was meant to surface — the pending-sync
        // queue must contain local deletions awaiting their server-side DELETE. New/Modified
        // rows always have deleted_at NULL, so dropping the filter leaves them unchanged.
        cmd.CommandText = $"""
            SELECT {SelectColumns}
            FROM events
            WHERE sync_status IN ('NewLocal', 'ModifiedLocal', 'DeletedLocal', 'Conflict', 'Error')
            ORDER BY updated_at
            """;

        var results = new List<CalendarEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(Map(reader));
        return results;
    }

    public async Task UpsertAsync(CalendarEvent evt, CancellationToken ct = default)
    {
        await using var lease = await _db.LeaseCommandAsync();
        await using var cmd = lease.CreateCommand();
        cmd.CommandText = """
            INSERT INTO events (
                id, calendar_id, uid, remote_href, etag, icalendar_data, summary, description,
                location, is_all_day, start_utc, end_utc, time_zone_id, is_floating,
                recurrence_rule, recurrence_id, sync_status, deleted_at, created_at, updated_at
            )
            VALUES (
                @id, @calendar_id, @uid, @remote_href, @etag, @icalendar_data, @summary, @description,
                @location, @is_all_day, @start_utc, @end_utc, @time_zone_id, @is_floating,
                @recurrence_rule, @recurrence_id, @sync_status, @deleted_at, @created_at, @updated_at
            )
            ON CONFLICT(id) DO UPDATE SET
                calendar_id = excluded.calendar_id,
                uid = excluded.uid,
                remote_href = excluded.remote_href,
                etag = excluded.etag,
                icalendar_data = excluded.icalendar_data,
                summary = excluded.summary,
                description = excluded.description,
                location = excluded.location,
                is_all_day = excluded.is_all_day,
                start_utc = excluded.start_utc,
                end_utc = excluded.end_utc,
                time_zone_id = excluded.time_zone_id,
                is_floating = excluded.is_floating,
                recurrence_rule = excluded.recurrence_rule,
                recurrence_id = excluded.recurrence_id,
                sync_status = excluded.sync_status,
                deleted_at = excluded.deleted_at,
                created_at = excluded.created_at,
                updated_at = excluded.updated_at
            """;

        cmd.Parameters.AddWithValue("@id", DbConvert.ToText(evt.Id));
        cmd.Parameters.AddWithValue("@calendar_id", DbConvert.ToText(evt.CalendarId));
        cmd.Parameters.AddWithValue("@uid", evt.Uid);
        cmd.Parameters.AddWithValue("@remote_href", (object?)evt.RemoteHref ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@etag", (object?)evt.ETag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@icalendar_data", evt.ICalendarData ?? string.Empty);
        cmd.Parameters.AddWithValue("@summary", evt.Summary ?? string.Empty);
        cmd.Parameters.AddWithValue("@description", (object?)evt.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@location", (object?)evt.Location ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@is_all_day", DbConvert.ToInt(evt.IsAllDay));
        cmd.Parameters.AddWithValue("@start_utc", DbConvert.ToTimestamp(evt.StartUtc));
        cmd.Parameters.AddWithValue("@end_utc", DbConvert.ToTimestamp(evt.EndUtc));
        cmd.Parameters.AddWithValue("@time_zone_id", (object?)evt.TimeZoneId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@is_floating", DbConvert.ToInt(evt.IsFloating));
        cmd.Parameters.AddWithValue("@recurrence_rule", (object?)evt.RecurrenceRule ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@recurrence_id", (object?)evt.RecurrenceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sync_status", DbConvert.EnumToText(evt.SyncStatus));
        cmd.Parameters.AddWithValue("@deleted_at", (object?)DbConvert.ToTimestamp(evt.DeletedAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@created_at", DbConvert.ToTimestamp(evt.CreatedAt));
        cmd.Parameters.AddWithValue("@updated_at", DbConvert.ToTimestamp(evt.UpdatedAt));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>CQ-003: update only the server-assignment columns of an existing row. Intentionally
    /// does NOT touch <c>updated_at</c>/<c>sync_status</c>: this runs while a concurrent edit owns
    /// those columns, and bumping them here would make the row look newer than the edit or break
    /// the next push's own freshness comparison.</summary>
    public async Task UpdateSyncFieldsAsync(Guid id, string? remoteHref, string? etag, CancellationToken ct = default)
    {
        await using var lease = await _db.LeaseCommandAsync();
        await using var cmd = lease.CreateCommand();
        cmd.CommandText = """
            UPDATE events
            SET remote_href = @remote_href,
                etag = @etag
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", DbConvert.ToText(id));
        cmd.Parameters.AddWithValue("@remote_href", (object?)remoteHref ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@etag", (object?)etag ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkDeletedAsync(Guid id, CancellationToken ct = default)
    {
        await using var lease = await _db.LeaseCommandAsync();
        await using var cmd = lease.CreateCommand();
        cmd.CommandText = """
            UPDATE events
            SET deleted_at = @now,
                sync_status = 'DeletedLocal',
                updated_at = @now
            WHERE id = @id
            """;
        var now = DateTimeOffset.UtcNow;
        cmd.Parameters.AddWithValue("@id", DbConvert.ToText(id));
        cmd.Parameters.AddWithValue("@now", DbConvert.ToTimestamp(now));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task HardDeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var lease = await _db.LeaseCommandAsync();
        await using var cmd = lease.CreateCommand();
        cmd.CommandText = "DELETE FROM events WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", DbConvert.ToText(id));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task HardDeleteAllAsync(CancellationToken ct = default)
    {
        await using var lease = await _db.LeaseCommandAsync();
        await using var cmd = lease.CreateCommand();
        cmd.CommandText = "DELETE FROM events";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static CalendarEvent Map(SqliteDataReader r) => new()
    {
        Id = DbConvert.ToGuid(r["id"]),
        CalendarId = DbConvert.ToGuid(r["calendar_id"]),
        Uid = DbConvert.StringOrEmpty(r["uid"]),
        RemoteHref = DbConvert.NullableString(r["remote_href"]),
        ETag = DbConvert.NullableString(r["etag"]),
        ICalendarData = DbConvert.StringOrEmpty(r["icalendar_data"]),
        Summary = DbConvert.StringOrEmpty(r["summary"]),
        Description = DbConvert.NullableString(r["description"]),
        Location = DbConvert.NullableString(r["location"]),
        IsAllDay = DbConvert.ToBool(r["is_all_day"]),
        StartUtc = DbConvert.ToDateTimeOffset(r["start_utc"]),
        EndUtc = DbConvert.ToDateTimeOffset(r["end_utc"]),
        TimeZoneId = DbConvert.NullableString(r["time_zone_id"]),
        IsFloating = DbConvert.ToBool(r["is_floating"]),
        RecurrenceRule = DbConvert.NullableString(r["recurrence_rule"]),
        RecurrenceId = DbConvert.NullableString(r["recurrence_id"]),
        SyncStatus = DbConvert.TextToEnum<SyncStatus>(r["sync_status"]),
        DeletedAt = DbConvert.ToNullableDateTimeOffset(r["deleted_at"]),
        CreatedAt = DbConvert.ToDateTimeOffset(r["created_at"]),
        UpdatedAt = DbConvert.ToDateTimeOffset(r["updated_at"]),
    };

    /// <summary>T7: same as <see cref="Map"/> but does NOT read the <c>icalendar_data</c> column
    /// (which is not present in the <see cref="LightweightColumns"/> projection).
    /// <see cref="CalendarEvent.ICalendarData"/> stays at its default "" — callers that need it
    /// (the recurrence expander) must backfill via <see cref="GetRecurringDataAsync"/>.</summary>
    private static CalendarEvent MapLight(SqliteDataReader r) => new()
    {
        Id = DbConvert.ToGuid(r["id"]),
        CalendarId = DbConvert.ToGuid(r["calendar_id"]),
        Uid = DbConvert.StringOrEmpty(r["uid"]),
        RemoteHref = DbConvert.NullableString(r["remote_href"]),
        ETag = DbConvert.NullableString(r["etag"]),
        // ICalendarData intentionally NOT read — left at default "" (T7-C1).
        Summary = DbConvert.StringOrEmpty(r["summary"]),
        Description = DbConvert.NullableString(r["description"]),
        Location = DbConvert.NullableString(r["location"]),
        IsAllDay = DbConvert.ToBool(r["is_all_day"]),
        StartUtc = DbConvert.ToDateTimeOffset(r["start_utc"]),
        EndUtc = DbConvert.ToDateTimeOffset(r["end_utc"]),
        TimeZoneId = DbConvert.NullableString(r["time_zone_id"]),
        IsFloating = DbConvert.ToBool(r["is_floating"]),
        RecurrenceRule = DbConvert.NullableString(r["recurrence_rule"]),
        RecurrenceId = DbConvert.NullableString(r["recurrence_id"]),
        SyncStatus = DbConvert.TextToEnum<SyncStatus>(r["sync_status"]),
        DeletedAt = DbConvert.ToNullableDateTimeOffset(r["deleted_at"]),
        CreatedAt = DbConvert.ToDateTimeOffset(r["created_at"]),
        UpdatedAt = DbConvert.ToDateTimeOffset(r["updated_at"]),
    };
}
