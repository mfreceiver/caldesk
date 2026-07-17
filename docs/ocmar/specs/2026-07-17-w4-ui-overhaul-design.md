# W4 — UI 总体重构设计（resize / DPI / 字号 / 周·月视图 / 日历管理 / QuickAdd）

- 日期：2026-07-17
- 范围：10 项 UI/UX 需求，一次设计、统一实现
- 状态：待用户审阅
- 适用对象：个人单用户 WPF CalDAV 客户端，**+08 北京时间，无 DST**

---

## 0. 已确认决策（来自澄清问答）

| 议题 | 决策 |
|---|---|
| 推进方式 | **一份完整 spec → 统一实现**（实现阶段仍由 fixer-grok 分批 + groker/cgpter 门控，但设计一次性定稿） |
| DPI 症状 | **换屏（不同 DPI 显示器）后窗体/文字尺寸错乱** → 运行期 DPI 变化处理问题，非 manifest 缺失（manifest 已是 PerMonitorV2） |
| 字号 | **字号资源重构**：硬编码 FontSize → DynamicResource token，S/M/L 切换资源字典，只缩字号不缩布局 |

## 0.1 现状速记（已验证，避免重复造轮子）

- 数据层已就绪：`CalendarInfo` 有 `IsSelected`+`Color`；`MonthDay` 有 `OccurrenceCount`+`Preview(limit=3)`；Color 已流入 `OccurrenceItem.Color`/`MonthOccurrence.Color`。
- 周窗 `WeekWindow`：`WindowStyle=None`+`AllowsTransparency=True`+`ResizeMode=CanResize`+`ResizeGrip`+`MinWidth=280/MinHeight=420`；事件 TextBlock 已 `TextWrapping=Wrap`；外层 `ScrollViewer` 已存在。
- 月窗 `MonthWindow`：`ResizeMode=NoResize`；只有月历网格，无选中日详情。
- `app.manifest`：`dpiAwareness=PerMonitorV2,PerMonitor,System`、`dpiAware=True/PM`。
- `Windowing/WindowExtensions.cs` + `Win32.cs`：有 gadget 样式（TOOLWINDOW/NOACTIVATE/clickthrough/topmost），**无边缘缩放 WM_NCHITTEST 管线**。
- `AppSettings`（JSON, `%AppData%\CalDesk\settings.json`）：有 WindowMode/Opacity/LockedPosition/Window*位置/DefaultDuration/...，无字号、无月视图拆分比例。
- QuickAdd：标准 OS 对话框外观（有系统标题栏/边框），`SizeToContent=Height`，与无边框主界面割裂。

---

## 1. 共享基座（先于各视图实现）

### 1.1 字号 token 体系（item 10）

**目标：** 硬编码 FontSize 全部替换为 DynamicResource token；S/M/L 切换即时生效（不重启）。

**token 集（语义命名，覆盖现有所有字号）：**

| token | 用途 | S(默认=现状) | M | L |
|---|---|---|---|---|
| `FontTiny` | 9px 标记（"今天"小字、月格 +n） | 9 | 10 | 12 |
| `FontMicro` | 10px（月格事件预览、月周首行） | 10 | 11 | 13 |
| `FontSmall` | 11px（状态栏、时间标签） | 11 | 12 | 14 |
| `FontBody` | 12px（事件正文） | 12 | 13 | 15 |
| `FontBase` | 13px（窗基线） | 13 | 14 | 16 |
| `FontHeader` | 15px（标题日期） | 15 | 16 | 18 |
| `FontTitle` | 17px（＋号、大标题） | 17 | 18 | 20 |

**实现：**
- 新增 `src/CalDesk.App/Themes/FontSizes.S.xaml` / `.M.xaml` / `.L.xaml` 三份 ResourceDictionary，各含上述 7 个 `<sys:Double x:Key="FontBody">` 资源。
- `App.xaml` 默认合并 `FontSizes.S.xaml`（MergedDictionaries）。
- 替换 `WeekWindow.xaml` / `MonthWindow.xaml` / `QuickAddWindow.xaml` / `SettingsWindow.xaml`(及日后新增) 中所有 `FontSize="数字"` → `FontSize="{DynamicResource FontBody}"` 等。
- `AppSettings` 新增 `public FontSizeLevel FontSize { get; set; } = FontSizeLevel.Small;`（enum `Small/Medium/Large`）。
- 设置窗提供 S/M/L 三选；切换时由 SettingsService/App 调 `ApplyFontSize(level)`：从 MergedDictionaries 移除旧字典、合并新字典 → 所有 DynamicResource 自动刷新。
- **DynamicResource 而非 StaticResource**：保证运行期切换即时生效。

**验收：** 切换 S/M/L 三档，全部文字立即按表缩放；重启后保持选择；M/L 下窗体不溢出（配合 1.2 的最小尺寸）。

### 1.2 无边框可缩放 + 最小尺寸（item 1 缩放部分）

**目标：** 周/月两窗均可四边+四角拖拽缩放（不止右下小 grip），并设合理最小尺寸。

**实现：**
- 新增 `Windowing/BorderlessResize.cs`：`Attach(Window w, double resizeBorderThickness=6)`，通过 `HwndSource.AddHook` 拦截 `WM_NCHITTEST (0x0084)`，依据鼠标相对客户区位置返回 `HTLEFT/HTRIGHT/HTTOP/HTBOTTOM/HTTOPLEFT/...`（厚度内），其余交默认。复用 `Win32` 已有 P/Invoke 风格。
- 两窗 XAML：`ResizeMode=CanResize`（月窗由 NoResize 改）；保留现有 `ResizeGrip`（右下）作为显式抓手。
- 最小尺寸（DIU，已考虑 L 档不溢出）：
  - 周：`MinWidth=320 MinHeight=500`
  - 月：`MinWidth=380 MinHeight=520`
- `WindowExtensions` 不动（仅加新文件）。

**验收：** 两窗任意边/角可拖拽缩放；不能小于最小尺寸；缩放过程无闪烁/撕裂（`UseLayoutRounding=True`+`SnapsToDevicePixels=True`）。

### 1.3 DPI 稳健（item 9，症状：换屏后尺寸错乱）

**根因假设（诊断优先，勿盲目 hook）：** PerMonitorV2 下 WPF 通常**自动**响应 `WM_DPICHANGED` 缩放字体/布局并应用建议矩形。本应用"换屏错乱"最可能源自 **持久化的位置/尺寸按物理像素或错误基准还原**，或 `AllowsTransparency=True`+`WS_EX_NOACTIVATE` 干扰自动处理。故先做"持久化正确 + 清晰度"，再按实测决定是否显式接管 DPI 消息。

**实现（新增 `Windowing/DpiHandling.cs`，分层落地）：**
1. **持久化正确（首要，必做）：** `AppSettings` 新增 `WindowWidth/WindowHeight`（周窗）与月窗 `MonthWindowWidth/Height/MonthSplitRatio`；`LocationChanged/SizeChanged` 存 **DIU**（WPF 的 Width/Height/Left/Top 本就是 DIU，跨屏一致）；恢复时按 DIU 还原到原 `ScreenDeviceName` 所在屏（找不到则主屏）。修正任何按物理像素存取的路径。
2. **清晰度（必做）：** 窗根 Border 设 `TextOptions.TextFormattingMode=Display`、`TextRenderingMode=Auto`、`UseLayoutRounding=True`、`SnapsToDevicePixels=True`。
3. **DPI 消息接管（条件，按实测）：** 仅当 Windows 双屏测试确认自动处理对透明窗失效（尺寸仍错乱）时，才在 `Attach(Window w)` 用 `HwndSource.AddHook` 拦截 `WM_DPICHANGED (0x02E0)`，将 `lParam.SuggestedRect` 换算为 DIU 后设 `Left/Top/Width/Height` 并 `return IntPtr(0)`。**若自动处理正常则不 hook**（避免与 WPF 双重应用）。`OnSourceInitialized` 调 `DpiHandling.Attach` + `BorderlessResize.Attach`。

**验收（Windows 桌面）：** 在 100% 与 150%（或 125%）DPI 双屏间拖动窗口，文字保持清晰（不糊）、窗体尺寸自适应（不出现裁剪/巨型/微型）；重启后尺寸正常。

### 1.4 日历颜色/可见性持久化（item 6 基座）

**目标：** 用户可改 `CalendarInfo.IsSelected`（隐藏）与 `Color`（配色），并持久化。

**实现：**
- 复用现有 `ICalendarRepository`；若缺 `UpdateAsync(CalendarInfo)` 或批量更新，则新增（实现时确认 schema：`IsSelected`/`Color` 列已存在，无需迁移）。
- 服务端下发的 `Color` 作为**初始值**；用户覆盖后以用户值为准并持久化。下次发现（discovery）不覆盖用户已设的 `Color`/`IsSelected`（仅回填缺失）。
- 12 色板（见 §4）作为用户可选预设；存储仍为 `#rrggbb` 字符串。

---

## 2. 周视图（items 1 头部, 2, 3-周）

### 2.1 标题栏底色 + 间距（item 1 头部）
- header Grid 外包一层 `Border`：`Background` 用资源 `HeaderBrushWeek=#FFF1F3F9`（冷色调）、`CornerRadius=8`、`Padding=10,6`；与正文 `Margin=0,0,0,10`。
- 月窗用 `HeaderBrushMonth=#FFF6F1F4`（暖色调）以视觉区分。
- 两种底色定义为 `App.xaml` 资源，便于统一调。

### 2.2 今天移左上 + 标题居中 + 每日＋（item 2）
- header 三栏改为：`[今天(左)] [★ ‹ 日期 › ★(中,居中)] [周|月(右)]`。
  - 左栏：`今天` 按钮（原在底部右），`GhostButton` 风格。
  - 中栏：`‹ WeekHeader ›`，`HorizontalAlignment=Center`；三栏 Grid 的中栏 `Width=*` 保证居中。
  - 右栏：周/月分段开关（不变）。
- **移除**原 header 左上全局 `＋`（它下沉到每日行）。
- 每日行模板：日期头行改为 `[周一 13  今天] ............ [＋]`：
  - 左：`Header` TextBlock + 条件 `今天` 小字。
  - 右：`＋` 按钮，`Command=AddNewCommand`，`CommandParameter={Binding Date}`（点击 → QuickAdd，`StartDate=该日`）。
  - 保留现有双击新增 `OnDayDoubleAdd`。

### 2.3 事件换行/滚动/行底色（item 3-周）
- 换行：已 `TextWrapping=Wrap` ✓；将各 TextBlock `FontSize` 换 token。
- 滚动：外层 `ScrollViewer` 已存在，`VerticalScrollBarVisibility=Auto` 保留；确保 `CanContentScroll` 默认即可（鼠标滚轮已生效）。
- 行底色：每日 `StackPanel` 外包 `Border Background`：
  - 今日：`#143B82F6`（淡蓝）。
  - 其他：`#0D000000`（极淡）/ 周末略深 `#14000000`。
  - `CornerRadius=6`、`Padding=8,6`，行间 `Margin=0,4`。

**验收：** 标题居中；今天在左上；每日行右有 ＋ 且点击带入该日期；事件多时可滚轮+滚动条；日行有底色区分。

---

## 3. 月视图重构（items 4, 5, 8, 3-月, 1-月头部）

### 3.1 上下拆分 + GridSplitter（items 4, 8）
- `MonthWindow` Grid 行改为：
  ```
  Row0 Auto  header（标题栏，§2.1 暖色底）
  Row1 Auto  weekday 周一首行
  Row2 *     上：月历网格（等高 6 行）
  Row3 Auto  GridSplitter（水平）
  Row4 *     下：选中日详情（单日，复用周视图日模板）
  Row5 Auto  status
  ```
- `Row2` 与 `Row4` 默认 `Height` 用 `*` 比例 3:2（上多下少），`GridSplitter` 拖拽改比例；两行各设 `MinHeight`（上≥180、下≥140，DIU）。
- `MonthSplitRatio`（double，上占比）持久化到 `AppSettings`，恢复时设两行 `Height` 的星值。

### 3.2 选中日详情（item 4）
- `MonthViewModel` 新增：
  - `[ObservableProperty] DateOnly? _selectedDate;`
  - `[ObservableProperty] WeekDay? _selectedDay;`（复用 `WeekDay` 模型：AllDay+Timed）
  - 点格子 → `SelectDayCommand(DateOnly)` → 设 SelectedDate + 重建 SelectedDay。
- `CalendarViewService` 新增 `BuildDay(events, calendars, date, tz, today, nowUtc): WeekDay`（单日桶，复用 `BucketByDay`）。
- 底部详情模板：与周视图"单日"一致（all-day + timed 列表、行底色、事件颜色条/点）；**外层包独立 `ScrollViewer`**（该日事件多时可滚）；空态"该日无事项"。每日行右侧 ＋ 同周（item 5 末要求）。

### 3.3 月历等高 + 最少3事件高 + +n 溢出（item 5）
- 月历容器：`Grid` 6 行（每行 `Height="*"`）× 每行内 `UniformGrid Columns=7`；保证等高。当前是 `ItemsControl` 套 `UniformGrid`，改为 `ItemsControl.Weeks` → 每项一行，外层用 6 等高行承载（或直接 `UniformGrid Rows=6`，行高等比）。
- 每格 `MinHeight` = 容纳 3 个预览行（约 `3×22px` + 日期头），资源 `MonthCellMinHeight=92`（DIU，随字号 token 适度联动——简单起见取固定 DIU，L 档由滚动兜底）。
- **溢出规则（改模型 + view service）：**
  - `MonthDay` 新增 `public int OverflowCount { get; init; }`（默认 0）。
  - `CalendarViewService.BuildMonth`：
    - `count = ordered.Count`
    - `count <= 3` → `Preview = ordered.Take(3)`，`OverflowCount = 0`
    - `count > 3` → `Preview = ordered.Take(2)`，`OverflowCount = count - 2`
  - 模板：渲染 `Preview`；若 `OverflowCount>0`，第 3 行显示 `(+{OverflowCount})`（`FontTiny`、淡色、可点 → 触发 `SelectDayCommand` 展开底部详情）。
- 格子点击（空白处）→ `SelectDayCommand`；点具体事件 → `Edit`。

### 3.4 月历格子/标题底色（item 3-月）
- 格子 `Border Background`：
  - 当月内：`#0A000000`；溢出月：`#00000000` + Opacity 0.35（保留现逻辑）。
  - 今日格：`#143B82F6`。
  - 周末：日期数字用 `#B91C1C`。
- weekday 首行 + 月标题：套 `HeaderBrushMonth` 或淡底色条 `#10000000`。

**验收：** 月历 6 行等高；每格至少容 3 事件高；≤3 全显、>3 显示 2 条 +(+n)；点格子/溢出行 → 底部显示该日全部事件；上下可拖拽改比例并记忆；格/标题有底色。

---

## 4. 日历管理（item 6）

- 设置窗 CalDAV 区新增"日历管理"面板：
  - 列出所有已发现日历（`DisplayName` + 所属账户）。
  - 每行：可见性 `CheckBox`（绑定 `IsSelected`）+ 12 色选择器（小色块网格，点击选中；选中态加勾/描边）。
  - "重置为服务端颜色"按钮（可选）。
- 保存：调 `ICalendarRepository` 更新 `IsSelected`/`Color`；触发一次 `SyncRunner`/视图刷新（或下次加载生效）。
- 颜色用途：周/月事件的颜色条/点（替换周视图现硬编码 `#3B82F6` → 绑定 `Color`，空则回退默认蓝）；月格事件已有色点。

### 12 色板（从用户给的 ColorBrewer 20 色中精选：剔除灰 `#d9d9d9`、过淡 `#ffffb3/#fccde5/#a6cee3/#b2df8a/#cab2d6/#bebada/#fdbf6f` 及近重，保留高对比、色相均匀的 12 色）
| # | 名称 | hex | | # | 名称 | hex |
|---|---|---|---|---|---|---|
| 1 | 红 | `#e31a1c` | | 7 | 绿 | `#33a02c` |
| 2 | 珊瑚 | `#fb8072` | | 8 | 薄荷 | `#8dd3c7` |
| 3 | 粉 | `#fb9a99` | | 9 | 蓝 | `#1f78b4` |
| 4 | 橙 | `#ff7f00` | | 10 | 钢蓝 | `#80b1d3` |
| 5 | 琥珀 | `#fdb462` | | 11 | 紫 | `#6a3d9a` |
| 6 | 黄绿 | `#b3de69` | | 12 | 紫红 | `#bc80bd` |

**验收：** 可隐藏日历（隐藏后其事件不在周/月出现）；可为每日历配色并持久化；颜色反映到事件指示。

---

## 5. QuickAdd 换肤匹配主界面（item 7）

- `QuickAddWindow`：`WindowStyle=None`+`AllowsTransparency=True`+`Background=Transparent`；根 `Border` `Background=#F4FFFFFF`、`CornerRadius=10`、`Padding=16`、`DropShadowEffect`（同主窗）。
- 顶部自定义标题条：拖拽区（`MouseLeftButtonDown` 拖动）+ 右上 `×` 关闭（`IsCancel`）；保留 `SizeToContent=Height`。
- 统一控件外观：`TextBox/DatePicker/ComboBox/CheckBox/Button` 用一致 token 字号 + 圆角/淡边框样式（`Styles/AppControls.xaml` 新建，集中放可复用样式）。
- 挂 §1.2 缩放？QuickAdd 用 `SizeToContent=Height`，给 `ResizeMode=NoResize`（保持简洁）；DPI 清晰度（§1.3 TextOptions）照挂。
- ESC 取消、Enter 保存继续生效。

**验收：** QuickAdd 外观与主窗同源（无边框圆角卡片、同字体/底色/阴影）；功能不回归。

---

## 6. 模型 / 设置 / 文件改动汇总

**Core 模型（新增/改字段）：**
- `MonthDay`：+ `OverflowCount`（int，init）。
- `CalendarViewService`：`BuildMonth` 溢出规则改；新增 `BuildDay(...)`。
- `CalendarInfo`：无结构变化（`IsSelected`/`Color` 已有）；确保仓储可更新。

**App 设置（`AppSettings` 新增）：**
- `FontSizeLevel FontSize`（enum, default Small）
- `double WindowWidth/WindowHeight`（周窗 DIU）
- `double MonthWindowWidth/MonthWindowHeight/MonthSplitRatio`

**新增文件：**
- `src/CalDesk.App/Themes/FontSizes.S.xaml` / `.M.xaml` / `.L.xaml`
- `src/CalDesk.App/Themes/AppControls.xaml`（统一控件样式 + HeaderBrushWeek/Month 等）
- `src/CalDesk.App/Windowing/BorderlessResize.cs`
- `src/CalDesk.App/Windowing/DpiHandling.cs`

**主要改文件：**
- `App.xaml`（合并字号字典、共享资源）、`App.xaml.cs`（ApplyFontSize、窗尺寸持久化/恢复接线）
- `WeekWindow.xaml(.cs)`、`MonthWindow.xaml(.cs)`、`QuickAddWindow.xaml(.cs)`、`SettingsWindow.xaml(.cs)`
- `WeekViewModel.cs`（每日 AddNew 已支持 date 参数；事件颜色绑定）、`MonthViewModel.cs`（SelectedDate/SelectedDay/SelectDayCommand/BuildDay）
- `SettingsViewModel.cs`（日历管理：加载/保存日历列表 + 颜色/可见性；字号切换）
- 仓储（如缺 UpdateAsync 则补）

## 7. 备选方案（已权衡）
- **字号**：LayoutTransform 整体缩放（最快但连布局一起放大、可能糊）→ 已选**资源重构**（清晰、只缩字号）。
- **缩放**：仅保留 ResizeGrip → 否，加 **WM_NCHITTEST 全边**（体验完整）。
- **DPI**：放弃 AllowsTransparency 改硬裁圆角（更大改）→ 否，保留透明 + **显式处理 WM_DPICHANGED 建议矩形 + 清晰度选项**。
- **月视图拆分**：弹独立日窗 → 否，**同窗 GridSplitter 上下区**（符合 item 4/8）。

## 8. 验证计划
- **Linux**：`dotnet build CalDesk.slnx`（App 须 0 error）+ `dotnet test tests/CalDesk.Tests`（112 须保持绿；为 OverflowCount/BuildDay/字号 token 增补单测：`CalendarViewServiceTests` 覆盖 +n 规则与单日构建）。
- **门控**：groker + cgpter ≥9.5（注明 +08/无 DST、本批为 WPF UI）。
- **Windows 桌面（你验收）**：见 §各项验收 + 双屏 DPI 拖拽、S/M/L 切换、月视图拆分拖拽、日历隐藏/配色。

## 9. 实现顺序（统一实现内的建议批次）
1. 基座：字号字典+token 替换 → BorderlessResize → DpiHandling → AppSettings 扩展 + 持久化接线。
2. 周：标题栏底色/今天左上/居中/每日＋/行底色/事件颜色绑定。
3. 月：上下拆分+GridSplitter → 等高+溢出(+n)+模型改动 → 选中日详情(BuildDay) → 格/标题底色。
4. 日历管理：设置面板 + 仓储更新 + 12 色板。
5. QuickAdd 换肤（AppControls.xaml 复用）。
6. 全量 build+test + 门控 + 发布。
