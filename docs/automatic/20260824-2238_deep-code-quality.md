# Automatic Deep Code Quality Audit

- **RUN_ID**: `20260824-2238_deep-code-quality`
- **执行时间**: 2026-08-24 22:38 – 2026-08-25 00:0x (UTC+8)
- **审计模式**: 无人值守深度审计（主 Agent + 5 个子 Agent，两波调度）
- **工作目录**: `docs/automatic/.work/20260824-2238_deep-code-quality/`

---

## 1. Executive Summary

**审计结论**：CalDesk 的核心库（Core/CalDav/Storage）工程质量整体**良好偏高**——时区处理历经三次真实 bug 修复后已真正收敛、存储层事务设计严谨、视图竞态防护（navVersion/selectionVersion）连贯一致、170 个离线单测全部真实有效且锁定了历史 bug 的回归。但审计发现 **1 个 P1 数据破坏缺陷**：通过 QuickAdd 编辑任何"非预设 RRULE"的重复事件，会把该事件的重复规则**在本地和服务端同时静默销毁**（PUT 删除 RRULE），违反项目自身文档声明的"complex rules are preserved verbatim"契约（`RecurrencePattern.cs:12-14`）与 `docs/REQUIREMENTS.md` §12。

**问题计数**：**P0×0 · P1×1 · P2×2 · P3×11**（共 14 个 Verified Findings）。

**最值得立即处理**：
1. **CQ-001（P1）** QuickAdd 编辑销毁服务端递归规则 —— 修复时必须同时处理 CQ-007 的 UNTIL 截断隐患，否则修完 CQ-001 会立即激活 CQ-007。
2. **CQ-002（P2）** 全量重同步与在飞周期同步的竞态可导致"空日历 + 成功状态"。
3. **CQ-003（P2）** 推送往返期间的用户编辑被静默丢弃（outbox 合并使丢失不可恢复）。

**总体工程健康度**：良好。未发现结构性/架构级风险；依赖方向纯净（Core 零外向引用）、资源释放（托盘/钩子/订阅/连接租约）审查无泄漏。主要风险集中在 **QuickAdd 编辑路径对非预设递归的处理**与**同步引擎的若干窄窗口竞态**——正是测试盲区（App 程序集零测试、CalDav 59.8% 行覆盖）所在的区域，两者高度吻合。

---

## 2. Repository & Git Baseline

| 项 | 值 |
|---|---|
| Branch | `main` |
| HEAD | `5579e43b0eed69e9e4b3cf1a575ae3f03f846009`（tag `v0.2.1`，= origin/main） |
| 初始工作区 | 干净（`git status --short` 无输出） |
| Remote | 私有 Gitea（`git.vectory.cn:18443/mfreceiver/caldesk`） |

**技术栈**：C# / .NET 10（`Nullable` + `LangVersion=latest` 全仓启用）；WPF 外壳 `net10.0-windows`（CommunityToolkit.Mvvm 8.4.0、H.NotifyIcon.Wpf 2.2.0）；CalDAV 层基于 Ical.Net 5.2.3 + 手写 HTTP/XML；存储层 Microsoft.Data.Sqlite 10.0.10（WAL/FK/迁移）；xunit 2.9.3 + coverlet 6.0.4。构建 `CalDesk.slnx`，版本由 git 派生（`Directory.Version.props`）。本机（Windows）dotnet SDK 10.0.302 可完整编译含 WPF 的全解决方案。

**审计基线**：`dotnet build CalDesk.slnx -c Debug` → **0 警告 0 错误**；`dotnet test tests/CalDesk.Tests` → **170/170 通过、0 跳过**（注意其中 3 个为空跑通过，见 CQ-013）。

---

## 3. Audit Scope

**已覆盖**：
- `src/CalDesk.Core` 全部（领域模型、CalendarViewService、CalendarDateMath、RecurrencePattern、时间类型）— 主 Agent + Agent 1/2
- `src/CalDesk.CalDav` 全部（CalDavClient、MultistatusReader、IcalMapper、IcalRecurrenceExpander/RecurringEventCache、SyncEngine）— 主 Agent + Agent 1/2/4/5（含执行验证）
- `src/CalDesk.Storage` 全部（CalDeskDatabase、4 个仓储、事务租约）— 主 Agent + Agent 1/2/3
- `src/CalDesk.App` 静态审计（App.xaml.cs、SyncRunner、QuickAddViewModel、ReminderService、SettingsService、SettingsViewModel、两个视图 VM 的竞态协议）— Agent 1/4/5；Agent 5 额外对真实 `SyncRunner`/`QuickAddViewModel` 做了**执行级**验证（本地回环 HTTP 假 CalDAV 服务器 + 真实 SQLite）
- `tests/CalDesk.Tests` 全部 22 个文件的真实性逐一分类 — Agent 3
- Git 历史（时区三连修 2eb1634/04f8eb6/f9d769e、月视图竞态 783e6c1、递归缓存锁 66a805d、T7 轻量化 0aeda19 等热点的考古）
- 工具链：build、test×3、XPlat coverage、`dotnet format --verify-no-changes`、`-warnaserror`

**未覆盖 / 限制**：
- 无真实 CalDAV 服务器（live 集成测试未配 env）；服务器交互全部经由 Agent 5 的本地回环假服务器与脚本化 `HttpMessageHandler`，未覆盖真实 Radicale/Google 的 ETag 引号形态差异、慢网络、TLS 行为。
- WPF 视觉/交互（渲染、DPI、无边框拖拽）未做运行时验证；`Windowing/*` 仅静态审阅（Agent 1 判定无泄漏）。
- App 程序集无自动化测试（结构性盲区，见 §10）。

---

## 4. Commands & Tests Executed

| 命令 | 结果 |
|---|---|
| `dotnet build CalDesk.slnx -c Debug` | 成功，0 警告 0 错误（含 WPF App），版本派生 `0.2.1` |
| `dotnet test tests/CalDesk.Tests --nologo`（基线 / 复跑 / 终验共 3 次） | 170/170 通过、0 跳过、~1s，无 flake（其中 3 个空跑通过见 CQ-013） |
| `dotnet build CalDesk.slnx -c Debug -warnaserror` | 通过——零警告状态属实 |
| `dotnet test --collect:"XPlat Code Coverage"` | 覆盖率：**总体 73.9% 行 / 63.1% 分支**；Core 88.1/81.1，Storage 83.1/70.4，**CalDav 59.8/50.7**；App 0%（不在测试工程引用内） |
| `dotnet format CalDesk.slnx --verify-no-changes --diagnostics CS --severity warn` | exit 2：**122 处空白错误**（App.xaml.cs 93、MonthGridRange.cs 16、测试文件 13），0 个 CS 分析器问题；确认只读未改文件 |
| Agent 2/3/4/5 各自的可执行探针 | 控制台工程引用真实 src 工程，驱动生产代码路径（IcalMapper/Expander/Storage/SyncEngine/CalDavClient/SyncRunner/QuickAddViewModel + 本地回环 HTTP 服务器）；输出存于 `.work/<RUN>/agent-*/` |
| 主 Agent 的 `git log/blame/show`、全文件精读 | 时区修复谱系、竞态加固历史、上述全部 findings 的主 Agent 复核 |

---

## 5. Verified Findings

> 所有 findings 均经状态机 DISCOVERED→(REPRODUCED/STATICALLY_PROVEN)→ADVERSARIALLY_REVIEWED→MAIN_AGENT_RECHECKED→VERIFIED。主 Agent 对每条的核心代码链路做了独立精读复核；标注 REPRODUCED 的均有可执行复现（产物在 `.work/`）。

---

### CQ-001 — QuickAdd 编辑任何非预设 RRULE 的重复事件会静默销毁其重复规则（本地 + 服务端 PUT）

**Severity**: P1　**Confidence**: 确定（双独立执行复现 + 主 Agent 全链路精读）

**Location**:
- `src/CalDesk.App/ViewModels/QuickAddViewModel.cs:154-158`（编辑加载：精确字符串匹配）
- `src/CalDesk.App/ViewModels/QuickAddViewModel.cs:343-349`（保存：非预设 → `RecurrenceRule = null`）
- `src/CalDesk.CalDav/Calendar/IcalMapper.cs:358-361`（`EditInPlace` else 分支：`ve.RecurrenceRule = null`）
- `src/CalDesk.CalDav/CalDavClient.cs:172`（`PutEventAsync` 每次都 `IcalMapper.Serialize(evt)`，从不原样上传 raw）

**Relevant Git Context**: `docs/REQUIREMENTS.md` §12 明确禁止降级服务端系列；`RecurrencePattern.cs:12-14` 的类文档写明 "Existing complex rules are **preserved verbatim** in CalendarEvent.RecurrenceRule"——本行为直接违反代码自身声明的契约。

**Trigger**: 用户双击任意一个**由其他客户端（或任何非 QuickAdd 预设字符串）创建**的重复事件（如 `FREQ=WEEKLY`（无 BYDAY）、`FREQ=DAILY;INTERVAL=2`、带 COUNT/UNTIL 的规则、`FREQ=YEARLY` 生日、多日 `BYDAY=MO,WE`），在 QuickAdd 中仅改标题并保存。

**Execution Path**（主 Agent 逐步核实）:
1. `App.OpenQuickAddAsync`（App.xaml.cs:571-596）对**任意**事件打开 QuickAdd，无 recurring 过滤；Repeat ComboBox 始终可编辑（QuickAddWindow.xaml:227-230）。
2. `InitializeForEdit`（QuickAddViewModel.cs:154-158）用**三个精确字符串**比对 `evt.RecurrenceRule`：`"FREQ=DAILY;INTERVAL=1"` / `"FREQ=WEEKLY;INTERVAL=1;BYDAY=<DTSTART本地星期>"` / `"FREQ=MONTHLY;INTERVAL=1"`；不匹配 → `Repeat = None`。
3. `SaveAsync`（:343-349）switch 的 `_ => null` → `evt.RecurrenceRule = null`。
4. `EventService.UpdateAsync`（EventService.cs:52-66）置 `ModifiedLocal` + 入 outbox。
5. 推送：`SyncEngine.PushOneAsync:344` → `CalDavClient.PutEventAsync:172` → `IcalMapper.Serialize` → raw 非空 → `EditInPlace`：`evt.IsRecurring`（= `RecurrenceRule` 非空白，CalendarEvent.cs:57）为 false → **`ve.RecurrenceRule = null`**。
6. PUT body 无 RRULE —— **服务端系列定义被删除**，所有客户端受影响。

**Evidence / Reproduction**: 两路独立执行复现——Agent 2 控制台复现（`agent-2/repro/`）与 Agent 5 真实栈复现（真实 `QuickAddViewModel` + 真实 `SyncRunner`/`SyncEngine` + 真实 SQLite + 本地回环 CalDAV 服务器，抓取实际 PUT body）：编辑 `FREQ=WEEKLY;INTERVAL=2` 事件后 PUT body 无 RRULE，展开出现次数 4 → 1。Agent 4 对抗审查穷尽了所有可能的守卫（无 recurring 过滤、ComboBox 未禁用、`UpdateAsync` 无其他 caller）后 **CONFIRMED**。

**Variants**（同族危害）:
- **(b) 拉取侧先行降级**：`RuleToRrule`（IcalMapper.cs:245-286）只保留 FREQ/INTERVAL/COUNT/UNTIL/BYDAY——`BYMONTHDAY` 等在**拉取时已丢**。`FREQ=MONTHLY;BYMONTHDAY=15` 存为 `FREQ=MONTHLY;INTERVAL=1`，恰好匹配 Monthly 预设；保存后上传不含 BYMONTHDAY 的规则。当 BYMONTHDAY ≠ DTSTART 日（规则曾单独编辑过）时月份日**静默移动**。
- **(c) 跨午夜星期错配**：Weekly 预设比对用 `startLocal.DayOfWeek`（本地化的星期）；服务端 BYDAY 语义基于事件自身时区，两者在 UTC→本地转换跨午夜时可能错一天 → 不匹配 → 同样被清空。

**Expected Behavior**: 非预设规则应原样保留（类文档已如此承诺）；至少 UI 不应把不认识的规则显示为"不重复"并允许无警告保存。

**Root Cause**: 用字符串相等（而非解析比较）将任意 RFC 5545 规则映射到 3 个预设；保存路径无条件以预设覆盖/null 化 `RecurrenceRule`，且 PUT 走重序列化而非保留原 raw 的 RRULE。

**Impact**: 常规用户路径；服务端数据不可逆破坏（除非服务端有备份）；静默（UI 报成功、Pushed++）。违反 REQUIREMENTS §11（禁止静默数据丢失）与 §12。

**Recommended Fix**: ① `InitializeForEdit` 改用 `RecurrencePattern.Parse` 判断"是否恰好等于某预设语义"，非预设时 Repeat 显示占位项（如"自定义（保留）"）且**保存时不触碰** `RecurrenceRule`；② 或最简兜底：`Repeat == None` 且原 `evt.RecurrenceRule` 非空时弹确认。③ 长期：`EditInPlace` 对"本地规则未变"的事件**不重写** `ve.RecurrenceRule`（保留 raw 中的原 RRULE 节点）。**注意：采用 ③ 或保留原规则字符串时，必须先修 CQ-007（UNTIL 截断），否则会把 `UNTIL=<DATE-TIME>` 截断成日期并再丢最后一个实例。**

**Recommended Regression Test**: xunit：给定 raw ICS 含 `RRULE:FREQ=WEEKLY;INTERVAL=2`，走 `IcalMapper.Serialize`（模拟仅改标题的 Update outbox 推送），断言输出 ICS 仍含原 RRULE 且展开次数不变。

**Adversarial Review**: Agent 4 穷举守卫（入口过滤/ComboBox 禁用/其他 caller/FR-405 编辑器缓期声明——后者只覆盖"复杂编辑器"未覆盖标题编辑）均不成立；并修正了两个次级表述（BYMONTHDAY 变体的真实机制如上；"UNTIL→无限系列"不成立——None 路径是清空成单次，无限只在用户主动再选预设时发生）。

---

### CQ-002 — FullResyncAsync 与在飞周期同步竞态：可留下"空日历 + 有效 token + 成功状态"，增量同步从此拉不到任何东西

**Severity**: P2　**Confidence**: 确定（真实组件执行复现 + 主 Agent 精读）

**Location**:
- `src/CalDesk.App/Services/SyncRunner.cs:253-260`（`FullResyncAsync`：4 次清库后调 `RunAsync`，不检查 `_running`）
- `src/CalDesk.App/Services/SyncRunner.cs:148-152`（`RunAsync` CAS 失败**静默返回**——调用方视为成功）
- `src/CalDesk.CalDav/Synchronization/SyncEngine.cs:145-151`（在飞 run 在清库后把 sync-token 写回被清空的库）
- `src/CalDesk.CalDav/Synchronization/SyncEngine.cs:79`（后续 run 只按 token 存在与否分支，从不看事件表是否为空）

**Trigger**: 5 分钟周期定时器的 `RunAsync` 正在拉取中（秒级窗口），用户此时在设置里点"全量重新同步"。

**Execution Path**: 周期 run 已 CAS `_running=1` 并在 `SyncAsync:39` 取走含旧 token 的 `cals` → 用户确认对话框 → `FullResyncAsync` 执行 4 次 DELETE（events/tokens/sync_state/outbox）→ 其内部 `RunAsync` 在 CAS 处**静默 no-op** → `FullResyncAsync` 正常返回，设置页显示成功（SettingsViewModel.cs:361）→ 被阻塞的周期 pull 返回（静默服务器：0 变更）→ `ApplySyncCollectionAsync:145-151` 把 token 重新写回空库 → 之后每次增量 `PullAsync:79` 见 token 有效 → 0 拉取。

**Evidence / Reproduction**: Agent 5 用**真实 SyncRunner + 真实 SyncEngine + 真实 SQLite 临时库 + 本地回环假服务器**（假服务器把周期 run 的 REPORT 挂起以制造交错）完整复现：终态 0 事件、每次 run `pulled=0 errors=0`、状态"同步于…"，且可无限持续。反事实对照：无竞态的 FullResync 成功恢复 3 个事件。关键判定：**空事件表 + token 存在 = 卡死；token 缺失 = 自动恢复**——竞态留下的恰是前者。

**Expected Behavior**: 全量重同步应等待/吸收在飞 run，或保证清库后必有一次以空 token 起步的全量拉取。

**Root Cause**: `FullResyncAsync` 与 `_running` CAS 之间无互斥；CAS 失败被当成功；在飞 run 的 token 回写无"库已被清空"防护。

**Impact**: 本地日历持续空白而 UI 报成功（服务器数据无恙）。缓解因素（对抗审查后定级 P2 而非 P1）：窗口为每 5 分钟内秒级；再次点击全量重同步（无在飞 run 时）即完全恢复；服务器任何变更会部分自愈。但用户恰恰在"感觉数据不对"时才点全量重同步，误判风险高。

**Recommended Fix**: `FullResyncAsync` 在清库前先获取同步权：自旋/等待 `Interlocked.CompareExchange(ref _running, 1, 0)` 成功（或等在飞 run 结束再清库），清库+全量拉取完成后释放；或给 `SyncEngine` 增加代数/世代号，检测到 `cals` 快照过期则丢弃 token 回写。

**Recommended Regression Test**: 用 Agent 5 的假服务器骨架（已在 `.work/agent-5/`）固化为测试：阻塞周期 pull → 并发 FullResync → 断言终态事件数 = 服务器事件数。

**Adversarial Review**: Agent 4 确认机制无守卫、但以"本地可恢复 + 窗口窄"将 P1 降为 P2；Agent 5 的执行复现证实最坏交错确实发生且用户无感知。

---

### CQ-003 — PushOneAsync 往返窗口内的用户编辑被静默覆盖且 outbox op 被删（合并复用使丢失不可恢复）

**Severity**: P2　**Confidence**: 确定（主 Agent + 对抗审查双重静态证明；机制确定性，无需运行时构造）

**Location**:
- `src/CalDesk.CalDav/Synchronization/SyncEngine.cs:316`（取事件快照）→ `:344`（PUT）→ `:353`（**全列**回写快照为 Clean）→ `:354`（删除 op）
- `src/CalDesk.Storage/EventRepository.cs:226-245`（`ON CONFLICT DO UPDATE` 覆盖所有列）
- `src/CalDesk.Storage/SyncOperationRepository.cs:44-84`（`EnqueueAsync` **复用**已存在的 pending op 行）

**Trigger**: 同一事件被推送期间（PUT 往返，百毫秒~数秒），用户再次编辑该事件。

**Execution Path**: `PushOneAsync:316` `GetAsync` 得到 pre-edit 快照 → 用户 `EventService.UpdateAsync` 写入新值（ModifiedLocal）且 `EnqueueAsync` **复用同一 pending op 行**（该行在 PUT 期间仍存在）→ `:353` `UpsertAsync(pre-edit 快照)` 全列覆盖用户编辑并置 Clean → `:354` `RemoveAsync` 删除 op → 用户编辑**本地与服务器双侧丢失、零痕迹**（无 Conflict、无 pending 计数）。

**Expected Behavior**: 推送回写不应覆盖推送开始后的本地修改（例如回写前重读比较 `UpdatedAt`，或 op 删除前检查事件是否仍 dirty）。

**Impact**: 静默数据丢失，但窗口 = 单次 PUT 往返且需对同一事件二次编辑，概率受限 → P2。

**Recommended Fix**: `PushOneAsync` 在 `:353` 前重读事件行，若 `UpdatedAt > 快照.UpdatedAt` 或 `SyncStatus != op 时的预期状态`，则保留新值仅回写 `RemoteHref/ETag`，并保留 op 供下轮推送。

**Recommended Regression Test**: 假 client 在 `PutEventAsync` 内回调注入一次 `UpdateAsync`，断言该编辑在 push 完成后仍存在且最终被推送。

**Adversarial Review**: Agent 4 复核后结论"比声称的更糟"——原以为编辑会留下 ModifiedLocal 行，实际因 op 复用被 `:354` 一并删除，完全不可恢复；同时确认无任何现有守卫（If-Match 只防服务端竞态）。

---

### CQ-004 — RECURRENCE-ID 例外被丢弃：被移动的单次实例在视图中重复显示或消失

**Severity**: P3（已声明的缓期范围）　**Confidence**: 确定（执行复现）

**Location**: `src/CalDesk.CalDav/Synchronization/SyncEngine.cs:209`（`parsed.Events[0]`——只取第一个 VEVENT）；`src/CalDesk.CalDav/Recurrence/IcalRecurrenceExpander.cs:73-74`（展开只用 master，不减去例外）

**Trigger**: 服务器事件带例外（Google/主流客户端"仅修改此实例"产生的 master + RECURRENCE-ID 结构）。同资源例外：被移动的实例既按原时间展示（master 展开）又……实际不展示新时间；分离资源例外：原时间与新时间**都显示**（重复）。

**Evidence**: Agent 2 执行复现（三种布局：分离资源重复显示 / 同资源移动不可见 / 例外在文件首位时整系列消失）；Agent 4 独立探测确认 Ical.Net `GetOccurrences(master)` 不会自动减去同资源例外，且 `IcalMapper.Map` 从不赋值 `RecurrenceId`。

**Expected / Actual**: 例外实例应替换 master 展开中的对应实例；实际是丢弃或双显。

**Root Cause**: MVP 只展开 master，例外 VEVENT/资源被 `Events[0]` 丢弃。

**Impact**: 展示层错误（无本地数据损坏，raw ICS 保留）。**定级说明**：`docs/REQUIREMENTS.md` §12 明确将"修改本次及以后（RECURRENCE-ID）"列为**暂缓**，MVP 例外范围仅 EXDATE——故为已知限制而非违约缺陷；但"重复显示"仍是一个用户可见的显示错误，建议在路线图排期时优先于纯缺失。

**Recommended Fix**: ① 最小：展开后按 `RecurrenceId` 减去/替换 master 实例（同资源）；② 分离资源例外按 href→master 关联合并。修复前建议对含 RECURRENCE-ID 的事件在 UI 上加"含例外（未完全支持）"标记，避免用户误信。

**Adversarial Review**: Agent 4 以 REQUIREMENTS §12 缓期声明为由从 P2 降为 P3；重复显示行为本身经其独立探测确认存在。

---

### CQ-005 — 拉取侧把 FREQ=HOURLY/MINUTELY/SECONDLY 规则静默降级为单次事件

**Severity**: P3　**Confidence**: 确定（主 Agent 精读 + 探测复现）

**Location**: `src/CalDesk.CalDav/Calendar/IcalMapper.cs:252-259`（freq switch 的 `_ => null`）

**Trigger**: 服务器上存在 RFC 5545 完全合法的 `RRULE:FREQ=HOURLY;INTERVAL=2` 事件（主流 GUI 客户端造不出，但脚本/API 可以）。

**Behavior**: `RuleToRrule` 对非 DAILY/WEEKLY/MONTHLY/YEARLY 频率返回 null → `RecurrenceRule=null` → `IsRecurring=false` → 仅显示 master 单次；且一旦编辑该事件即触发 CQ-001 连锁（服务端规则被清）。

**Mitigations**: raw ICS 保留、服务器数据不动（除非编辑）；REQUIREMENTS FR-402 将创建范围限定三种频率；现实罕见。→ P3。

**Recommended Fix**: `RuleToRrule` 保留原样字符串（未知频率透传，反正展开走 Ical.Net raw 路径）；或至少 quar­antine 为 Error 而非静默单次。

---

### CQ-006 — 前缀/非常规 TZID（`/mozilla.org/…` 等）导致整资源被隔离为"(unparseable resource)"

**Severity**: P3　**Confidence**: 确定（双 Agent 执行复现，含 VTIMEZONE 对照组）

**Location**: `src/CalDesk.CalDav/Calendar/IcalMapper.cs:157`（`TimeZoneInfo.FindSystemTimeZoneById(dt.TzId)`——系统 ID 查找，不看资源内 VTIMEZONE）

**Trigger**: 服务器事件使用 RFC 5545 §3.8.7.5 允许的全局 TZID 前缀形式（Mozilla Sunbird/旧 KOrganizer 产物，`/mozilla.org/20050126_1/Europe/Berlin`、`/freeassociation.sourceforge.net/…`），且机器上没有同名 Windows 时区。

**Behavior**: `FindSystemTimeZoneById` 抛异常 → `TryToCalendarEvents` 失败 → G5-21 隔离：事件以"(unparseable resource)"占位符出现在当前周 + 同步出错状态。raw 保留、不影响其他客户端。

**Evidence**: Agent 3 探测（无 VTIMEZONE 与**带完整 VTIMEZONE** 两组均失败——证明与资源完整性无关，是系统查找语义）；Agent 4 独立复测确认。

**Recommended Fix**: TZID 解析前剥离已知前缀（`/mozilla.org/…/`、`/freeassociation.sourceforge.net/`）再查；查不到时回退 Ical.Net 内建 tzdb 或资源内 VTIMEZONE 定义的偏移。

---

### CQ-007 — RecurrencePattern 的 UNTIL 变换丢时间成分：`UNTIL=<DATE-TIME>` → `UNTIL=<DATE>`（当前不可达的活性隐患，修复 CQ-001 时会被激活）

**Severity**: P3（latent）　**Confidence**: 变换本身确定（双 Agent 执行验证 + 主 Agent 精读）；当前生产路径不可达（对抗审查证实）

**Location**: `src/CalDesk.Core/Recurrence/RecurrencePattern.cs:61`（`ToRrule` 只输出 `yyyyMMdd`）与 `:125-141`（`ParseUntil` 剥掉时间）；消费点 `IcalMapper.cs:317,352`（`Parse(evt.RecurrenceRule)?.ToRrule()`）

**Behavior**: `FREQ=DAILY;UNTIL=20260715T230000Z` 经 Parse→ToRrule 变为 `FREQ=DAILY;UNTIL=20260715`——Ical.Net 把日期型 UNTIL 当本地午夜，**丢掉最后一天的实例**（复现：4→3），且违反 RFC 5545 §3.3.10（UNTIL 类型必须与 DTSTART 匹配）。同变换还会丢 `INTERVAL=1`（无害）与 BYMONTHDAY（有害，同 CQ-001 变体 b）。

**为什么当前不可达**: 拉取侧 `RuleToRrule`（IcalMapper.cs:273）保留完整 DATE-TIME UNTIL 存库；而唯一会带着 UNTIL 走到 `Serialize` 的变异路径是 QuickAdd——它先因精确匹配失败把规则整个置 null（CQ-001 路径），截断反而轮不到发生。Agent 4 据此将"任何推送都会截断 UNTIL"的原始 P1 主张**驳回**；Agent 5 的 R2 探针是直接以带 UNTIL 的事件对象调 `EventService.UpdateAsync`（绕过 QuickAdd 映射）——机制验证有效但当时不构成用户可达路径。

**为什么要报**: 任何"修复 CQ-001 时保留原规则字符串"的方案都会把规则重新送进 `Parse→ToRrule`，**立即激活本截断**。必须在同一变更中让 `RecurrencePattern` 支持 DATE-TIME UNTIL（或让 `EditInPlace` 对未变更的规则保留原 RRULE 文本）。

**Recommended Fix**: `Until` 改存 `DateTime?`（UTC）或并行保留原文；`ToRrule` 按 DTSTART 类型输出 DATE 或 DATE-TIME。

---

### CQ-008 — sync-collection 响应中缺 calendar-data 的条目被静默丢弃，而 token 照常前进（违反 G5-21 自身原则）

**Severity**: P3　**Confidence**: 静态证明（主 Agent 精读）；触发依赖服务器行为

**Location**: `src/CalDesk.CalDav/CalDavClient.cs:239-242`（`Where(... !string.IsNullOrEmpty(calendar-data))` 过滤）+ `SyncEngine.cs:145-151`（token 无条件保存）

**Trigger**: 服务器对某变更 href 返回 200 但不带 calendar-data 属性（截断响应、代理剥离、非规范服务器）。

**Behavior**: 该 href 的变更**永久丢失**（token 已前进，下次增量不会再给）；对比之下解析失败有 G5-21 隔离（SyncEngine.cs:181-201 注释原话："must NOT be silently dropped — doing so would let sync advance its token and permanently lose the resource"）——同样的推理没有覆盖到"无 body"这一形态。

**Recommended Fix**: 缺 body 的 changed-href 单独收集，交由引擎决定：按 href 显式 GET 补拉一次，或记入 Errors（与解析失败同等对待）。

---

### CQ-009 — SyncCollectionAsync 把 HttpRequestException 当"不支持该特性"：瞬时网络错误触发全量重拉；且全量重拉（含 RFC 6578 token 失效恢复路径）从不回收服务端已删事件

**Severity**: P3　**Confidence**: 机制静态证明（主 Agent 精读）

**Location**: `src/CalDesk.CalDav/CalDavClient.cs:233-236`（`catch (HttpRequestException) → Supported:false`）；`SyncEngine.cs:99-121`（Unsupported → 回落 init 分支）；`SyncEngine.cs:133-152`（init 全量只应用响应中的 Changed/Deleted，不与本地行对账）

**Behavior**: ① 瞬时网络错误 → `Supported:false` → 回落到空 token 的 init 调用：网络已恢复则做一次**不必要的全量重拉**，网络仍坏则二次抛出被逐日历隔离记录（正确）。② 更实质的缺口：空 token 全量响应**不含历史删除**（RFC 6578 语义），而 `ApplySyncCollectionAsync` 不做本地对账 → 自上次 token 以来服务端删除的事件在本地永久僵尸。该缺口同样影响 token 失效恢复路径（`SyncEngine.cs:86-97`，RFC 6578 §3.2 强制要求客户端届时"re-sync"，业界做法是与本地全量 diff）。注：Agent 2 最初描述的"回落 ±180 天时间窗查询"不准确——`SupportsSyncToken=true` 时回落的是 sync-collection init 而非 time-range 查询（已由主 Agent 更正后并入本条）。

**Recommended Fix**: 网络异常直接传播（由逐日历隔离记录、下轮重试）；全量重拉后增加"本地 Clean 行不在响应 href 集合中 → 删除"的对账步骤。

---

### CQ-010 — 启动早期失败 → OnExit 对 `_settingsSvc`（`null!`）解引用产生二次崩溃

**Severity**: P3　**Confidence**: 静态证明（主 Agent 精读）

**Location**: `src/CalDesk.App/App.xaml.cs:762`（`OnExit` 第一行 `var s = _settingsSvc.Current;`）vs `:80-82`（DB/Storage 构造在 `_settingsSvc` 赋值**之前**）与 `:197-206`（catch → `Shutdown(-1)`）

**Trigger**: `OnStartup` 在 `_settingsSvc` 赋值前抛出——现实诱因：SQLite 打不开/损坏/被锁（`new StorageServices(db)` 阶段）。

**Behavior**: 用户先看到"启动失败"错误框，随后 `Shutdown(-1)` 触发 `OnExit` → NRE → 第二个崩溃（且 OnExit 其余清理被跳过）。

**Recommended Fix**: `OnExit` 全程防御（`if (_settingsSvc is null) return;` 或字段可空化）；或把 settings 加载挪到 DB 构造之前。

---

### CQ-011 — ReminderService 定时器回调可重入：async 回调无重入门 + `_fired` HashSet 非线程安全

**Severity**: P3　**Confidence**: 机制静态证明；触发需 CheckAsync > 30s

**Location**: `src/CalDesk.App/Services/ReminderService.cs:22,29-30`（`Timer(_ => _ = CheckAsync(), …, 30s)` + `HashSet<string> _fired`）

**Trigger**: 某次 `CheckAsync`（含 DB 往返 + 递归展开，可能争用全局 `ExpandLock`）超过 30s 周期。

**Behavior**: 下一拍回调并发进入 → 并发 `HashSet.Add`（.NET 上可抛异常/丢项/极端情况死循环）+ 潜在重复通知。

**Recommended Fix**: `Interlocked` 重入门（`if (Interlocked.Exchange(ref _checking, 1) == 1) return;` try/finally 复位），或将 `_fired` 换 `ConcurrentDictionary`。

---

### CQ-012 — SettingsService 非原子写 + 全吞异常：崩溃窗口内 settings.json 损坏 → 下次启动静默重置为默认值

**Severity**: P3　**Confidence**: 静态证明（主 Agent 精读）

**Location**: `src/CalDesk.App/Services/SettingsService.cs:37-49`（`File.WriteAllText` 直写 + `catch {}`）与 `:19-34`（Load 失败 → `new AppSettings()`）

**Trigger**: 写入中途崩溃/断电（文件截断）或目标盘瞬时不可写。

**Behavior**: 半截 JSON → 下次 `JsonSerializer` 抛出 → catch → **静默全默认**（主题、窗口位置、默认日历、提醒偏好一次性丢失），无日志无提示。

**Recommended Fix**: 写临时文件 + `File.Replace`（同目录保证原子性）；Load 失败时备份损坏文件并提示，而非静默重置。

---

### CQ-013 — live 集成测试在 env 缺失时空跑且被计为 PASSED（"170/170 绿"包含 3 个空测试）

**Severity**: P3　**Confidence**: 静态证明（主 Agent + 两 Agent 复核）

**Location**: `tests/CalDesk.Tests/CalDavIntegrationTests.cs:14-19`（静态 `Configured` 属性）及测试体内 `if (!Configured) return;` 模式（3 个测试）；csproj 无 `xunit.skippablefact` 包

**Behavior**: 无 `CALDAV_TEST_*` env 时这 3 个测试 1–5ms"通过"，报告 `已跳过: 0`——`AGENTS.md`"默认 skip"的表述与实际不符，基线数字被虚增，且 live 路径的**真实失败会被误认为绿色**（若 env 半配置导致断言路径未走到）。

**Recommended Fix**: 引入 `SkippableFact`（或运行前 `Assert` 配置完整性），让报告呈现真实的 skipped 计数。

---

### CQ-014 — ETag 为空的服务端事件：本地删除不上传且计为 Pushed++，下次拉取"复活"

**Severity**: P3　**Confidence**: 静态证明（主 Agent 精读）；触发依赖无 ETag 服务器

**Location**: `src/CalDesk.CalDav/Synchronization/SyncEngine.cs:330-341`（`if (RemoteHref 非空 && ETag != null)` 才 DELETE，否则仅本地硬删 + `Pushed++`）

**Trigger**: 服务器不返回 getetag（RFC 允许）的事件被用户删除。

**Behavior**: 远端 DELETE 被跳过但计入成功推送；下次 pull 该资源重新插入为 Clean——用户看到删除的事件回来。当前主力服务器（Radicale）总返回 ETag，故现实面窄。

**Recommended Fix**: ETag 缺失时执行无条件 DELETE（无 If-Match），失败则按错误处理而非静默跳过；至少不得计入 `Pushed`。

---

## 6. Architecture & Maintainability Observations

（有工程价值但非缺陷的观察，均经 Agent 1 静态核实）

1. **App 是不可测的巨型组合根**：`App.xaml.cs` 827 行、26 次提交（churn 约为第二名 2 倍）；`SyncRunner` 焊死具体 `StorageServices`/工厂类型，`SettingsViewModel` 向下转型 `_factory is CalDavClientFactory`。测试工程只引用 Core/CalDav/Storage——本报告 P1/P2 全部位于 App 缝隙或其触发的推送链，与该结构盲区精确重合。建议逐步抽 `IAppServices` 组合接口。
2. **同步引擎按设计只跑 `accounts[0]`**，而 DB schema/仓储都为 N 账户建造——与单账户设置 UI 一致，非 bug，但建议加注释/守卫防止未来误用。
3. **`IcalMapper.ToUtc(null)` 把缺 DTSTART 的 VEVENT 映射为"现在"** 而非隔离（`IcalMapper.cs` ToUtc 分支）——畸形但能解析的资源变成一个"此刻"事件。
4. **`EditInPlace` 解析失败时回落 `BuildICalendar`**，静默丢 VTIMEZONE/X-/VALARM（与 NFR-05"编辑保留原资源"目标相悖；概率低——拉取时能解析）。
5. **日期数学仍有三处平行实现**（`WeekViewModel.cs:130-135` vs `CalendarDateMath.cs:9`：两种"周一"公式；day-code 映射 3 份）；`AppSettings.WeekStart` 是死配置（定义后从未被读取）。
6. **`SyncEngine.cs:369` 的 `ex.Message.Contains(" 412 ")`** 与自身"永不嗅探消息文本"的注释原则矛盾（typed `StatusCode` 已覆盖；此分支是死重 + 误报面）。
7. **`CalendarService`（Core）0 引用 0 覆盖**——死代码，建议删除。
8. **每次同步新建 `HttpClient`+handler 并销毁**：无泄漏但每 5 分钟重握手 TLS；桌面规模可接受，量级增长时改池化。
9. **`EventRepository.GetPendingSyncAsync`** 的 `deleted_at IS NULL` 与 IN 列表含 `DeletedLocal` 互相矛盾（软删行必有 deleted_at）——当前无调用方依赖，属埋雷（Agent 1/2 独立发现并交叉确认）。
10. **过期重复事件行被每次范围查询永久返回**（`BuildInRangeWhere:186` 只判 `start_utc < @end`）——正确性无恙，纯性能面；配合 CQ-005 修复（透传规则）时会放大。

---

## 7. Rejected Hypotheses

| ID | 原假设 | 驳回依据 |
|---|---|---|
| RH-001 | （A3-005/A2-008 原始 P1 版本）"任何对带 UNTIL 事件的推送都会截断 UNTIL 并腐蚀服务器规则" | Agent 4 证明唯一变异入口 QuickAdd 在推送前就把规则置 null/预设（CQ-001 路径），UNTIL 到不了 `Serialize`；Agent 5 的 R2 探针绕过了 QuickAdd 映射，机制有效但当时不可达。降级保留为 CQ-007（latent），并与 CQ-001 修复强绑定。 |
| RH-002 | （Agent 5 R1 附带论断）"编辑后本地周视图仍显示旧系列（raw 保留 RRULE）" | 主 Agent 复核 `BuildInRangeWhere:185`：显示按**非规范化 `recurrence_rule` 列**分支，该列已被置 NULL → 本地同样只显示单次。附带论断删除，核心结论（PUT 销毁服务端规则）不受影响。 |
| RH-003 | （A1-002 原始表述）"FullResync 竞态导致**永久**空日历" | Agent 4：再次点击全量重同步即恢复（第二次清库会清掉复活的 token）；服务器变更部分自愈。P1 → P2（CQ-002）。 |
| RH-004 | （A2-001 子断言）"UNTIL 被丢后系列变成无限" | None 路径把规则整个清空成单次事件；只有用户再主动选预设才会得到无界规则。表述已修正并入 CQ-001。 |
| RH-005 | （A3-013）"SECONDLY/MINUTELY 会产生 2000 次实例洪水" | 探针直连展开器；生产路径中 `RuleToRrule`（IcalMapper.cs:252-259）早已把这些频率降为 null（CQ-005），展开器根本不会被调用。降为观察项（上限保护本身存在且有效）。 |
| RH-006 | （A2-005 原始表述）"瞬时网络错误回落 ±180 天时间窗查询（丢失删除检测）" | `SupportsSyncToken=true` 时回落的是**空 token 的 sync-collection**（全量重拉）而非 time-range；真正缺口是全量路径不对账删除。已重写为 CQ-009。 |
| RH-007 | （A2-002 位置归因）"IcalMapper 只映射 Events[0]" | 位置错误：mapper 映射全部事件，丢弃点在 `SyncEngine.ApplyRemoteAsync:209`。行为属实，位置已更正（CQ-004）。 |
| RH-008 | "带 VTIMEZONE 的前缀 TZID 资源应能解析"（对 CQ-006 的潜在驳回） | Agent 4 对照探测：带完整 VTIMEZONE 仍失败——`FindSystemTimeZoneById` 不读资源内时区定义。驳回不成立，CQ-006 维持。 |

---

## 8. Inconclusive Investigations

1. **PUT 响应 ETag 与 multistatus getetag 的引号形态差异**（Agent 2 提出）：CalDesk 按序数精确比较（`StringComparison.Ordinal`）；不同服务器引号策略不一致时可能导致重复拉取或漏更新。无真实服务器可验证，留待 live 测试。
2. **CQ-003 的现实发生率**：机制确定但窗口是单次 PUT 往返 ×同一事件二次编辑，无法从代码估频。假 client 注入实验可量化（已在建议测试中给出骨架）。
3. **`MultistatusReader` 全程 `LocalName` 匹配**（无命名空间校验，Agent 3 探测：换命名空间的 XML 也被接受；空 href 被接受）：宽松设计在真实 DAV 生态里可能是特性而非缺陷，无法在无真机下定论。
4. **`SyncCompleted` 不在 RunAsync 异常路径触发**（SyncRunner.cs:208 仅在 try 内）：失败的同步后视图要等下个 5 分钟 tick 才刷新。影响极小，未升级为 finding。
5. **无效 RRULE 的生产可见性**（A3-015：直连展开器时 0 实例）：生产中需绕过 `RuleToRrule` 白名单才可达，路径未打通，观察项。

---

## 9. Tests / Build / Tooling Results

见 §4 表。要点：构建零警告（`-warnaserror` 复核）；170/170 ×3 次无 flake；覆盖率 CalDav 59.8/50.7 为最大洼地（`SyncEngine.CopyEventFields`、`IcalMapper.Serialize` 回落分支、`CalDavClient` REPORT/PUT/DELETE 路径 0 覆盖——恰是 P1/P2 findings 所在）；`dotnet format --verify-no-changes` 122 处空白（93 在 App.xaml.cs）——只读确认未改文件。测试真实性：170 个中仅 2 个自述的接线性弱测试，其余断言真实硬编码期望；历史时区三 bug 均有回归锁（TZID→00:00Z、FLOATING→02:00Z、UTC 双转换不变）；存储测试用真实临时文件 SQLite。

---

## 10. Coverage and Blind Spots

- **App 程序集 0% 结构性盲区**（测试工程不引用）：QuickAdd 编辑语义、SyncRunner 状态机/FullResync、ReminderService、SettingsService——本审计 14 个 findings 中 6 个位于此。`SyncRunner`/`QuickAddViewModel` 实际可被控制台工程引用执行（Agent 5 已证明），并非不可测，只是未被纳入。
- **CalDav 59.8%**：`CalendarService`/`RequestBodies` 0%，`IcalMapper` 55%（Serialize 回落、TZID/floating BuildDateTime 分支未覆盖），`SyncEngine` 58.2%（冲突矩阵、删除标记、412、推送路径未覆盖）。
- **迁移**：仅 0→1 幂等有测，未来迁移无回归护栏。
- live 集成测试的空跑（CQ-013）使"服务器互通"维度实际为零。

---

## 11. Prioritized Remediation Plan

| 优先级 | 动作 | 涉及 |
|---|---|---|
| 1 | **CQ-001 + CQ-007 联合修复**：QuickAdd 对非预设规则显示"自定义（保留）"且保存不触碰规则；`EditInPlace` 规则未变时保留原 RRULE 文本；`RecurrencePattern` 支持 DATE-TIME UNTIL。附回归测试（raw 往返保真） | P1，消除唯一数据破坏路径；**必须同批**否则激活 UNTIL 截断 |
| 2 | **CQ-002**：FullResync 先取同步权（等待在飞 run）再清库；世代号防护 token 回写。用 `.work/agent-5` 的假服务器骨架固化回归测试 | P2，低概率高困惑 |
| 3 | **CQ-003**：PushOneAsync 回写前重读比较 `UpdatedAt`，dirty 则保留 op | P2，消除静默丢编辑 |
| 4 | **CQ-013 + 测试补盲**：集成测试改 SkippableFact；把 Agent 5 证明可行的 App 层测试工程（SyncRunner/QuickAdd）纳入仓库；为 IcalMapper.Serialize/EditInPlace、SyncEngine 冲突矩阵补测 | 抬升回归基线 |
| 5 | **CQ-004/005/006/008/009/014 打包**（同步鲁棒性批次）：RECURRENCE-ID 至少减重复显示 + UI 标记；规则透传未知频率；TZID 前缀剥离；无 body 条目按 G5-21 处理；网络异常传播 + 全量重拉删除对账；无 ETag 删除失败化 | P3 批量 |
| 6 | **CQ-010/011/012**：OnExit 判空、Reminder 重入门、settings 原子写 | 独立小修 |
| 7 | 观察项 5/6/7/9（死配置、消息嗅探、死代码、矛盾谓词）随手清理 | 卫生 |

---

## 12. Audit Integrity

- **最终 `git status --short`**：见下方执行记录——除本报告与 `.work/` 审计产物（未跟踪文件，均为新增，无已跟踪文件改动）外工作区干净；`tests/CalDesk.Tests/TestResults/`（覆盖率运行的未跟踪产物，**.gitignore 未覆盖**，已随清理删除并建议补 ignore）。
- **生产代码**：零修改。全部实验在 `.work/<RUN>/agent-*/` 的独立工程中进行（引用 src 工程但只读）；`dotnet format` 等工具均以 verify/只读模式运行。
- **Git**：仅读操作（log/blame/show/diff/status），无 commit/stash/checkout/reset/push。
- **临时产物**：`docs/automatic/.work/20260824-2238_deep-code-quality/` 保留（各 agent 的 findings/探针源码/输出，报告证据可复核）；其内 `bin/`/`obj/` 由既有 gitignore 规则覆盖。若需彻底清理可直接删除该目录，不影响报告自洽（关键证据已嵌入本文）。
- **验证限制**：无真实 CalDAV 服务器——服务器侧断言（ETag 形态、删除通知、无 body 响应）基于代码证明 + 本地回环假服务器，未对真机复核。

---

*Report generated by unattended deep-code-quality audit · RUN_ID 20260824-2238 · main agent + agents 1-5*
