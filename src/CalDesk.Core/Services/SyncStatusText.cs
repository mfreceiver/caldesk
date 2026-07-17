using System.Globalization;

namespace CalDesk.Core.Services;

/// <summary>Pending outbox counts by operation kind (non-zero kinds are shown in status).</summary>
public sealed record PendingSyncCounts(int Create, int Update, int Delete);

/// <summary>Formats the main-window sync status line from last successful sync time and pending queue.</summary>
public static class SyncStatusText
{
    /// <summary>
    /// base = lastSyncLocal null ? <c>未同步</c> : <c>同步于·HH:mm</c> (24h local);
    /// if any pending: append <c> ·</c> + non-zero of <c>新增{c}</c>/<c>修改{u}</c>/<c>删除{d}</c> joined by <c>/</c>.
    /// </summary>
    public static string Format(DateTimeOffset? lastSyncLocal, PendingSyncCounts pending)
    {
        var head = lastSyncLocal is null
            ? "未同步"
            : "同步于·" + lastSyncLocal.Value.ToString("HH:mm", CultureInfo.InvariantCulture);

        return WithPendingSuffix(head, pending);
    }

    /// <summary>
    /// Append pending suffix to an arbitrary status head (e.g. error/conflict text).
    /// No change when all counts are zero.
    /// </summary>
    public static string WithPendingSuffix(string head, PendingSyncCounts pending)
    {
        if (pending.Create == 0 && pending.Update == 0 && pending.Delete == 0)
            return head;

        var parts = new List<string>(3);
        if (pending.Create > 0) parts.Add("新增" + pending.Create.ToString(CultureInfo.InvariantCulture));
        if (pending.Update > 0) parts.Add("修改" + pending.Update.ToString(CultureInfo.InvariantCulture));
        if (pending.Delete > 0) parts.Add("删除" + pending.Delete.ToString(CultureInfo.InvariantCulture));

        return head + " ·" + string.Join("/", parts);
    }
}
