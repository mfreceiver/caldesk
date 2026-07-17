using CalDesk.Core.Services;
using Xunit;

namespace CalDesk.Tests;

public sealed class SyncStatusTextTests
{
    [Theory]
    [InlineData(null, 0, 0, 0, "未同步")]
    [InlineData("09:05", 0, 0, 0, "同步于·09:05")]
    [InlineData("09:05", 2, 0, 0, "同步于·09:05 ·新增2")]
    [InlineData("09:05", 1, 2, 3, "同步于·09:05 ·新增1/修改2/删除3")]
    [InlineData("09:05", 0, 2, 0, "同步于·09:05 ·修改2")]
    [InlineData(null, 1, 0, 0, "未同步 ·新增1")]
    public void Format_MatchesSpec(string? time, int create, int update, int delete, string expected)
    {
        DateTimeOffset? lastSync = time is null
            ? null
            : new DateTimeOffset(2026, 7, 15, int.Parse(time[..2]), int.Parse(time[3..]), 0, TimeSpan.Zero);

        var text = SyncStatusText.Format(lastSync, new PendingSyncCounts(create, update, delete));
        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData("同步出错（已记录）", 0, 0, 0, "同步出错（已记录）")]
    [InlineData("同步出错（已记录）", 1, 0, 0, "同步出错（已记录） ·新增1")]
    [InlineData("有 2 个冲突待处理", 0, 1, 1, "有 2 个冲突待处理 ·修改1/删除1")]
    [InlineData("同步失败：timeout", 1, 2, 0, "同步失败：timeout ·新增1/修改2")]
    public void WithPendingSuffix_AppendsOnlyWhenNonZero(string head, int create, int update, int delete, string expected)
    {
        var text = SyncStatusText.WithPendingSuffix(head, new PendingSyncCounts(create, update, delete));
        Assert.Equal(expected, text);
    }
}
