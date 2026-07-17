# CalDesk — 构建与发布

> 目标机已安装 .NET 10 桌面运行时 → 采用 **framework-dependent 单文件** 发布（不打包运行时）。

## 一、构建机要求（Windows）
- Windows 10 21H2 / 11
- **.NET 10 SDK**（构建用）
- Git（克隆 `https://github.com/mfreceiver/caldesk.git`）

> WPF 只能在 Windows 上编译；Core / CalDav / Storage / Tests 已在 Linux 验证（9 项单测全部通过，含对 Radicale 的实时端到端同步测试）。

## 二、目标机要求（运行）
- **.NET 10 Desktop Runtime**（已确认你的机器有）
- Windows 10 21H2 / 11

## 三、构建步骤（在 Windows 上）
```powershell
git clone https://github.com/mfreceiver/caldesk.git
cd caldesk

# (可选) 跑一遍库的单元测试（不含 WPF）
dotnet test tests/CalDesk.Tests

# 发布：framework-dependent 单文件 EXE（产物 -> release\CalDesk.exe）
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Version 0.1.0
# 见 scripts/publish.ps1（single-file + 去 pdb；release/ 已 gitignore）
```

产物在：
```
src/CalDesk.App/bin/Release/<tfm>/win-x64/publish/CalDesk.exe
```
该目录整体拷贝即「便携版」（含 CalDesk.exe 与必要的本地依赖；运行需目标机已装 .NET 10 桌面运行时）。

## 四、首次运行配置
1. 启动 `CalDesk.exe`（首次窗口为空属正常，尚未连接账户）。
2. 托盘图标右键 → **设置**。
3. 填入：
   - 服务器：`https://your-caldav-server.example/`（或服务器根地址，会自动发现）
   - 用户名：`<your-caldav-user>`
   - 密码：你的 CalDAV 密码（仅存入 Windows 凭据管理器，不落盘）
4. 点 **连接并保存** → 自动发现并拉取 `工作 / 个人 / 中国节假日` 日历，开始同步。
5. 之后每 5 分钟自动同步；托盘「立即同步」可手动触发。

## 五、数据与凭据位置
- 本地缓存（SQLite）：`%AppData%\CalDesk\caldesk.db`
- 设置：`%AppData%\CalDesk\settings.json`
- 密码：Windows 凭据管理器（目标名 `CalDesk:account:<id>`），**绝不写入 DB / 配置 / 注册表明文**

## 六、关于本次产出的说明（重要）
WPF 桌面外壳（`CalDesk.App`）是在 Linux 上编写的（Linux 无法编译 WPF），遵循标准模式与 Win32 互操作约定。**首次在 Windows 编译时若出现编译错误，请把错误贴回，我会就地修复**——核心数据通路（CalDAV / 存储 / 同步 / 重复展开）已通过对 Radicale 的实时测试验证，逻辑层是稳的。

## 七、MVP 范围与已知限制
- ✅ 多日历、周视图、月份角标今日、增删改、全天、基础重复显示、5 分钟同步、托盘、开机启动、无边框透明窗口（普通/桌面/置顶）、基本提醒（尊重远端 VALARM + 全局默认）。
- ⏸/❌ 暂缓/放弃：复杂重复编辑器、本次及以后、多账户合并、WinUI Widgets、自然语言解析、WorkerW 严格嵌入（实验）、VTODO。
- 同步冲突：本地与远端同时改动 → 本地标记「冲突」并在状态栏提示，**绝不静默覆盖**（G4-16）。
- 初始全量同步用 `sync-collection` 拉取（已确认 Radicale 支持），之后按 `sync-token` 增量并感知删除。
