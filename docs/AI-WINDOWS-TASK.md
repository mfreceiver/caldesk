# CalDesk — Windows 端构建/修复任务（AI 助手用）

> 把这份文件直接喂给你的 VS Code AI 插件（或让它读本文件）。它的工作边界、已知事实、验证步骤都在这里。

## 0. 你的角色
你是 CalDesk 项目的 **Windows 端构建/修复助手**。我在 Linux 上完成了全部代码：核心库已测试通过（45/45 单测 + 真机 CalDAV 同步全绿），但 **WPF 桌面外壳 `CalDesk.App` 无法在 Linux 编译，从未编译过**。你的任务是在 Windows 上把它**编译通过 → 修掉编译期/运行期错误 → 发布单文件 EXE → 跑起来冒烟验证**。

## 1. 立即目标（按序）
1. `git pull`，HEAD 应为 `20487df` 或更新。
2. `dotnet restore CalDesk.slnx` 还原。
3. 编译整个解决方案，**重点让 `CalDesk.App`（net10.0-windows，UseWPF）编译通过**，迭代直到 0 error。
4. 执行发布命令，产出单文件 EXE。
5. 运行 → 首次配置 → 冒烟验证（见 §6）。

## 2. 环境
- Windows + **.NET 10 SDK**（10.0.302 或更高）。
- 解决方案：`CalDesk.slnx`（**是 `.slnx` 不是 `.sln`**）。
- 项目：`src/CalDesk.Core | CalDav | Storage | App` + `tests/CalDesk.Tests`。
- App 目标 `net10.0-windows10.0.19041.0`，`UseWPF`，`EnableWindowsTargeting=true`。
- 关键 NuGet：CommunityToolkit.Mvvm 8.4.0、H.NotifyIcon.Wpf 2.2.0、Ical.Net 5.2.3、Microsoft.Data.Sqlite + SQLitePCLRaw。

## 3. 不可违反的约束
1. **不要改动核心库**（`src/CalDesk.Core | CalDav | Storage`）。它们在 Linux 上 0 错构建、45/45 单测 + 真机同步全绿，是稳的。修编译错误**只在 `src/CalDesk.App/**` 和 `tests/CalDesk.Tests/**` 内**进行。若某编译错误的根因在库的公共 API → **停下告诉我**，不要擅自改库。
2. **不要回退以下已确认的修复**：
   - H.NotifyIcon.Wpf 2.2.0：xmlns 用 `clr-namespace:H.NotifyIcon;assembly=H.NotifyIcon.Wpf`。托盘 `IconSource` 现在绑定应用 logo（`<BitmapImage UriSource="pack://application:,,,/Assets/icon.png"/>`，由 `misc/icon.svg` 生成），**不是** `GeneratedIcon`/`GeneratedIconSource`，别改回文本生成图标。EXE/窗口图标靠 `<ApplicationIcon>Assets\app.ico</ApplicationIcon>`（多分辨率 16/32/48/256，已含）。`Assets\app.ico` 和 `Assets\icon.png` 都是 `<Resource>`。
   - `Win32` 与 `WindowsCredentialStore` 的类**必须是 `partial`**（`LibraryImport` 源生器要求），别去掉。
   - 托盘显形靠 `TaskbarIcon.ForceCreate(enablesEfficiencyMode: false)`；通知用 **`ShowNotification(title, message)`**（不是 `ShowBalloonTip`）。
3. **CommunityToolkit.Mvvm 用源生器**：`[ObservableProperty] private string _foo;` 会生成 `Foo` 属性；`[RelayCommand] private void X()` 生成 `XCommand`。XAML 绑定这些**生成成员是对的**，别因“找不到手写定义”误判为错误。
4. 保持架构决策：framework-dependent 单文件、local-first、冲突=保留本地并标记、周一起始、密码仅入 Windows 凭据管理器。

## 4. 编译与发布命令
```powershell
dotnet restore CalDesk.slnx
dotnet build CalDesk.slnx -c Debug          # 整体编译通过（重点 CalDesk.App）
dotnet test tests/CalDesk.Tests             # 库单测应全绿，确认没被破坏
dotnet publish src/CalDesk.App/CalDesk.App.csproj `
  -c Release -r win-x64 `
  --self-contained false `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableWindowsTargeting=true
```
产物：`src\CalDesk.App\bin\Release\<tfm>\win-x64\publish\CalDesk.exe`
（运行需目标机已装 .NET 10 桌面运行时。）

## 5. 盲写高风险区（按“最可能出错”排查）
1. **`Views/MonthWindow.xaml`**——全新写的月视图，最可能有错：绑定路径、DataTrigger、`UniformGrid` 当 ItemsPanel、事件签名。
2. **`App.xaml`** 托盘 `TaskbarIcon` + `ContextMenu` 的 XAML 与 `Click=` 处理器签名。
3. **`Views/WeekWindow.xaml`** 绑定与 DataTrigger。
4. **`App.xaml.cs`** 的 `ForceCreate` / `ShowNotification` 调用、周⇄月双窗生命周期。
5. 所有 XAML `Click=` / `MouseLeftButtonDown=` / `SelectionChanged=` / `ValueChanged=` 处理器，**签名必须与 code-behind 完全一致**。
6. `CommandParameter` 传值类型（如 `DateOnly`）给 `RelayCommand<DateOnly?>` 的装箱；`CommandParameter="{x:Null}"` 传 null 的处理。
7. `MonthWindow` 的“今天”徽章：day-number 在 Border 内，背景用 DataTrigger 切蓝。

## 6. 运行与冒烟验证
1. 运行 `CalDesk.exe`（首次窗口为空属正常，尚未连接账户）。
2. 托盘右键 → **设置**：
   - 服务器：`https://your-caldav-server.example/path/`
   - 用户名：`<your-caldav-user>`　密码：`<your-caldav-password>`
   - 点 **连接并保存** → 应自动发现并拉取「工作 / 个人 / 中国节假日」三个日历。
3. 逐项验证（OK / 失败）：
   - 周视图显示事件、进行中事件高亮、过去事件隐藏。
   - 点 **＋** 新增事项、双击空白日期新增。
   - 表头 **月** → 切到月视图；月视图 **周** → 切回。
   - 月视图：跨月补齐格灰显、今日蓝徽、点单元格内事项进入编辑、双击空白格新增。
   - 托盘 **立即同步**、状态栏显示“已同步 · 拉 N · 推 M”。
    - 关窗 → 收到托盘；托盘菜单：立即同步 / 设置 / 关于 / 退出（托盘双击可重新显示窗口）。
4. 运行期排查：留意 VS “输出”窗口的 `BindingExpression` 错误（运行期绑定失败，编译期不报）。
5. 数据位置：`%AppData%\CalDesk\caldesk.db` + `settings.json`；密码在“Windows 凭据管理器”（目标名 `CalDesk:account:<id>`）。

## 7. 反馈给我（格式）
- **每个编译错误**：`文件:行号` + 错误原文 + 你的修复（贴 diff）。
- 若**根因在核心库**（不在 App）→ 停下，把错误贴回，**不要改库**。
- 编译通过后：发布是否成功、EXE 大小、§6 每项 OK/失败。
- 任何**运行期异常**（崩溃 / 绑定错误 / 同步失败）的**完整堆栈**。
- 若改了 App 的 XAML/CS，说明改了什么、为什么。

## 8. 已知未做（不要顺手“补”，先问我）
时间类型模型拆分（浮动/全天语义）、RECURRENCE-ID 例外聚合、RDATE/EXDATE 持久化、PUT 后 ETag 回取、ICalendarCodec 抽象、`remote_href` 按日历作用域、连接池复用、重复展开缓存。这些是**有意推迟**的深层重构，不是 bug。
