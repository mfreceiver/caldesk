# Spec: UI 性能重构与全面修复（Opaque 迁移 + 审计修复）

- **日期**: 2026-07-21
- **状态**: Draft（待用户 🔑 确认）
- **作者**: orchestrator（基于 3 份 explorer 调研 + 3 份 rev-sgpt-sol 评审）
- **可审计引用**:
  - 调研: `exp-1`（核心管线）、`exp-2`（窗体结构）、`exp-3`（渲染绑定反模式）
  - 评审: `rev-1`（核心）、`rev-2`（窗体+锁定bug）、`rev-3`（渲染绑定）
  - 所有问题项均带 file:line，详见各评审报告（本 spec 摘要引用）

---

## 1. 原始需求与决策演进

**一句话需求**：图形化部分性能极差（拖动 resize 卡顿），希望全部修复完善完成。

**关键技术决策演进**（已与用户多轮确认）：
1. 初评（3 评委一致）：resize 卡顿首要根因 = `AllowsTransparency=True` + `DropShadowEffect`；推荐移除透明迁移不透明 `WindowChrome`。
2. 用户一度声明"半透明是必要的"。
3. 经澄清冲突后，用户**最终决定：纯方案 C — 彻底移除透明**，换取最干净、最流畅的 resize。
4. 用户对范围（全部修复）与验收线无异议。

> **本 spec 的地基决策（D1）即"彻底移除透明"，由用户两次明确确认。后续所有设计以此为准。**

---

## 2. 核心设计决策（已确认）

| ID | 决策 | 说明 |
|---|---|---|
| **D1** | 移除 layered transparency → opaque `WindowChrome` | `AllowsTransparency=False`，WeekWindow/MonthWindow/QuickAddWindow 全迁。resize 走硬件合成路径（评委一致最优解）。 |
| **D2** | 移除根 `DropShadowEffect` | 改用系统/DWM 阴影：Win11 用 `DwmSetWindowAttribute`（系统阴影+圆角）；Win10 用静态预渲染阴影或接受无阴影/薄边框。**不再每帧高斯模糊**。 |
| **D3** | 移除 `Window.Opacity` 与透明度设置 | 取消"整窗半透明"产品特性。移除 `AppSettings.Opacity`、`SettingsWindow` 透明度滑块、`SettingsViewModel.Opacity` 字段、`ApplyWindowSettings`/`ApplyMonthWindowSettings` 里的 `w.Opacity=` 赋值。 |
| **D4** | 保留无边框 + 圆角视觉风格 | 窗口仍无边框、圆角，但**不透明**（桌面不再透过可见）。圆角 Win11 走系统、Win10 走根 Border `CornerRadius`。 |
| **D5** | 统一 resize 机制 | `WindowChrome.ResizeBorderThickness` 为主；评估移除 `BorderlessResize.Attach`（WM_NCHITTEST）和/或 `ResizeGrip`，消除"三轨 resize"。 |
| **D6** | 月窗位置语义独立持久化 | 锁定 bug 修复一并明确：月窗独立保存位置（`MonthWindowLeft/Top`），与已独立的宽高持久化一致。 |

---

## 3. 范围（"全部修复完善" = 6 个 cluster）

### Cluster A — 窗口层（resize 主因根除）【P0】
- **A1** 三个窗口迁移 opaque `WindowChrome`（`AllowsTransparency=False` + `WindowChrome CaptionHeight=0/ResizeBorderThickness`）。源：rev-2 10/10、rev-3 9/10。`WeekWindow.xaml:12`、`MonthWindow.xaml:11`、`QuickAddWindow.xaml:10`。
- **A2** 移除根 `DropShadowEffect`，换系统/DWM 阴影（Win11）或静态阴影（Win10）。源：rev-2 10/10、rev-3 8/10。`WeekWindow.xaml:24-26`、`MonthWindow.xaml:23-25`、`QuickAddWindow.xaml:22-24`。
- **A3** 移除透明度：`AppSettings.Opacity`、`SettingsWindow.xaml:229` 滑块、`SettingsViewModel.cs:53` 字段、`App.xaml.cs:261,303` 赋值。源：D3。
- **A4** 统一 resize 机制（WindowChrome 为主，去三轨）。源：rev-2 §待确认4、rev-3 §待确认5。`WeekWindow.xaml.cs:82`、`BorderlessResize.cs`、`ResizeGrip`。

### Cluster B — 布局（resize 第二开销）【P0/P1】
- **B1** 删除 `WeekDaysPanel.ViewportHeight` DP + `ActualHeight` 绑定（布局反馈环）。源：rev-2 8/10、rev-3 9/10。`WeekWindow.xaml:71`、`WeekDaysPanel.cs:8-17`。**注意（rev-3 纠偏）：不能简单把 `AffectsMeasure` 改 `AffectsArrange`，必须删绑定或重构使 DesiredSize 与 viewport 无关。**
- **B2** 重构 `WeekDaysPanel`：优先 `Grid` 7 行均分（加 `WeekDay.WeekIndex`），或真·均分 Panel（去 `PositiveInfinity`/LINQ/动态 extra）。源：rev-2 P0-2、rev-3 3.2 7/10。`WeekDaysPanel.cs:12-26`。
- **B3** resize-session 协调器：拖动中 defer 同步刷新（`SyncCompleted`→LoadAsync）、去抖 settings 落盘；松手提交。源：rev-2 §三-1（7/10）、rev-3 4.1。`App.xaml.cs:88-95,110-117`。

### Cluster C — 正确性 bug【P0】
- **C1** **锁定位置跳变**（CRITICAL）：加 `_week.LocationChanged`（+`_month.LocationChanged`）镜像到 settings；**移除 `OnExit` 的 `!LockedPosition` 守卫**（锁定=禁拖，≠禁持久化）；加 `_applyingWindowSettings` 抑制标志防重入。源：rev-2 9/10。`App.xaml.cs:107-117,482-490,506-513`、`WeekWindow.xaml.cs:18,27-32`。
- **C2** **月视图网格范围不一致**（35天查询 vs 42天渲染）：统一 `MonthGridRange.For(year,month,rowCount=6)`，Core 与 ViewModel 共用，加边界测试。源：rev-1 8/10（explorer 漏）。`CalendarViewService.cs:75-87`、`MonthViewModel.cs:100-105`。
- **C3** **`SelectDay` 异步竞态**：引入版本号/CTS，旧请求不覆盖新选择。源：rev-1 7/10、rev-2 7/10。`MonthViewModel.cs:137-149`。
- **C4** **DST 转换**：`LocalMidnightToUtc` 改 `TimeZoneInfo.ConvertTimeToUtc`，Core/App 共用一份。源：rev-1 6/10。`CalendarViewService.cs:271-275`、`MonthViewModel.cs:175-179`。

### Cluster D — 核心管线性能【P1，需 profiling 证收益】
- **D-core1** `ExpandedCalendarRange` 一次展开复用（消除月视图双查双展开）。源：rev-1 7/10。`MonthViewModel.cs:110-131`、`CalendarViewService.BuildMonth/BuildDay`。
- **D-core2** `Build*`/`BuildDay` 纯计算 `Task.Run` 后台化（保留 `_navVersion` 校验，`Week=built` 留 UI 线程）。源：rev-3 4.3 6/10。`WeekViewModel.cs:74-84`、`MonthViewModel.cs:110-131,137-149`。
- **D-core3** 循环事件解析缓存：先请求级，再 LRU（key=`Id+ETag/UpdatedAt`，不用可变对象引用；上限+截断诊断）。源：rev-1 7/10、rev-1 §待确认2。`IcalRecurrenceExpander.cs:45-60`。
- **D-core4**（P2 择优）`GetInRangeAsync` 轻量投影：非循环事件不拉 `icalendar_data`，循环事件按 ID 批量补原文。源：rev-1 6/10。`EventRepository.cs:15-19,84-93`。

### Cluster E — WPF 次级优化【P1/P2】
- **E1** `ColorOrAccentConverter` 缓存 hex→Frozen `SolidColorBrush`（`ConcurrentDictionary`）。源：rev-3 1.2 5/10、rev-2 §一-6。`ColorOrAccentConverter.cs:12-25`。
- **E2**（P2）`RelativeSource AncestorType=Window` 命令提到 item VM。源：rev-3 1.3 4/10。`WeekWindow.xaml:114,122,154`、`MonthWindow.xaml:107,126,270,318`。
- **E3** 月视图模板瘦身（合并今日/非今日日期节点为一个 TextBlock+DataTrigger；预计算 `EventAccentBrush`/`Color`）。源：rev-2 §一-6、P1。`MonthWindow.xaml:99-200`。
- **E4** 复查 `SlimScrollViewer` 模板是否存在第二条 viewport/layout 反馈环。源：rev-3 §三-2。`AppControls.xaml:134`。
- **E5**（P2）月视图底部 `LeftEvents/RightEvents` 确认数据截断 vs 视觉截断；若未截断则 ViewModel 截断+OverflowCount。源：rev-3 §待确认6、rev-1 §九。`MonthWindow.xaml.cs:104-105`、`CalendarViewService.BuildDay`。

### Cluster F — 设置/UI 调整【随 A3/C1】
- **F1** 移除透明度 UI（随 A3）。
- **F2** 锁定位置语义明确 + 月窗位置持久化 UI（随 C1/D6）。

---

## 4. 非目标（YAGNI / 防过度设计）

- **不**重写整个 UI 框架或换控件库。
- **不**新增功能（只修复+优化+去透明）。
- **不**追求绝对 60fps（opaque 迁移后预期显著改善，但不设硬性帧率指标）。
- **不**为周视图 7 卡 / 月视图 42 cell 做虚拟化（评委一致：收益<复杂度）。
- **不**做 `CalendarEvent` 不可变快照重构（rev-1 P2 架构债，跨层影响大，本轮不做）。
- **不**做 SQLite 单长连接改造（rev-1 4/10，风险>收益）。
- P2 微优化（Palette Freeze、LINQ→for、SizeChanged debounce 等）按收益择优，不强制全做。

---

## 5. 验收标准

1. **resize 流畅性**：opaque 迁移后，拖动改变窗口大小**跟手、无明显掉帧/卡顿**（主观验收，Windows 真机）。
2. **离线单测**：`dotnet test tests/CalDesk.Tests` 全绿（Linux 可跑核心层）。
3. **新增单测覆盖**：
   - 月视图网格范围一致性（`MonthGridRange` 42 天，含月初周一/月末周日边界）
   - `SelectDay` 竞态（快速切换不串台）
   - 位置持久化（锁定后关闭设置不跳变、退出重启位置正确）
   - DST 边界转换
4. **Windows 真机回归矩阵**（winbuild 编译 + 手动/冒烟）：
   - Win10 / Win11
   - DPI 100% / 125% / 150%、多显示器跨 DPI
   - 窗口模式：Normal / Topmost / Desktop（桌面底层）
   - 锁定位置：开/关、拖动、重启
   - 视觉：无边框、圆角、阴影（Win11 系统 / Win10 静态）
   - 托盘隐藏/恢复、同步、提醒不破坏
5. **视觉验收**：窗口不透明、保留圆角与无边框、阴影观感可接受（Win10/Win11 差异记录）。

---

## 6. 关键约束与风险

- **Linux 编不了 WPF**：核心层改动可在 Linux `dotnet test` 验证；**所有 WPF 改动必须经 winbuild 远程编译 + Windows 真机回归**。ocmar verifier 命令需分两层（见 §8 开放问题）。
- **Win10/Win11 差异**：Win11 有系统圆角+DWM 阴影属性；Win10 无系统圆角、DWM 阴影视觉不同。需分别回归，可能需要平台分支。
- **`WindowChrome` + `WS_EX_NOACTIVATE` + Desktop 模式兼容性**：gadget 样式（不抢焦点、不进 Alt+Tab、HWND_BOTTOM）与 WindowChrome 的交互需真机验证（rev-2/rev-3 均标为不确定项）。
- **不破坏现有行为**：托盘常驻、同步引擎、提醒服务、CalDAV 写侧均不能受影响。
- **`BrushResourceProxy` 不能 Freeze**（需响应动态主题），勿误伤。
- **失败回流预案**：若 opaque 迁移在真机出现严重兼容问题（如 Desktop 模式失效），属方案层风险，回 §1 重新决策（可能退到"折中：默认不透明+保留透明开关"）。

---

## 7. 测试策略

- **核心层（Linux 可跑）**：TDD，先写 `MonthGridRange`/`SelectDay` 竞态/位置持久化/DST 单测，再改实现。`dotnet test tests/CalDesk.Tests`。
- **WPF 层（winbuild + Windows）**：编译通过 + 真机冒烟（resize/锁定/模式切换/DPI/视觉）。无自动化 UI 测试框架，靠手动回归矩阵。
- **回归基线**：改动前先在 winbuild 跑一次当前版本 resize 录屏/感受作基线，改动后对比。

---

## 8. 开放问题（留给 plan / grilling / Phase 4）

1. **ocmar verifier 命令**：WPF 在 Linux 编不了，verifier 不能只跑 `dotnet test`。候选：① Linux 跑核心单测作为 verifier 门控，WPF 编译+冒烟作为独立 winbuild 人工门（记录到 ledger）；② 把 winbuild 编译命令纳入 verifier。**Phase 4 需与用户定。**
2. **slug**：候选 `ui-perf-fixes` / `opaque-resize-refactor`。Phase 4 定。
3. **Win10 阴影方案**：静态预渲染 PNG 9 切 vs 薄边框 vs 无阴影——视觉验收时定。
4. **月窗位置持久化（D6）**：确认走独立持久化（推荐）而非跟随周窗。
5. **D-core 缓存/后台化是否本轮全做**：rev-1/rev-3 建议先 profiling 证收益。本轮可先做 D-core1（一次展开复用，无争议纯收益）+ D-core2（后台化），D-core3/D-core4 视 profiling 结果择优。
6. **resize-session 协调器（B3）**的实现范围：最小化（仅 defer 同步刷新 + 去抖落盘）vs 完整（含 layout 降级）。

---

## 9. 成功定义

全部 P0（Cluster A/B/C）完成并通过 Windows 真机回归 + 核心单测全绿；Cluster D/E 按 §8-5 择优完成；锁定位置 bug 与三个正确性 bug 修复并有测试覆盖；resize 在 Windows 真机主观流畅。最终 verifier `EXIT=0 AND FAILURES=0` 且 final code-review pass → promote。
