# T13 Ledger — 统一 resize 机制 + resize-session 协调器

> **For agentic workers:** 这是 T13 (Wave 4 W2) 的决策与实现 ledger。涵盖 A4 + B3。
> 关联 plan: `docs/ocmar/plans/2026-07-21-ui-perf-and-fixes.md` Task 13 (L428-445)。
> 关联 spec: `docs/ocmar/specs/2026-07-21-ui-perf-and-fixes.md` §A4 + §B3。

---

## 1. 目标与背景

resize 期间 UI 线程被两件互相干扰的事抢资源：

1. **同步/数据库刷新**（`SyncCompleted` 触发 `_vm.LoadAsync()` + `_monthVm.LoadAsync()`）。
   这些是较重的 DB 查询 + 事件展开，会触发大量 `INotifyPropertyChanged`。
2. **窗口尺寸落盘**（旧 `SizeChanged` handler 每个像素 `c.WindowWidth = _week.Width;` —
   虽然当前代码只更新内存，但每次都触发 `PropertyChanged` 引起绑定回弹，加剧 resize 卡顿）。

T13 的目标：

- 用 **resize session 协调器** 区分"用户在拖动"与"代码/系统调尺寸"。
- 拖动期间：DB 刷新 defer（合并到 resize 结束后一次），尺寸落盘去抖到 200ms idle / resize 结束。
- 避免引入跨线程触碰 WPF 属性的风险（所有路径走 Dispatcher）。

---

## 2. resize session 检测机制

**采用 HwndSource hook 监听 `WM_ENTERSIZEMOVE` / `WM_EXITSIZEMOVE` 边沿。**

为什么不只靠 `SizeChanged` 边沿：

- `SizeChanged` 在 minimize/restore、`Width=x`/`Height=y` 程序赋值、DPI 变化（`WM_DPICHANGED`）、
  WindowChrome/AeroSnap 等都会触发，无法区分"用户在拖边"和"系统在改尺寸"。
- `WM_ENTERSIZEMOVE` / `WM_EXITSIZEMOVE` 由 `DefWindowProc` 在用户开始/结束拖动窗口边角或移动窗口时
  发出，是 resize session 的可靠边沿。

实现：`src/CalDesk.App/Windowing/ResizeSession.cs`

- 构造时 `HwndSource.AddHook(WndProc)`（沿用 `BorderlessResize.cs` / `DpiHandling.cs` 的 hook 风格）。
- `WndProc` 检测两个消息的边沿（`!_isResizing → true` / `true → false`），状态变化时：
  - `WM_ENTERSIZEMOVE`：置 `_isResizing = true`，raise `ResizeStarted`。
  - `WM_EXITSIZEMOVE`：置 `_isResizing = false`、停 timer、`onUpdate + onCommit` 一次最终尺寸、
    raise `ResizeEnded`。
- 每个窗口一个 `ResizeSession` 实例；App 在 `Window.SourceInitialized` event
  （HWND 已存在后）创建并订阅 `ResizeEnded`。
- `IDisposable.Dispose()`：stop timer + `Tick -= OnDebounceTick` + `HwndSource.RemoveHook(WndProc)`
  （try/catch 兜底 HwndSource 已释放的情况）。App `OnExit` 统一 dispose。

**Win32 常量**：`src/CalDesk.App/Windowing/Win32.cs:25-29` 加
`WM_ENTERSIZEMOVE = 0x0231` 和 `WM_EXITSIZEMOVE = 0x0232`。
（沿用 `Win32.cs` 已有的 `LibraryImport` 风格；hook 通过 HwndSource，不需额外 P/Invoke。）

---

## 3. SizeChanged 去抖落盘路径

**resize 期间**：
- `SizeChanged` 每次触发 → `ResizeSession.ScheduleUpdate()` →
  - 调用 `onUpdate(_week.Width, _week.Height)` 同步更新 `AppSettings.WindowWidth/Height`（内存）
  - **不**调 `onCommit`（不写磁盘）
  - `_debounceTimer.Stop(); _debounceTimer.Start()` 重置 200ms 去抖
- 200ms 内继续拖动：timer 持续被 reset，永不 tick。
- 用户停止拖动 200ms：timer tick → 调 `onCommit` 写磁盘（`SettingsService.Save`）。
- 用户松手（`WM_EXITSIZEMOVE`）：立即 `onUpdate + onCommit` 写最终尺寸。

**非 resize 期间**（如 `ApplyWindowSettings` 改宽高、minimize/restore、DPI 变化）：
- `SizeChanged` → `ScheduleUpdate()` → `onUpdate + onCommit` **立即**一次。
- 不启动 debounce timer（避免误延迟一次性事件的保存）。

**最终 OnExit 落盘**（保留旧逻辑）：
- `OnExit` 仍显式 `_week.Width/Height → s.WindowWidth/Height` + `_settingsSvc.Save(s)`，
  作为防御性 final 快照（即使 debounce 还在 200ms 等待中也能落盘）。
- 然后 `Dispose` ResizeSession。

**为什么 onUpdate + onCommit 分离**：
- `onUpdate` 仅是 `c.WindowWidth = _week.Width`（内存赋值，cheap，无 I/O）。
- `onCommit` 才调 `_settingsSvc.Save(c)`（写 `%AppData%\CalDesk\settings.json`）。
- 这让 resize 中**只**在 `dispatcher` 线程上做内存赋值，磁盘 I/O 最多 200ms 一次或 resize 结束一次。

---

## 4. SyncCompleted defer / 合并刷新路径

**旧代码**：
```csharp
_sync.SyncCompleted += (_, _) => _week?.Dispatcher.InvokeAsync(() =>
{
    _ = _vm.LoadAsync();
    if (_monthVm is not null) _ = _monthVm.LoadAsync();
});
```

**新代码**：
```csharp
_sync.SyncCompleted += (_, _) => _week?.Dispatcher.InvokeAsync(HandleSyncCompleted);
```

`HandleSyncCompleted` 决策：
- 如果 `AnyResizing`（week 或 month 任一窗口的 `ResizeSession.IsResizing == true`）：
  - 置 `_pendingSyncRefresh = true`，**不**调 `LoadAsync`。
- 否则：`DoRefreshAfterSync()` 立即触发 week + month 双 LoadAsync。

`OnAnyResizeEnded`（订阅自 `ResizeSession.ResizeEnded`）：
- 如果 `_pendingSyncRefresh == true`：清标志 + `DoRefreshAfterSync()` 合并触发一次。
- 不会丢失最后一次 sync 完成事件；也不会在 resize 中与每像素 SizeChanged 抢 UI 线程。

`DoRefreshAfterSync` 保持原逻辑：
```csharp
private void DoRefreshAfterSync()
{
    _ = _vm.LoadAsync();
    if (_monthVm is not null) _ = _monthVm.LoadAsync();
}
```

**线程安全**：
- `HandleSyncCompleted` 通过 `InvokeAsync` 在 UI 线程触发（沿用旧 `SyncCompleted` 包装）。
- `OnAnyResizeEnded` 在 UI 线程触发（`WM_EXITSIZEMOVE` hook 在 WPF Dispatcher 上跑）。
- `_pendingSyncRefresh` 字段只在 UI 线程访问（bool，无并发问题）。

---

## 5. T13-C5 决策：是否移除 `BorderlessResize.Attach`

**决策：保留。** 不在 T13 内盲删。

### 5.1 现状分析

`WeekWindow.xaml.cs:82` / `MonthWindow.xaml.cs:72` 都调 `BorderlessResize.Attach(this)`。
`BorderlessResize.cs` 通过 `HwndSource.AddHook` 拦截 `WM_NCHITTEST`，自行返回 `HTLEFT`/
`HTRIGHT`/`HTTOPLEFT` 等值让系统处理 resize 拖动。

同时 T12 在 XAML 加了 `WindowChrome.WindowChrome ResizeBorderThickness="6"` — 这本意是
让 WPF 的 `WindowChrome` 接管 resize（性能优化目标）。但 WPF `WindowChrome` 内部也通过
NCHITTEST 处理 resize。

**冲突点**：两个机制都通过 `WM_NCHITTEST` 接管 resize 行为。`BorderlessResize.Attach`
抢先 `AddHook` 并 `handled = true`，所以 `WindowChrome` 的 NCHITTEST 逻辑实际**未生效**。

### 5.2 为什么不"为了统一而盲删"

1. **真机手测已确认流畅**：T12-C5 在 winbuild 上 A/B 验证 "resize 明显流畅" 通过。
   当前 `BorderlessResize.Attach` 抢先 hook 的方案就是被验证的方案。
2. **替代方案未真机验证**：移除 `BorderlessResize.Attach` 让 `WindowChrome` 接管，
   需要在真机重新 A/B 验证 resize 性能 + 边角热区 + DPI 表现。这超出 T13 范围。
3. **风险不对称**：保留是 T12 已验证的现状；删除是未经真机验证的猜测。
4. **任务约束**：plan Task 13 明确写 "不要为了'统一'而盲删"。

### 5.3 后续工作（待 winbuild 真机 A/B）

- 准备两套 XAML 变体（仅 BorderlessResize / 仅 WindowChrome），winbuild 编译后真机
  resize 性能 + 边角热区 + DPI 切换表现对比。
- 决策点：若 `WindowChrome`-only 方案性能相当或更好，则可在后续 wave 移除
  `BorderlessResize.Attach` 调用，保留 `BorderlessResize.cs` 但标记为 no-longer-used。
- 决策点：若 `BorderlessResize`-only（当前）保持优势，则保留现状并考虑进一步清理
  XAML 中冗余的 `WindowChrome.ResizeBorderThickness`。

### 5.4 代码注释更新

`WeekWindow.xaml.cs:78-87` 和 `MonthWindow.xaml.cs:68-76` 已更新注释，明确指向本 ledger
说明决策理由。

---

## 6. Acceptance Criteria 自检

| Criterion | 实现 | 证据 |
|---|---|---|
| **T13-C1** `_isResizing` 标志 + resize 结束后合并刷新逻辑存在 | `ResizeSession._isResizing` (ResizeSession.cs:38) + `OnAnyResizeEnded` (App.xaml.cs:264-269) | grep `_isResizing` 4 处, `OnAnyResizeEnded` 在 App.xaml.cs |
| **T13-C2** `SizeChanged` 去抖落盘（DispatcherTimer）存在 | `ResizeSession._debounceTimer` (ResizeSession.cs:35) `DefaultDebounce=200ms` | grep `DispatcherTimer` 在 ResizeSession.cs |
| **T13-C3** resize 期间 `SyncCompleted` 触发的 LoadAsync 被 defer | `HandleSyncCompleted` + `_pendingSyncRefresh` (App.xaml.cs:274-282) | grep `_pendingSyncRefresh` 3 处, `AnyResizing` 1 处 |
| **T13-C4** winbuild 编译通过 | Linux `dotnet build src/CalDesk.App` 已通过 (0 errors) | winbuild 真机编译仍需在目标机器上跑 |
| **T13-C5** BorderlessResize.Attach 决策 | **保留**（见 §5） | 注释 + 本 ledger |

---

## 7. grep 关键证据（自检）

```text
# App.xaml.cs
37:    private ResizeSession? _weekResize;
38:    private ResizeSession? _monthResize;
43:    private bool _pendingSyncRefresh;
120:    _week.SizeChanged += (_, _) => _weekResize?.ScheduleUpdate();
122:    _week.SourceInitialized += (_, _) => AttachWeekResizeSession();
137:    _month.SizeChanged += (_, _) => _monthResize?.ScheduleUpdate();
138:    _month.SourceInitialized += (_, _) => AttachMonthResizeSession();
264:    private void OnAnyResizeEnded()  # pending 合并触发
274:    private void HandleSyncCompleted()  # SyncCompleted 入口, defer 逻辑
285:    private bool AnyResizing =>
624:    _weekResize?.Dispose();  # OnExit dispose
625:    _monthResize?.Dispose();

# ResizeSession.cs
31:public sealed class ResizeSession : IDisposable
35:    private readonly DispatcherTimer _debounceTimer;
38:    private bool _isResizing;
84:    public bool IsResizing => _isResizing;
96:    public void ScheduleUpdate()
142:        if (msg == Win32.WM_ENTERSIZEMOVE)
150:        else if (msg == Win32.WM_EXITSIZEMOVE)
155:                _isResizing = false;  # 边沿切换
160:        _source.RemoveHook(WndProc);  # Dispose 解绑 hook

# Win32.cs
28:    public const int WM_ENTERSIZEMOVE = 0x0231;
29:    public const int WM_EXITSIZEMOVE = 0x0232;
```

---

## 8. 测试与验证

### 8.1 Linux 单测（已跑）

```bash
export DOTNET_ROOT=$HOME/.dotnet; export PATH="$DOTNET_ROOT:$PATH"
dotnet test tests/CalDesk.Tests --nologo
# Passed!  - Failed:     0, Passed:   168, Skipped:     0, Total:   168
```

> ResizeSession 依赖 WPF `HwndSource` + Windows `WM_ENTERSIZEMOVE/EXITSIZEMOVE` 消息，
> Linux 不可伪造。按 plan 约束"能写的纯逻辑测试才写；WPF HWND 真机行为不伪造
> Linux 单测"，本 task 不新增 tests/ 下文件。

### 8.2 Linux 编译（已跑）

```bash
dotnet build src/CalDesk.App/CalDesk.App.csproj -c Debug --nologo
# Build succeeded.  0 Error(s)
```

> csproj 含 `EnableWindowsTargeting=true`，Linux 上也能完成 XAML + C# 编译。
> Win32 P/Invoke 不会在 Linux 调用，仅做 source-gen 验证。

### 8.3 winbuild 编译（待）

`docs/REMOTE-BUILD.md`：

```bash
ssh winbuild 'cd C:\Dev_Code\caldesk && git -c credential.helper= pull \
  && dotnet build CalDesk.slnx -c Debug \
  && dotnet test tests/CalDesk.Tests'
```

### 8.4 winbuild 真机手测（待）

按 plan T13-C4：

1. **resize 流畅性**：与 T12-C5 baseline 对比，应**不低于** T12 流畅度。
2. **尺寸持久化**：拖动结束后立刻 kill 进程 → 重启后尺寸是否正确恢复。
   （验证 200ms debounce 不会丢失最终尺寸；OnExit 兜底；resize 结束立即 commit。）
3. **SyncCompleted defer**：在窗口正在 resize 时观察 tray "立即同步" → 完成后
   底部 status 文字是否正常出现。resize 中是否不出现 UI 闪烁/卡顿。
4. **T13-C5 A/B**（可选）：将 `BorderlessResize.Attach` 注释掉，对比 resize 表现。

---

## 9. 不在 T13 范围（已确认不动）

- `SettingsViewModel.cs` / `SettingsWindow.xaml` / `MonthViewModel.cs` / `WeekViewModel.cs`：
  业务设置/数据层，未改动。
- `Core/CalDav/Storage`：未改动。
- T10 (锁定位置 bug + 月窗独立位置)：已 committed（`46b5620` 等），未回退。
- T11 + T12：已 committed（`77feba1` 等），未回退。
- T14 (月视图模板瘦身)：未开始，保留后续。

---

## 10. 风险与未决项

1. **多显示器 + WM_ENTERSIZEMOVE 时序**：`WM_ENTERSIZEMOVE` 在跨显示器拖动时是否
   仍可靠发单次边沿？需真机验证。
2. **AeroSnap 行为**：Windows 10/11 的 Snap（半屏/四分之一屏）也走 `WM_ENTERSIZEMOVE`，
   `IsResizing` 期间会包含 Snap 调整。`SyncCompleted` defer 触发在 Snap 结束后才发生 —
   这与"Snap 期间不抢 UI 线程"的目标一致，是符合预期的。
3. **最小化恢复**：`WM_ENTERSIZEMOVE` 不会在 minimize 触发。最小化恢复时 `SizeChanged`
   走"非 resize 路径"（`_isResizing=false`），ScheduleUpdate 立即 commit 一次。
   不影响行为。
4. **多窗口同步 resize**：用户能否同时拖动 week + month？技术上 month 默认 Hide，
   不太可能。但 AnyResizing 逻辑覆盖了"任一窗口在 resize"，对极端情况鲁棒。
