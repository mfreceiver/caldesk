# CalDesk — 构建与发布

> 目标机已安装 .NET 10 桌面运行时 → 采用 **framework-dependent 单文件** 发布（不打包运行时）。

## 一、构建机要求（Windows）
- Windows 10 21H2 / 11
- **.NET 10 SDK**（构建用）
- Git（克隆 `https://git.vectory.cn:18443/mfreceiver/caldesk.git`）

> ⚠ **开发只走私有 Gitea origin**（上面的地址）；GitHub 上的 `mfreceiver/caldesk` 仅作公开镜像（单提交、历史已清洗、滞后），**勿克隆/勿用于开发**。

> WPF 只能在 Windows 上编译；Core / CalDav / Storage / Tests 在 Linux 验证（离线单测全绿，含对 Radicale 的实时端到端同步测试；当前用例数见 `dotnet test tests/CalDesk.Tests` 输出，随版本增长）。

## 二、目标机要求（运行）
- **.NET 10 Desktop Runtime**（已确认你的机器有）
- Windows 10 21H2 / 11

## 三、构建步骤（在 Windows 上）
```powershell
git clone https://git.vectory.cn:18443/mfreceiver/caldesk.git
cd caldesk

# (可选) 跑一遍库的单元测试（不含 WPF）
dotnet test tests/CalDesk.Tests

# 发布：framework-dependent 单文件 EXE（产物 -> release\CalDesk.exe）
# ① dev/试用构建（默认）：不带 -Version → 版本号自动 = git describe（带 commit 锚点，可追溯、可覆盖升级）
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
# ② 里程碑发版：先在 Linux 跑 ./scripts/release.sh <patch|minor|major> 打 tag 并 push，再用 -Version <tag> 出干净版本号
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Version <tag>
# 见 scripts/publish.ps1（single-file + 去 pdb；release/ 已 gitignore）
```

产物在 `<repo>\release\CalDesk.exe`（~29 MB，single-file、去 pdb、framework-dependent；`release/` 已 gitignore）。运行需目标机已装 **.NET 10 桌面运行时**。

### 三.1 版本号机制（git 派生，单一权威）
- 版本号**不写在任何文件里**，构建期由 `Directory.Version.props` 从 git 派生：`InformationalVersion = git describe --tags --always --dirty`（「关于」对话框显示），`FileVersion = <major>.<minor>.<patch>.<提交数>`（第 4 段 = `git rev-list --count HEAD`，单调递增）。
- **里程碑发版（bump semver）**：Linux 上 `./scripts/release.sh <patch|minor|major>` → `dotnet test` 门禁 → 生成 changelog → 打 annotated tag → `git push origin main <tag>` → Windows 上 `publish.ps1 -Version <tag>` 出干净版本号 EXE。
- **同族小修 / 试用构建**：直接 `git commit` → `publish.ps1`（**不带** `-Version`）→ EXE 自带 `<tag>-<hash>`、`FileVersion` 更高，可追溯、可覆盖升级、无需新 tag。
- ⚠ `-Version` 只在里程碑发版配合真实 tag 用；dev/试用构建一律省略，让版本自动带 commit 锚点。
- 细节见 [`VERSIONING.md`](./VERSIONING.md)；发版/推送流程见 [`RELEASING.md`](./RELEASING.md)。

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
核心库（`CalDesk.Core | CalDav | Storage`）在 Linux 上跨平台开发并通过测试；WPF 桌面外壳（`CalDesk.App`）在 Windows 上迭代（Linux 无法编译 WPF）。若 Windows 端编译出现错误，请把错误贴回，我会就地修复——核心数据通路（CalDAV / 存储 / 同步 / 重复展开）已通过对 Radicale 的实时测试验证，逻辑层是稳的。

## 七、MVP 范围与已知限制
- ✅ 多日历、周视图、月份角标今日、增删改、全天、基础重复显示、5 分钟同步、托盘、开机启动、无边框不透明窗口（普通/桌面/置顶）、基本提醒（尊重远端 VALARM + 全局默认）。
- ⏸/❌ 暂缓/放弃：复杂重复编辑器、本次及以后、多账户合并、WinUI Widgets、自然语言解析、WorkerW 严格嵌入（实验）、VTODO。
- 同步冲突：本地与远端同时改动 → 本地标记「冲突」并在状态栏提示，**绝不静默覆盖**（G4-16）。
- 初始全量同步用 `sync-collection` 拉取（已确认 Radicale 支持），之后按 `sync-token` 增量并感知删除。
