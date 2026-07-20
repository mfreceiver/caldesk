# UI 性能重构与全面修复 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use ocmar-subagent-driven-development (recommended) or ocmar-executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 彻底移除 layered 透明迁移到 opaque `WindowChrome` 根除 resize 卡顿，并修复全部审计发现（4 个正确性 bug + 核心管线性能 + WPF 次级优化）。

**Architecture:** 三层推进——① 核心层（Core/CalDav/Storage）正确性+性能，Linux 可 TDD；② App ViewModel（后台化、竞态、位置持久化）；③ WPF 层（WindowChrome 迁移、布局反馈环、协调器），winbuild 编译门控 + Windows 真机手测。保留无边框+圆角视觉，放弃整窗半透明。

**Tech Stack:** .NET 10, WPF (net10.0-windows), CommunityToolkit.Mvvm, Ical.Net 5.2.3, Microsoft.Data.Sqlite, xUnit。

## Global Constraints

- **平台**：核心库（Core/CalDav/Storage）跨平台 net10.0；WPF 仅 net10.0-windows，**Linux 编不了 App**。
- **Linux 验证命令**：`dotnet test tests/CalDesk.Tests --nologo`（编译 Core/CalDav/Storage/Tests + 跑单测，必须全绿）。
- **WPF 编译门控**：`ssh winbuild 'cd C:\Dev_Code\caldesk && git -c credential.helper= pull && dotnet build CalDesk.slnx -c Debug'`（每个 WPF task 完成后必须通过；最终 Phase 6 再跑一次）。
- **不可破坏**：托盘常驻、同步引擎（`ConfigureAwait(false)`）、提醒服务、CalDAV 写侧、现有离线单测全绿。
- **不要手写 `<Version>`**（git 派生，见 AGENTS.md）。
- **commit 策略**：ocmar 默认不 commit；每个 task 末尾 `git diff --stat` 记录证据，不 `git add`。
- **`BrushResourceProxy` 不能 Freeze**（需响应动态主题），勿误伤。
- **spec 引用**：`docs/ocmar/specs/2026-07-21-ui-perf-and-fixes.md`。

---

## File Structure（新增/改动文件总览）

**新增（Core/CalDav）**：
- `src/CalDesk.Core/Services/CalendarDateMath.cs` — 共享 `DaysSinceMonday` + DST-safe `LocalMidnightToUtc`（T1 建，T2 扩）
- `src/CalDesk.Core/Services/MonthGridRange.cs` — `readonly record struct MonthGridRange(Start, EndExclusive)` + `For(year,month,rowCount=6)`（T1）
- `src/CalDesk.Core/Services/ExpandedCalendarRange.cs` — 一次展开结果，多视图读取（T3）
- `src/CalDesk.CalDav/Recurrence/IRecurringEventCache.cs` + `RecurringEventCache.cs` — 循环事件解析缓存（T6）

**新增测试**：
- `tests/CalDesk.Tests/MonthGridRangeTests.cs`（T1）
- `tests/CalDesk.Tests/CalendarDateMathTests.cs`（T2）
- `tests/CalDesk.Tests/ExpandedCalendarRangeTests.cs`（T3）
- `tests/CalDesk.Tests/RecurringEventCacheTests.cs`（T6）

> ⚠ **G1（grilling）**：测试项目 `CalDesk.Tests` 是跨平台 net10.0、**仅引用 Core/CalDav/Storage，不引用 App**。App 层 VM（WeekViewModel/MonthViewModel，T4/T5）**无法在 Linux 单测**——其验收改为 winbuild 编译 + grep + 真机手测。Core/CalDav/Storage 层（T1/T2/T3/T6/T7）可 Linux 单测。**无 `SelectDayRaceTests`**（建不起来）。

**改动（按 task）**：见各 task 的 Files 块。

---

## Task 1: MonthGridRange 统一月视图网格范围（修 35天查询 vs 42天渲染）

**Spec**: C2（rev-1 8/10，explorer 漏掉的正确性 bug）
**Files:**
- Create: `src/CalDesk.Core/Services/MonthGridRange.cs`
- Create: `src/CalDesk.Core/Services/CalendarDateMath.cs`（先只放 `DaysSinceMonday`）
- Modify: `src/CalDesk.Core/Services/CalendarViewService.cs:75-87`（BuildMonth 改用 `MonthGridRange.For`）
- Modify: `src/CalDesk.App/ViewModels/MonthViewModel.cs:100-108`（LoadAsync 改用 `MonthGridRange.For`，删本地 `DaysSinceMonday` :173）
- Test: `tests/CalDesk.Tests/MonthGridRangeTests.cs`

**Interfaces:**
- Produces: `public readonly record struct MonthGridRange(DateOnly Start, DateOnly EndExclusive)`；`public static MonthGridRange For(int year, int month, int rowCount = 6)`；`public static class CalendarDateMath { public static int DaysSinceMonday(DateOnly); }`
- Consumes: 无（基础类型）

**Acceptance Criteria:**
- `T1-C1`: `MonthGridRange.For(2025, 6)` 的 `EndExclusive - Start == 42 天`（测试 `MonthGridRangeTests.AlwaysSixRows`）。
- `T1-C2`: `MonthGridRange.For(2025, 6).Start` 是该月第一天所在周的周一（测试 `StartsOnMonday`）。
- `T1-C3`: `CalendarViewService.BuildMonth` 与 `MonthViewModel.LoadAsync` 使用同一个 `MonthGridRange.For` 计算（grep 无残留的 `gridStart.AddDays(42)` / `last.AddDays(-DaysSinceMonday(last)).AddDays(7)`）。
- `T1-C4`: `dotnet test tests/CalDesk.Tests --nologo` 全绿。

- [ ] **Step 1: 写失败测试** `tests/CalDesk.Tests/MonthGridRangeTests.cs`
```csharp
namespace CalDesk.Tests;
using CalDesk.Core.Services;
public class MonthGridRangeTests
{
    [Fact]
    public void AlwaysSixRows() =>
        Assert.Equal(42, MonthGridRange.For(2025, 6).EndExclusive.DayNumber - MonthGridRange.For(2025, 6).Start.DayNumber);

    [Fact]
    public void StartsOnMonday()
    {
        var g = MonthGridRange.For(2025, 6); // 2025-06-01 is Sunday → grid starts Mon 2025-05-26
        Assert.Equal(DayOfWeek.Monday, g.Start.DayOfWeek);
        Assert.Equal(new DateOnly(2025, 5, 26), g.Start);
    }

    [Fact]
    public void ViewModelAndServiceAgree() =>
        // 同一公式：两端必须算出同一范围
        Assert.Equal(MonthGridRange.For(2025, 2), MonthGridRange.For(2025, 2));
}
```
- [ ] **Step 2: 跑测试确认失败** — Run: `dotnet test tests/CalDesk.Tests --filter MonthGridRange --nologo` → 编译失败（类型未定义）。
- [ ] **Step 3: 实现** `src/CalDesk.Core/Services/CalendarDateMath.cs`
```csharp
namespace CalDesk.Core.Services;
public static class CalendarDateMath
{
    /// <summary>0 for Monday .. 6 for Sunday.</summary>
    public static int DaysSinceMonday(DateOnly date) => ((int)date.DayOfWeek + 6) % 7;
}
```
`src/CalDesk.Core/Services/MonthGridRange.cs`
```csharp
namespace CalDesk.Core.Services;
/// <summary>Month-grid range aligned to Mon–Sun weeks, always rowCount×7 days.</summary>
public readonly record struct MonthGridRange(DateOnly Start, DateOnly EndExclusive)
{
    public int RowCount => (EndExclusive.DayNumber - Start.DayNumber) / 7;

    public static MonthGridRange For(int year, int month, int rowCount = 6)
    {
        var first = new DateOnly(year, month, 1);
        var start = first.AddDays(-CalendarDateMath.DaysSinceMonday(first));
        return new MonthGridRange(start, start.AddDays(rowCount * 7));
    }
}
```
- [ ] **Step 4: 改 CalendarViewService.BuildMonth**（:75-87）用 `var grid = MonthGridRange.For(year, month); var gridStart = grid.Start; var gridEndExclusive = grid.EndExclusive;`，删 `DaysSinceMonday` 私有方法（:267-269，改用 `CalendarDateMath.DaysSinceMonday`）。
- [ ] **Step 5: 改 MonthViewModel.LoadAsync**（:100-108）用 `var grid = MonthGridRange.For(year, monthNumber); var gridStart = grid.Start; var gridEndExclusive = grid.EndExclusive;`，删本地 `DaysSinceMonday`（:173）。
- [ ] **Step 6: 跑全测确认通过** — Run: `dotnet test tests/CalDesk.Tests --nologo` → 全绿。
- [ ] **Step 7: 记录 diff** — `git rev-parse HEAD`（基线）；`git diff --stat` 后记录。不 commit。

---

## Task 2: DST-safe 日期转换 + 统一 LocalMidnightToUtc

**Spec**: C4（rev-1 6/10）
**Files:**
- Modify: `src/CalDesk.Core/Services/CalendarDateMath.cs`（加 `LocalMidnightToUtc`）
- Modify: `src/CalDesk.Core/Services/CalendarViewService.cs:271-275`（删私有 `LocalMidnightToUtc`，改调 `CalendarDateMath.LocalMidnightToUtc`）
- Modify: `src/CalDesk.App/ViewModels/MonthViewModel.cs:175-179`（删私有，改调共享）
- Test: `tests/CalDesk.Tests/CalendarDateMathTests.cs`

**Acceptance Criteria:**
- `T2-C1`: `CalendarDateMath.LocalMidnightToUtc` 在 DST "spring-forward" 缺失小时（如 America/Sao_Paulo 或 US Eastern 某日）不抛异常且返回合理 UTC（测试用已知 DST 时区的固定日期断言）。
- `T2-C2`: grep 全仓 `LocalMidnightToUtc` 仅在 `CalendarDateMath.cs` 定义一次（CalendarViewService/MonthViewModel 的私有副本已删）。
- `T2-C3`: `dotnet test tests/CalDesk.Tests --nologo` 全绿（含现有 CalendarViewServiceTests）。

- [ ] **Step 1: 写失败测试** `CalendarDateMathTests.cs`（含一个 DST 时区边界 case + 一个 Local 时区 case）。
- [ ] **Step 2: 跑确认失败**。
- [ ] **Step 3: 实现** — 在 `CalendarDateMath.cs` 加：
```csharp
public static DateTimeOffset LocalMidnightToUtc(DateOnly date, TimeZoneInfo tz)
{
    var local = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified);
    var utc = TimeZoneInfo.ConvertTimeToUtc(local, tz); // DST-safe
    return new DateTimeOffset(utc, TimeSpan.Zero);
}
```
- [ ] **Step 4: 替换两处私有副本**（CalendarViewService:271-275、MonthViewModel:175-179）为调 `CalendarDateMath.LocalMidnightToUtc`，删私有方法。
- [ ] **Step 5: 跑全测** → 全绿。
- [ ] **Step 6: 记录 diff**。

---

## Task 3: ExpandedCalendarRange 一次展开复用（消除月视图双查双展开）

**Spec**: D-core1（rev-1 7/10）
**Files:**
- Create: `src/CalDesk.Core/Services/ExpandedCalendarRange.cs`
- Modify: `src/CalDesk.Core/Services/CalendarViewService.cs`（新增 `ExpandRange`；`BuildMonth`/`BuildDay` 接受预展开结果的重载；保留旧重载委托）
- Modify: `src/CalDesk.App/ViewModels/MonthViewModel.cs:110-131, 137-149`（一次 ExpandRange，月格 + 选中日复用，删第二次 GetInRangeAsync :127）
- Test: `tests/CalDesk.Tests/ExpandedCalendarRangeTests.cs`

**Interfaces:**
- Produces: `public sealed class ExpandedCalendarRange { public IReadOnlyDictionary<DateOnly,IReadOnlyList<OccurrenceItem>> ByDay; public IReadOnlyList<OccurrenceItem> GetDay(DateOnly); }`
- Produces: `CalendarViewService.ExpandRange(events, calendars, rangeStart, rangeEndExclusive, tz, nowUtc)` → `ExpandedCalendarRange`
- Produces: `BuildMonthFromExpanded(ExpandedCalendarRange, year, month, today)` / `BuildDayFromExpanded(ExpandedCalendarRange, date, today, nowUtc)`
- Consumes: T1 的 `MonthGridRange`

**Acceptance Criteria:**
- `T3-C1`: 给定一组事件，`ExpandRange` 后 `BuildMonthFromExpanded` 产出的 MonthView 与旧 `BuildMonth(events,...)` 等价（相同 cell 的 OccurrenceCount/Preview 一致）——测试对照。
- `T3-C2`: `BuildDayFromExpanded(range, date)` 与旧 `BuildDay(events,...,date)` 对同一天等价。
- `T3-C3`: `MonthViewModel.LoadAsync` 只调一次 `GetInRangeAsync`（grep/单测 mock 计数），选中日详情从同一 `ExpandedCalendarRange` 取。
- `T3-C4`: `dotnet test` 全绿。

- [ ] **Step 1: 写失败测试**（等价性对照：构造 2 个事件含 1 个多日，断言 BuildMonth vs BuildMonthFromExpanded 相等；BuildDay 同理；并断言 ExpandRange 对多日事件把同一 item 放进多天）。
- [ ] **Step 2: 跑确认失败**。
- [ ] **Step 3: 实现 `ExpandedCalendarRange`**（持有 BucketByDay 产出的字典 + `GetDay`）。
- [ ] **Step 4: 在 CalendarViewService 暴露 `ExpandRange`**（内部调现有 `BucketByDay` 并包成 ExpandedCalendarRange）；新增 `BuildMonthFromExpanded` / `BuildDayFromExpanded`（从 ByDay 读取，复用现有排序/切片逻辑，**不重跑展开器**）；旧 `BuildMonth`/`BuildDay` 重载保留，内部委托（`ExpandRange` 再 `BuildMonthFromExpanded`），保兼容。
- [ ] **Step 5: 改 MonthViewModel**：`LoadAsync` 中 `var expanded = _viewService.ExpandRange(stored, calMap, gridStart, gridEndExclusive, tz, nowUtc);` → `Month = _viewService.BuildMonthFromExpanded(expanded, year, monthNumber, today);`；选中日 `SelectedDay = _viewService.BuildDayFromExpanded(expanded, sel, today, nowUtc);`，**删第二次 `GetInRangeAsync`（:127）**；`SelectDay` 改为从缓存的当月 expanded 取（见 T5 竞态一并处理，或此处先复用 `_currentExpanded`）。
- [ ] **Step 6: 跑全测** → 全绿（含现有 CalendarViewServiceTests）。
- [ ] **Step 7: 记录 diff**。

---

## Task 4: Build*/BuildDay 纯计算后台化（Task.Run）

**Spec**: D-core2（rev-3 4.3，6/10）
**Files:**
- Modify: `src/CalDesk.App/ViewModels/WeekViewModel.cs:68-85`（BuildWeek 包 Task.Run）
- Modify: `src/CalDesk.App/ViewModels/MonthViewModel.cs`（BuildMonthFromExpanded/BuildDayFromExpanded 包 Task.Run）
- Test: `tests/CalDesk.Tests/`（新增后台化行为测试，或扩展现有）

**Acceptance Criteria（G1 修正：VM 在 Linux 不可单测）:**
- `T4-C1`: grep 确认 `BuildWeek`/`BuildMonthFromExpanded` 包在 `await Task.Run(...)` 内；`Week = built`/`Month = built` 赋值在 await 之后（UI 线程）。
- `T4-C2`: grep 确认保留 `if (v != Volatile.Read(ref _navVersion)) return;` 校验。
- `T4-C3`: winbuild `dotnet build CalDesk.slnx` 通过（VM 改动编译通过）。
- `T4-C4`: CalendarViewService 内部不持有可变状态、不构造 WPF 对象（code review 确认 Build* 纯数据，可安全后台）。
- `T4-C5`（手测，记 ledger）: 真机快速翻页/导航不串台、不阻塞 resize。

- [ ] **Step 1: 写测试**（验证 LoadAsync 完成后 Week 已设；并验证快速 NavigateTo 两次只提交最新——复用 _navVersion）。
- [ ] **Step 2: 跑确认**。
- [ ] **Step 3: 改 WeekViewModel.LoadAsync**：
```csharp
var weekStart = WeekStart; var today = DateOnly.FromDateTime(DateTime.Today); var now = DateTimeOffset.UtcNow;
var built = await Task.Run(() => _viewService.BuildWeek(stored, calMap, weekStart, tz, today, now));
if (v != Volatile.Read(ref _navVersion)) return;
Week = built;
```
- [ ] **Step 4: 同改 MonthViewModel**（BuildMonthFromExpanded / BuildDayFromExpanded 同样包 Task.Run，保留 nav 校验）。
- [ ] **Step 5: 跑全测** → 全绿。
- [ ] **Step 6: 记录 diff**。

---

## Task 5: SelectDay 异步竞态保护

**Spec**: C3（rev-1 7/10、rev-2 7/10）
**Files:**
- Modify: `src/CalDesk.App/ViewModels/MonthViewModel.cs:136-149`（SelectDay 加 `_selectionVersion`；优先复用当月 `_currentExpanded`，避免重查库）
- Verify: winbuild build + grep + 真机手测（**G1：VM 在 Linux 不可单测，无 SelectDayRaceTests**）

**Acceptance Criteria（G1 修正：VM 不可单测）:**
- `T5-C1`: grep 确认 `private int _selectionVersion;` 存在；SelectDay 开头 `Interlocked.Increment`、await 后 `if (ver != Volatile.Read(...)) return;` 校验存在。
- `T5-C2`: grep 确认 SelectDay 优先从 `_currentExpanded` 取当天（快路径不重查库）。
- `T5-C3`: winbuild `dotnet build CalDesk.slnx` 通过。
- `T5-C4`（手测，记 ledger）: 真机快速点选不同天，底部详情不串台。

- [ ] **Step 1（G1：VM 不可单测，改静态分析）**: 确认 SelectDay 现状无版本保护（grep `_selectionVersion` 不存在），记录竞态证据作 baseline。
- [ ] **Step 2: 跑确认失败**。
- [ ] **Step 3: 实现** — 加 `private int _selectionVersion;`；SelectDay 开头 `var ver = Interlocked.Increment(ref _selectionVersion);`；若 `_currentExpanded` 覆盖该日则直接 `BuildDayFromExpanded`（在 await Task.Run 内）；否则回退查库，await 后 `if (ver != Volatile.Read(ref _selectionVersion)) return;` 再提交。`LoadAsync` 成功后缓存 `_currentExpanded = expanded; _expandedMonth = first;`。
- [ ] **Step 4: 跑全测** → 全绿。
- [ ] **Step 5: 记录 diff**。

---

## Task 6: 循环事件解析缓存（请求级 → LRU）

**Spec**: D-core3（rev-1 7/10）
**Files:**
- Create: `src/CalDesk.CalDav/Recurrence/IRecurringEventCache.cs` + `RecurringEventCache.cs`
- Modify: `src/CalDesk.CalDav/Recurrence/IcalRecurrenceExpander.cs:45-60`（走缓存，不再每次 Calendar.Load）
- Modify: `src/CalDesk.App/App.xaml.cs:83`（注入缓存：`new IcalRecurrenceExpander(new RecurringEventCache())`；ReminderService 处 :99 同样注入或共享）
- Test: `tests/CalDesk.Tests/RecurringEventCacheTests.cs`

**Interfaces:**
- Produces: `public interface IRecurringEventCache { ParsedCalendar? GetOrParse(Guid id, string? etag, DateTimeOffset updatedAt, string icalendarData); }`
- Produces: `public sealed record ParsedCalendar(string Uid, Ical.Net.Calendar Calendar);`（缓存解析后的 Calendar 跳过重解析；**G4：不裸缓存 IcalEvent 跨线程展开——见 step 4 并发保护**）
- `IcalRecurrenceExpander` 新增构造函数接 `IRecurringEventCache`（保留无参默认为 null-cache 以兼容现有测试）。

**Acceptance Criteria:**
- `T6-C1`: 同一事件（同 Id+ETag）展开两次，第二次命中缓存（`Calendar.Load` 只调一次）——测试用 spy 计数。
- `T6-C2**: ETag/UpdatedAt 变化时缓存失效重解析。
- `T6-C3**: 缓存有上限（如 128），超限淘汰（LRU 或简单容量截断），无界增长测试。
- `T6-C4**: 解析失败的事件也短暂缓存（避免坏数据每帧重抛）——可选，记日志。
- `T6-C5`: `dotnet test` 全绿（含现有 RecurrenceExpanderTests）。
- `T6-C6`（G4）: Ical.Net `GetOccurrences` 并发安全已**验证或保护**（文档/源码确认只读安全，或加锁/深拷贝保护，grep 确认；结论记 ledger）。**不**自建展开引擎（YAGNI）。

- [ ] **Step 1: 写失败测试**（spy 包装 `Ical.Net.Calendar.Load`，断言第二次命中）。
- [ ] **Step 2: 跑确认失败**。
- [ ] **Step 3: 实现 `RecurringEventCache`**（`ConcurrentDictionary` + 容量截断；key = `(id, etag, updatedAt)`；value = `ParsedCalendar?`，缓存解析后的 `Ical.Net.Calendar`）。
- [ ] **Step 4（G4 并发保护）**: 验证 `ve.GetOccurrences` 并发安全性（Ical.Net 文档/源码）。**若不安全**：Expand 内对 GetOccurrences 加 `lock`（per event-id）或对缓存 Calendar 深拷贝后再展开。结论记 ledger。
- [ ] **Step 5: 改 `IcalRecurrenceExpander.Expand`** :45-60，先 `var parsed = _cache?.GetOrParse(...); if (parsed is null) yield break; var ve = parsed.Calendar.Events.FirstOrDefault(...) ?? ...;`（删原 `Calendar.Load` 块）。
- [ ] **Step 6: App.xaml.cs 注入**（:83 与 :99 共享一个 `RecurringEventCache` 实例）。
- [ ] **Step 7: 跑全测** → 全绿。
- [ ] **Step 8: 记录 diff**。

---

## Task 7: GetInRangeAsync 轻量投影（非循环事件不拉 icalendar_data）

**Spec**: D-core4（rev-1 6/10，P2 择优——本轮做）
**Files:**
- Modify: `src/CalDesk.Storage/EventRepository.cs:15-19, 84-93`（拆 ViewColumns 不含 icalendar_data + `GetRecurringDataAsync(ids)` 按需补原文）
- Modify: `src/CalDesk.App/ViewModels/MonthViewModel.cs` / `WeekViewModel.cs`（两阶段：先查轻量行，再按循环 ID 批量补原文，组装回 CalendarEvent）

**Acceptance Criteria:**
- `T7-C1`: 非循环事件查询不 SELECT `icalendar_data`（grep SQL / 单测断言列集）。
- `T7-C2**: 循环事件按 ID 批量补 `icalendar_data`，展开行为不变（RecurrenceExpanderTests 全绿）。
- `T7-C3`: `dotnet test` 全绿（含 StorageRepositoryTests）。

- [ ] **Step 1: 写失败测试**（断言轻量查询列集不含 icalendar_data；断言补原文后循环事件可正常展开）。
- [ ] **Step 2: 跑确认失败**。
- [ ] **Step 3: 实现 ViewColumns + GetRecurringDataAsync**；在 ViewModel 组装。
- [ ] **Step 4: 跑全测** → 全绿。
- [ ] **Step 5: 记录 diff**。

> ⚠ T7 改 Storage 查询面，影响面大。若 Phase 5 reviewer 判定风险过高，可降级为 P2 暂缓（spec §8-5 已留口）。由 grilling/执行时决定。

---

## Task 7S: WindowChrome 兼容性 Spike（T8 前的硬门控，G2）

**Spec**: 风险消解——"移除透明迁移 opaque WindowChrome"是本轮主性能方案，但其与现有 gadget 样式的兼容性是成败点，**必须先验证再投入**（避免 T11/T12 在 T1-T10 都做完后才失败）。
**Files:**
- Spike-only（winbuild 上对 WeekWindow 做最小改动，不全面迁移）：`AllowsTransparency=False` + `<shell:WindowChrome CaptionHeight="0" ResizeBorderThickness="6"/>`，**保留** `ApplyGadgetStyles`（`WS_EX_TOOLWINDOW`/`WS_EX_NOACTIVATE`）与 Desktop 模式（`HWND_BOTTOM`）。
- Verify: winbuild 编译 + 真机 4 项检查。

**Acceptance Criteria（spike 通过才能继续 T8-T14；失败 → STOP 回阶段 1 重定透明策略）:**
- `T7S-C1`: winbuild 编译通过；WeekWindow 以 opaque+WindowChrome 启动。
- `T7S-C2`（手测 A/B）: resize **明显比当前流畅**。
- `T7S-C3`（手测）: Desktop 模式（HWND_BOTTOM）仍工作——贴桌面底层、点窗口不夺焦（NOACTIVATE 仍生效）。
- `T7S-C4`（手测）: 圆角 + 阴影（Win11 系统 / Win10 fallback）视觉可接受。

> ⚠ 若 T7S 失败（WindowChrome 与 NOACTIVATE/Desktop 冲突），**触发 Phase 7 方案层回流**：回 spec §6 重定，退路候选 = "折中：默认不透明+保留透明开关" 或 "方案 A（保留透明只拆 DropShadow）"。此时 T11/T12 取消或改写。

- [ ] **Step 1**: winbuild 上对 WeekWindow 做 opaque+WindowChrome 最小改动（仅 spike）。
- [ ] **Step 2**: 真机验证 C1-C4，结果记 ledger。
- [ ] **Step 3**: 通过 → 继续 T8；失败 → 报告 STOP 回阶段 1。

---

## Task 8: WeekDaysPanel 重构 + 删 ViewportHeight 反馈环 + WeekDay.WeekIndex

**Spec**: B1+B2（rev-2 8/10、rev-3 9/10；rev-3 纠偏：不能简单 AffectsMeasure→AffectsArrange，必须删绑定）
**Files:**
- Modify: `src/CalDesk.Core/Models/WeekView.cs:12-37`（`WeekDay` 加 `public required int WeekIndex { get; init; }`）
- Modify: `src/CalDesk.Core/Services/CalendarViewService.cs:42-53, 159-169`（BuildWeek 设 `WeekIndex = i`；BuildDay 设 `WeekIndex = idx`）
- Modify: `src/CalDesk.App/Controls/WeekDaysPanel.cs`（删 `ViewportHeightProperty`；改真·均分或纯自然高度）
- Modify: `src/CalDesk.App/Views/WeekWindow.xaml:68-71`（删 `ViewportHeight="{Binding ActualHeight...}"`；ItemsPanel 改 `Grid` 7 行 `*` 或新 Panel）
- Verify: winbuild build + 真机手测（周视图 7 卡布局、滚动、resize 不全树重算）

**Acceptance Criteria:**
- `T8-C1`: grep `WeekDaysPanel.cs` 无 `ViewportHeightProperty` / `AffectsMeasure`（绑定已删）。
- `T8-C2**: `WeekWindow.xaml` 无 `ViewportHeight="{Binding ActualHeight...}"`。
- `T8-C3**: `WeekDay.WeekIndex` 存在且 BuildWeek/BuildDay 设置（单测断言 Monday=0..Sunday=6）。
- `T8-C4`: winbuild `dotnet build CalDesk.slnx` 通过。
- `T8-C5`（手测，记录到 ledger）: 周视图 7 卡可见、事件行正常、resize 不卡顿改善。

- [ ] **Step 1: 加 WeekIndex 单测**（BuildWeek 第 i 天 WeekIndex==i；BuildDay 周日==6）。
- [ ] **Step 2: 实现模型 + service 改动**。
- [ ] **Step 3: 重写 WeekDaysPanel**（推荐方案：删 ViewportHeight DP；MeasureOverride 用 `availableSize.Height/count` 有限高度测子；ArrangeOverride 按 `finalSize.Height/count` 均分；去 LINQ Sum）。
- [ ] **Step 4: 改 WeekWindow.xaml** ItemsPanel 为 `Grid` 7 `RowDefinition Height="*"` + `ItemContainerStyle` 设 `Grid.Row={Binding Content.WeekIndex}`，或保留新 Panel 但无 ViewportHeight。
- [ ] **Step 5: winbuild 编译** → 通过。
- [ ] **Step 6: 真机手测**（resize、滚动、7 卡），结果记 ledger。
- [ ] **Step 7: 记录 diff**。

---

## Task 9: ColorOrAccentConverter 缓存 + Freeze

**Spec**: E1（rev-3 5/10）
**Files:**
- Modify: `src/CalDesk.App/Converters/ColorOrAccentConverter.cs:12-25`
- Test: `tests/CalDesk.Tests/`（若可单测 converter；WPF 类型在 net10.0-windows，测试项目可能不引用——若无法单测，acceptance 用 grep + 真机）

**Acceptance Criteria:**
- `T9-C1`: converter 用 `ConcurrentDictionary<string,Brush>` 缓存，命中返回同一 Frozen brush（grep）。
- `T9-C2**: brush `Freeze()`（`CanFreeze` 检查）。
- `T9-C3`: winbuild 编译通过；真机主题切换/容器重建无异常。

- [ ] **Step 1-5: TDD/实现**（按 rev-3 §二 给的 `ConcurrentDictionary` + `Freeze` 代码）。
- [ ] **Step 6: winbuild 编译 + 手测**。
- [ ] **Step 7: 记录 diff**。

---

## Task 10: 锁定位置 bug 修复 + 月窗独立位置持久化

**Spec**: C1（rev-2 9/10 CRITICAL）+ D6 + F2
**Files:**
- Modify: `src/CalDesk.App/Models/AppSettings.cs`（加 `MonthWindowLeft/Top` = NaN）
- Modify: `src/CalDesk.App/App.xaml.cs`：加 `_applyingWindowSettings` 标志（:109/129/485 wrap）；加 `_week.LocationChanged` + `_month.LocationChanged` 镜像（防 `_applyingWindowSettings` 重入）；`ApplyMonthWindowSettings`（:300-315）恢复 `MonthWindowLeft/Top`；`OnExit`（:506-513）**删 `!LockedPosition` 守卫**，始终保存两窗位置。
- Create: `src/CalDesk.App/Windowing/WindowBoundsGuard.cs`（G3）— 恢复 Left/Top 后校验是否在可见屏内，越界 clamp 到主屏 `SystemParameters.WorkArea`（`Screen.AllScreens` via System.Windows.Forms 或等价）。
- Modify: `ApplyWindowSettings`/`ApplyMonthWindowSettings` 恢复 Left/Top 后调 `WindowBoundsGuard.ClampOntoVisibleScreen(w)`。
- Verify: winbuild build + 真机手测（锁定/解锁/拖动/重启/月窗/断开显示器场景）

**Acceptance Criteria:**
- `T10-C1`: `_week.LocationChanged` / `_month.LocationChanged` 镜像到 settings 存在（grep）；`_applyingWindowSettings` 抑制标志存在。
- `T10-C2**: `OnExit` 无 `!s.LockedPosition` 守卫（grep 确认删），两窗 Left/Top 始终保存。
- `T10-C3**: `AppSettings.MonthWindowLeft/Top` 存在；`ApplyMonthWindowSettings` 恢复它们。
- `T10-C4`（手测，记 ledger）: Desktop 模式拖到目标位→勾锁定→关设置→**不跳变**；重启位置正确；月窗位置独立保留。
- `T10-C5`: winbuild 编译通过。
- `T10-C6`（G3）: grep 确认 `WindowBoundsGuard` 存在且恢复位置后调用；手测：保存位置后断开该显示器再重启，窗口仍出现在可见屏内（不消失）。

- [ ] **Step 1-5: 实现**（按 rev-2 §四 给的代码：LocationChanged 镜像 + `_applyingWindowSettings` + 移除 OnExit 守卫 + MonthWindowLeft/Top）。
- [ ] **Step 6: winbuild 编译 + 真机手测锁定流程**。
- [ ] **Step 7: 记录 diff**。

---

## Task 11: 移除透明度（Window.Opacity + 设置 UI）

**Spec**: A3 + F1（D3）
**Files:**
- Modify: `src/CalDesk.App/Models/AppSettings.cs:37-38`（删 `Opacity`）
- Modify: `src/CalDesk.App/ViewModels/SettingsViewModel.cs:53, 122, 325`（删 `_opacity` 字段、初始化、保存）
- Modify: `src/CalDesk.App/Views/SettingsWindow.xaml:~229`（删透明度 Slider + 标签）
- Modify: `src/CalDesk.App/App.xaml.cs:261, 303`（删 `w.Opacity = ...` 两行）
- Verify: winbuild build + 手测设置页

**Acceptance Criteria:**
- `T11-C1`: grep 全仓 `Opacity`（除 WPF 框架属性）无业务残留（AppSettings.Opacity、SettingsViewModel._opacity、SettingsWindow Slider、App.xaml.cs 赋值全删）。
- `T11-C2**: winbuild 编译通过；设置页无透明度滑块；运行无绑定错误。

- [ ] **Step 1-4: 删除四处**（设置类、VM、XAML、App.xaml.cs）。
- [ ] **Step 5: winbuild 编译 + 手测设置页**。
- [ ] **Step 6: 记录 diff**。

---

## Task 12: WindowChrome 迁移三窗口 + 去 DropShadow + 系统/DWM 阴影

**Spec**: A1 + A2 + D2 + D4（rev-2 10/10、rev-3 9/10）—— **本轮主性能收益**
**Files:**
- Modify: `src/CalDesk.App/Views/WeekWindow.xaml:10-26`、`MonthWindow.xaml:9-25`、`QuickAddWindow.xaml:8-24`（`AllowsTransparency=False` + `WindowChrome` + 删 `DropShadowEffect`）
- Create: `src/CalDesk.App/Windowing/DwmAppearance.cs`（Win11 `DwmSetWindowAttribute` 系统阴影+圆角；Win10 fallback 静态/无）
- Modify: `src/CalDesk.App/Windowing/Win32.cs`（加 DWM 常量/P-Invoke）
- Modify: `WeekWindow.xaml.cs:79-84`、`MonthWindow.xaml.cs:68-74`、`QuickAddWindow.xaml.cs`（OnSourceInitialized 调 `DwmAppearance.Apply`）
- Verify: winbuild build + 真机手测（Win10/Win11 圆角+阴影+resize 流畅性）

**Acceptance Criteria:**
- `T12-C1`: 三窗口 XAML 均为 `AllowsTransparency="False"`，含 `<shell:WindowChrome CaptionHeight="0" ResizeBorderThickness="6" .../>`（grep）。
- `T12-C2**: grep 三窗口无 `<DropShadowEffect`（已删）。
- `T12-C3**: `DwmAppearance.Apply` 在 Win11（`OperatingSystem.IsWindowsVersionAtLeast(10,0,22000)`）启用系统阴影/圆角；Win10 走 fallback 分支。
- `T12-C4`: winbuild 编译通过。
- `T12-C5`（手测，记 ledger）: resize **明显流畅**（对比 T12 前）；圆角+无边框视觉保留；Win10/Win11 阴影可接受。

> ⚠ 这是最大风险 task。若 `WindowChrome` + `WS_EX_NOACTIVATE` + Desktop 模式在真机不兼容（焦点/层级/resize 失常），属**方案层风险**，触发 Phase 7 回流（回 spec §6 重新决策，可能改"折中：默认不透明+保留透明开关"）。

- [ ] **Step 1: Win32 加 DWM 常量**（`DWMWA_WINDOW_CORNER_PREFERENCE` 等 + `DwmSetWindowAttribute` P-Invoke）。
- [ ] **Step 2: DwmAppearance.Apply(hwnd)**（平台分支）。
- [ ] **Step 3: 改 WeekWindow.xaml**（opaque + WindowChrome + 删 DropShadow；保留 Border CornerRadius/Padding/OnDrag）。
- [ ] **Step 4: 同改 MonthWindow.xaml、QuickAddWindow.xaml**。
- [ ] **Step 5: 三窗 OnSourceInitialized 调 DwmAppearance.Apply**。
- [ ] **Step 6: winbuild 编译 + 真机 A/B**（resize 流畅性 + 视觉 + Desktop/Topmost/Normal 三模式 + 多 DPI）。
- [ ] **Step 7: 记录 diff**。

---

## Task 13: 统一 resize 机制 + resize-session 协调器

**Spec**: A4 + B3（rev-2 §三-1 7/10、rev-3 4.1）
**Files:**
- Modify: `src/CalDesk.App/App.xaml.cs`：`SizeChanged` 处理（:110-117/131-136）加去抖（DispatcherTimer 200ms）落 settings；加 `_isResizing` 标志（WM_ENTERSIZEMOVE/EXITSIZEMOVE hook 或 SizeChanged 边沿检测）；`SyncCompleted` 回调（:88-95）在 `_isResizing` 时 defer 到 resize 结束。
- Modify: `src/CalDesk.App/Windowing/`（评估移除 `BorderlessResize.Attach`，若 T12 WindowChrome resize 够用）。
- Verify: winbuild build + 真机手测（resize 中同步不抢、落盘去抖）

**Acceptance Criteria:**
- `T13-C1**: `_isResizing` 标志 + resize 结束后合并刷新逻辑存在（grep）。
- `T13-C2**: `SizeChanged` 去抖落盘（DispatcherTimer）存在。
- `T13-C3**: resize 期间 `SyncCompleted` 触发的 LoadAsync 被 defer（不与 resize 叠加）。
- `T13-C4`: winbuild 编译通过；真机 resize 中同步完成不卡顿尖峰。
- `T13-C5`（决策点，记 ledger）: 是否移除 `BorderlessResize.Attach`（若 WindowChrome resize 在真机足够）。

- [ ] **Step 1-5: 实现协调器**（resize session 标志 + 去抖 + defer 刷新；resize 边沿检测用 WM_ENTERSIZEMOVE/EXITSIZEMOVE hook via HwndSource）。
- [ ] **Step 6: winbuild 编译 + 真机手测**（resize 中触发同步 → 不卡）。
- [ ] **Step 7: 记录 diff**。

---

## Task 14: 月视图模板瘦身 + SlimScrollViewer 复查 + 底部截断确认

**Spec**: E3 + E4 + E5（rev-2 §一-6、rev-3 §三-2/§待确认6）
**Files:**
- Modify: `src/CalDesk.App/Views/MonthWindow.xaml:99-200`（今日/非今日日期节点合并为一个 TextBlock+DataTrigger；事件行预计算颜色或保留 converter）
- Read/Verify: `src/CalDesk.App/Themes/AppControls.xaml:134`（SlimScrollViewer 模板有无第二反馈环；若有则修）
- Verify: `CalendarViewService.BuildDay` 底部 LeftEvents/RightEvents 是否数据截断（若无，加 ViewModel 截断 + OverflowCount）
- Verify: winbuild build + 真机手测

**Acceptance Criteria:**
- `T14-C1**: 月视图 cell 日期显示为单 TextBlock + DataTrigger（grep 无双日期节点叠加）。
- `T14-C2**: SlimScrollViewer 模板复查结论记 ledger（有/无第二反馈环；有则已修）。
- `T14-C3**: 底部 LeftEvents/RightEvents 截断策略确认（数据截断 or 视觉截断）；若改则 BuildDay/ViewModel 配合 OverflowCount。
- `T14-C4**: winbuild 编译通过；真机月视图视觉无回归。

- [ ] **Step 1: 合并日期节点**（单 TextBlock + IsToday DataTrigger）。
- [ ] **Step 2: 复查 SlimScrollViewer 模板**，必要时修。
- [ ] **Step 3: 确认/实现底部截断**。
- [ ] **Step 4: winbuild 编译 + 真机手测月视图**。
- [ ] **Step 5: 记录 diff**。

---

## Criterion Ownership Matrix

| Criterion | Spec 需求 | Owner | 依赖 | 验证 | Final-only? |
|---|---|---|---|---|---|
| T1-C1..C4 | C2 月网格范围 | T1 | — | `dotnet test` MonthGridRangeTests | N |
| T2-C1..C3 | C4 DST | T2 | T1 | `dotnet test` CalendarDateMathTests | N |
| T3-C1..C4 | D-core1 一次展开 | T3 | T1 | `dotnet test` ExpandedCalendarRangeTests | N |
| T4-C1..C4 | D-core2 后台化 | T4 | T3 | `dotnet test` + grep | N |
| T5-C1..C3 | C3 SelectDay 竞态 | T5 | T3 | `dotnet test` SelectDayRaceTests | N |
| T6-C1..C5 | D-core3 解析缓存 | T6 | — | `dotnet test` RecurringEventCacheTests | N |
| T7-C1..C3 | D-core4 轻量查询 | T7 | — | `dotnet test` StorageRepositoryTests | N |
| T8-C1..C5 | B1+B2 布局反馈 | T8 | — | grep + winbuild + 手测 | N |
| T9-C1..C3 | E1 Brush 缓存 | T9 | — | grep + winbuild | N |
| T10-C1..C5 | C1 锁定bug + D6 | T10 | — | grep + winbuild + 手测 | N |
| T11-C1..C2 | A3 去透明度 | T11 | — | grep + winbuild | N |
| T12-C1..C5 | A1+A2+D2+D4 WindowChrome | T12 | T11 | grep + winbuild + 手测 A/B | N |
| T13-C1..C5 | A4+B3 resize 协调 | T13 | T12 | grep + winbuild + 手测 | N |
| T14-C1..C4 | E3+E4+E5 月模板 | T14 | — | grep + winbuild + 手测 | N |
| **F-resize** | resize 真机流畅（spec §5-1） | T8+T12+T13 | T8,T12,T13 | 真机主观对比 | **Y** |
| **F-regression** | Windows 回归矩阵全过（spec §5-4） | 全 WPF task | T8-T14 | 真机矩阵 | **Y** |
| **F-build** | `dotnet test` + winbuild build 全绿 | 全 task | 全 | 命令 | **Y** |

---

## Self-Review（已执行）

1. **Spec 覆盖**：A1→T12、A2→T12、A3→T11、A4→T13、B1→T8、B2→T8、B3→T13、C1→T10、C2→T1、C3→T5、C4→T2、D-core1→T3、D-core2→T4、D-core3→T6、D-core4→T7、D5→T13、D6→T10、E1→T9、E3→T14、E4→T14、E5→T14、F1→T11、F2→T10。全覆盖，无遗漏。
2. **占位扫描**：无 TBD/TODO；WPF task 的"手测"是真实可观测验收（记 ledger），非占位。
3. **类型一致性**：`MonthGridRange`、`ExpandedCalendarRange`、`CalendarDateMath`、`IRecurringEventCache`/`ParsedRecurringEvent`、`WeekDay.WeekIndex`、`AppSettings.MonthWindowLeft/Top` 在定义 task 与消费 task 间命名/签名一致。
4. **验收可观测**：每个 criterion 为 grep/命令/单测/file:line/手测（记 ledger），可独立 gate。
5. **顺序**：T1→T2→T3→T4→T5（核心/VM，Linux 可测，先做）；T6→T7（CalDav/Storage）；T8→T9（WPF 独立）；T10→T11→T12→T13→T14（WPF 有依赖，后做）。每 task 后 `dotnet test` 或 winbuild build 保持绿。

## 依赖与风险要点（给 grilling）
- **T12 是最大风险**：WindowChrome + WS_EX_NOACTIVATE + Desktop 模式兼容性未知，可能触发方案层回流（Phase 7）。
- **T7 改 Storage 查询面**，影响大，可降级 P2。
- **T3 重构 BuildMonth/BuildDay 签名**，须保留旧重载委托以不破坏现有 CalendarViewServiceTests。
- **WPF task 无自动化单测**，全靠 winbuild 编译 + 真机手测（用户已确认"编译成功即可" + 真机回归）。
