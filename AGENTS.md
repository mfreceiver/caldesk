# AGENTS.md — CalDesk 操作指引（AI agent 入口）

CalDesk 是一个 **WPF 桌面 CalDAV 日历客户端**（无边框透明周/月视图、本地优先 SQLite、
多日历同步、托盘常驻、提醒）。核心库（Core/CalDav/Storage）跨平台；WPF 外壳仅 Windows。

> 个人/私有配置（服务器、token、构建机 IP）在 `docs/LOCAL.md`（**gitignored，不进版本库**）。
> 本文件只放通用指引。

## 仓库结构
```
CalDesk.slnx                  解决方案（.slnx，非 .sln）
Directory.Build.props         公共属性（版本在此之下由 git 派生，见下）
Directory.Version.props       git 派生版本（禁止手写 <Version>）
scripts/release.sh            发版打 tag（patch|minor|major）
src/CalDesk.Core/             领域模型 + 服务契约 + CalendarViewService（周/月视图逻辑）
src/CalDesk.CalDav/           CalDAV 客户端 + 同步引擎 + iCalendar 映射/展开
src/CalDesk.Storage/          SQLite（WAL/FK/迁移/索引）+ 仓储
src/CalDesk.App/              WPF（net10.0-windows）：周/月窗、快速新增、设置、托盘、关于
tests/CalDesk.Tests/          单测 + 对真机 CalDAV 的集成测试（env 触发，默认 skip）
docs/                         全部文档（见下方索引）
misc/icon.svg                 应用图标源（已生成 src/CalDesk.App/Assets/app.ico + icon.png）
```

## 构建 & 测试
### Linux（开发机，只能编库 + 跑测试，不能编 WPF）
```bash
export DOTNET_ROOT=$HOME/.dotnet
export PATH="$DOTNET_ROOT:$PATH"
dotnet build CalDesk.slnx -c Debug          # Core/CalDav/Storage/Tests 编过；App(WPF) 在 Linux 编不了
dotnet test tests/CalDesk.Tests --nologo    # 离线单测全绿；live 集成测试需 CALDAV_TEST_* env（见 .env.example）
```
> ⚠ **不要**改核心库（Core/CalDav/Storage）除非必要——它们已 45/45 单测 + 真机同步验证过。改了要重跑测试。

### Windows（WPF 编译 + 发布）
**首选：远程构建机**（见 `docs/REMOTE-BUILD.md`）：
```bash
ssh winbuild 'cd C:\Dev_Code\caldesk && git -c credential.helper= pull && dotnet build CalDesk.slnx -c Debug && dotnet test tests/CalDesk.Tests'
```
发布单文件 EXE（产物到 `<repo>\release\CalDesk.exe`）：
```bash
ssh winbuild 'cd C:\Dev_Code\caldesk && powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Version 0.1.0'
```
> `scripts/publish.ps1`（single-file、去 pdb、framework-dependent）；`release/` 已 gitignore。

## 版本号（git 派生，禁止手写）
版本**不写在任何文件里**，由 `Directory.Version.props` 在构建期从 git 派生：
`InformationalVersion = git describe --tags --always --dirty`。发版用 `scripts/release.sh`。
→ 细节见 [`docs/VERSIONING.md`](./docs/VERSIONING.md)，发版/推送流程见 [`docs/RELEASING.md`](./docs/RELEASING.md)。

## 推送（三套，别混）
- **私有开发** → `git push origin main`（origin = 私有 Gitea）。
- **里程碑 tag** → `./scripts/release.sh <type>` + `git push origin main <tag>`。
- **公开镜像** → GitHub `mfreceiver/caldesk`，**只走干净单提交镜像**（历史含过密码，禁止推全历史）。见 `docs/RELEASING.md` §3。
- **个人信息**（server/token/密码/构建机 IP）**只进 `docs/LOCAL.md`（gitignored）**，绝不写已跟踪文件。

## 文档索引（完整地址）
| 文档 | 内容 |
|---|---|
| [`docs/REQUIREMENTS.md`](./docs/REQUIREMENTS.md) | 需求规格、架构决策、锁定决策表 |
| [`docs/ROADMAP.md`](./docs/ROADMAP.md) | 里程碑路线图与验收门槛 |
| [`docs/SCHEDULE.md`](./docs/SCHEDULE.md) | 排期、双轨推进规则、风险登记 |
| [`docs/BUILD.md`](./docs/BUILD.md) | 构建与发布总览（Windows 构建步骤、首次配置、数据位置） |
| [`docs/WINDOWS-BUILD.md`](./docs/WINDOWS-BUILD.md) | Windows 端「拉取+编译」运行手册 |
| [`docs/VERSIONING.md`](./docs/VERSIONING.md) | 版本号策略（git 派生，go-around 模式） |
| [`docs/RELEASING.md`](./docs/RELEASING.md) | 发版打 tag + Gitea/GitHub 推送流程（含公开镜像） |
| [`docs/REMOTE-BUILD.md`](./docs/REMOTE-BUILD.md) | 远程 Windows 构建机：SSH/sshd_config 全部坑 + 克隆 + 远程编译 |
| [`docs/AI-WINDOWS-TASK.md`](./docs/AI-WINDOWS-TASK.md) | 喂给 Windows 端 AI 插件的构建/修复任务提示词 |
| `.env.example` | live 集成测试凭据占位（复制为 `.env` 填值，`.env` 已 gitignore） |
| `docs/LOCAL.md` | ⚠ **gitignored**：真实私有值（服务器/token/密码/构建机 IP），不进版本库 |

## 常见动作速查
- 改了库代码 → `dotnet test tests/CalDesk.Tests` 必须绿。
- 改了 App(WPF) → 远程 `ssh winbuild 'cd C:\Dev_Code\caldesk && dotnet build CalDesk.slnx'` 验证。
- 发版 → `./scripts/release.sh patch`（或 minor/major）。
- 同步公开仓库 → 见 `docs/RELEASING.md` §3（先跑 §3.3 自检无私人信息）。
