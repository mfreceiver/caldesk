# 发布与推送流程（Releasing & Pushing）

CalDesk 的三个发布动作：**日常开发推送（Gitea origin）**、**打版本 tag（release.sh）**、
**公开镜像推送（GitHub，干净单提交历史）**。版本号模型见 [`VERSIONING.md`](./VERSIONING.md)。

## 1. 日常开发 → Gitea（origin，私有）
开发主仓在私有 Gitea。常规：
```bash
git add -A && git commit -m "..."
git push origin main
```
- 远端：`origin` = 私有 Gitea（地址见 `docs/LOCAL.md`）。
- 认证：本机已配 Gitea 凭据；Windows 构建机克隆时用 token + `-c credential.helper=`（见 `REMOTE-BUILD.md` §3）。
- **不要**把个人信息（server/token/密码）写进任何已跟踪文件——放 `docs/LOCAL.md`（gitignored）。

## 2. 打版本 tag（里程碑发版）
版本号**只来自 git tag**，不手写。用 `scripts/release.sh`：
```bash
./scripts/release.sh <patch|minor|major>
# 它会：校验 main+干净 → dotnet test 门禁 → 推算下一 semver → 生成 changelog → 创建 annotated tag
# 不自动 push；打印 push + Windows 发布命令。
git push origin main <new-tag>            # e.g. v0.1.1
```
首次发版（无历史 tag）→ `./scripts/release.sh patch` 初始化为 `v0.1.0`。

**Windows 发布带干净版本号的 EXE**（产物 → `<repo>\release\CalDesk.exe`）：
```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Version <tag>   # e.g. 0.1.0
# 不带 -Version 则 versionName 自带 <tag>-<hash>（同版本族小修复可直接发，文件版本号更高、可覆盖升级）
```
> `scripts/publish.ps1` = single-file + 去 pdb + framework-dependent，输出到 `release\`（gitignored）。

## 3. 公开镜像 → GitHub（干净单提交历史）
**为什么不能直接推全历史**：历史里曾含私人 CalDAV 密码/URL（即使在私有 Gitea 也别推到公开）。
GitHub 仓库 `mfreceiver/caldesk` 维护为**干净的单提交镜像**（一个 initial commit，无私人信息）。

### 3.1 首次发布（已做过一次）
```bash
# 从私有 origin 的 main 导出已跟踪文件（自动排除 gitignored 的 docs/LOCAL.md/.env）
rm -rf /tmp/opencode/caldesk-public && mkdir -p /tmp/opencode/caldesk-public
git -C <repo> archive main | tar -x -C /tmp/opencode/caldesk-public
cd /tmp/opencode/caldesk-public
git init -q -b main
git -c user.name=mfreceiver -c user.email=mfreceiver@gmail.com commit -q -m "CalDesk — public release"
git remote add origin git@github.com:mfreceiver/caldesk.git
git push -u origin main
```
> **认证**：GitHub HTTPS PAT 对该仓库只读；**用 SSH（`git@github.com:...` + 本机 `~/.ssh/id_rsa`）推**。

### 3.2 更新公开镜像
```bash
cd /tmp/opencode/caldesk-public
git -C <私有repo> archive main | tar -x -C .    # 覆盖为最新干净工作树
git add -A && git commit -m "CalDesk — public sync (<describe>)"
git push origin main
# 需要的话打公开 tag：git tag v<x.y.z> && git push origin v<x.y.z>
```

### 3.3 发布前自检（无私人信息残留）
把 `docs/LOCAL.md` 里记录的每个私有串（服务器域名、token、账号路径、密码）依次 grep，确认公开工作树里**只剩 `docs/LOCAL.md` 命中**：
```bash
# 把下面的占位符换成 docs/LOCAL.md 里的实际私有串
grep -rn -E "<private-domain>|<private-token>|<private-account-path>|<private-password>" \
  --include='*.md' --include='*.cs' --include='*.xaml' . | grep -v docs/LOCAL.md
# 期望：无输出（真实私有值只在 gitignored 的 docs/LOCAL.md）
```

## 4. 硬规则
- **历史里有密码** → 永远不要把私有 origin 的全历史推到公开仓库；公开仓库只走 §3 的干净镜像。
- **不要手写版本号** → 见 `VERSIONING.md`（git 派生）。
- **不要手动 `git tag` 发版** → 用 `release.sh`。
- 个人 token/密码/服务器地址只进 `docs/LOCAL.md`（gitignored），不进已跟踪文件。
