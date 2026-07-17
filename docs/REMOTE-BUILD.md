# 远程 Windows 构建机（Remote Windows Build Box）

WPF 只能在 Windows 上编译；开发在 Linux 上进行。本文档记录如何让 **Linux 开发机通过 SSH
驱动局域网里一台 Windows** 完成 `restore / build / test / publish`。所有坑都已踩过并标注。

> 具体值（IP、token、路径）见 `docs/LOCAL.md`（gitignored）。本文档保持通用。

## 0. 目标拓扑
```
Linux 开发机  ──ssh──▶  Windows 构建机 (OpenSSH :22, 用户 build)
   ~/.ssh/caldesk_build            C:\Dev_Code\caldesk  (仓库克隆在此)
   ssh 别名 winbuild               git + .NET 10 SDK
```

## 1. Windows 端一次性配置（管理员 PowerShell）

### 1.1 装 .NET 10 SDK + git
```powershell
winget install Microsoft.DotNet.SDK.10
winget install Git.Git
dotnet --version    # 应 ≥ 10.0.302
```

### 1.2 开 OpenSSH Server
```powershell
Get-WindowsCapability -Online | Where-Object Name -like 'OpenSSH.Server*' | Add-WindowsCapability -Online
Start-Service sshd
Set-Service -Name sshd -StartupType Automatic
# 防火墙（默认规则通常已建）
New-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -DisplayName 'OpenSSH Server' -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 -ErrorAction SilentlyContinue
```

### 1.3 建编译用户 `build`（**必须设密码**）
```powershell
New-LocalUser -Name build -Description "remote build" -NoPassword
$pw = ConvertTo-SecureString "<一个任意强密码>" -AsPlainText -Force
Set-LocalUser -Name build -Password $pw
```
> ⚠ **坑 1**：Windows 默认策略「空密码本地账户只能控制台登录」会拦截 `build` 的网络/SSH 登录，
> 即使用密钥也不行。**必须给 `build` 设一个密码**（登录仍走密钥，密码仅为满足策略）。

### 1.4 装 Linux 公钥到 `build` 的 authorized_keys
Linux 端先生成密钥（见 §2），拿到 `.pub` 内容后：
```powershell
$dir = "C:\Users\build\.ssh"
New-Item -ItemType Directory -Path $dir -Force | Out-Null
$pub = "<粘贴 Linux 端 caldesk_build.pub 的整行>"
[IO.File]::WriteAllText("$dir\authorized_keys", $pub + "`n", (New-Object System.Text.UTF8Encoding $false))
# ACL：去掉继承，仅 build+SYSTEM+Administrators（sshd 否则拒绝）
icacls "$dir\authorized_keys" /inheritance:r /grant "build:F" "SYSTEM:F" "Administrators:F"
```
> ⚠ **坑 2（ACL 自锁）**：若曾把 ACL 锁成只 `build:F`，admin 会被锁出去、改不动。用 `takeown /F <file>`
> 夺回所有权（无视 DACL），再 `/grant`。
> 用 `[IO.File]::WriteAllText`（UTF8 no-BOM）而非 `Set-Content -Encoding ascii`，避免中文/编码把配置写坏。

### 1.5 `sshd_config`（**关键坑都在这里**）
Windows OpenSSH 的 `C:\ProgramData\ssh\sshd_config`，用**整体重写**保证干净（纯 ASCII、无 BOM）：
```powershell
$cfg = "C:\ProgramData\ssh\sshd_config"
$content = @"
Subsystem sftp sftp-server.exe
StrictModes no

Match Group administrators
    AuthorizedKeysFile __PROGRAMDATA__/ssh/administrators_authorized_keys

Match User build
    AuthorizedKeysFile C:/Users/build/.ssh/authorized_keys
"@
[IO.File]::WriteAllText($cfg, $content + "`r`n", (New-Object System.Text.UTF8Encoding $false))
icacls $cfg /reset | Out-Null
& "$env:WINDIR\System32\OpenSSH\sshd.exe" -t        # 启动前语法校验，必须无错
Start-Service sshd
```
> ⚠ **坑 3（`StrictModes` 全局专用）**：`StrictModes` **不能**放进 `Match` 块（`sshd -t` 报
> `Directive 'StrictModes' is not allowed within a Match block`）。放全局段。
> ⚠ **坑 4（home 解析）**：`build` 账户 profile/主目录可能未被初始化（`net user build` 里为空），
> sshd 用默认 `.ssh/authorized_keys`（相对 home）会找不到 key。`Match User build` 里写**绝对路径**
> `C:/Users/build/.ssh/authorized_keys` 绕开 home 解析。
> ⚠ **坑 5（改配置后起不来）**：每次改完先 `sshd.exe -t` 校验通过再 `Start-Service`，避免服务挂掉。

## 2. Linux 端一次性配置
```bash
# 生成专用密钥（无口令）
[[ -f ~/.ssh/caldesk_build ]] || ssh-keygen -t ed25519 -f ~/.ssh/caldesk_build -N "" -C "caldesk-linux-dev"
cat ~/.ssh/caldesk_build.pub    # 这一行贴到 Windows §1.4

# ssh 别名
cat >> ~/.ssh/config <<'EOF'
Host winbuild
    HostName <WINDOWS_IP>
    User build
    IdentityFile ~/.ssh/caldesk_build
    IdentitiesOnly yes
    StrictHostKeyChecking accept-new
EOF
```
验证：`ssh winbuild whoami` → 应输出 `machine\build`。

## 3. 克隆仓库到 Windows（**绕过 Git Credential Manager**）
> ⚠ **坑 6（GCM 抢认证）**：Windows 上 git 默认用 Git Credential Manager，在非交互的 SSH 会话里
> 会报 `Unable to persist credentials with the 'wincredman' credential store` 并认证失败。
> 用 `-c credential.helper=` 清空 helper，让 git 直接用 URL 里的 token：
```bash
ssh winbuild 'git -c credential.helper= clone "https://<user>:<gitea-token>@<gitea-host>/<owner>/caldesk.git" C:\Dev_Code\caldesk --quiet'
ssh winbuild 'git -C C:\Dev_Code\caldesk rev-parse --short HEAD && git -C C:\Dev_Code\caldesk describe --tags --always'
```
> token 必须有效（40-char Gitea PAT）。无效 token → Gitea 返回 `401 / Failed to authenticate user`，
> 需在 Gitea 个人设置重新生成。token 当前值/状态见 `docs/LOCAL.md`。

## 4. 远程构建 / 测试 / 发布
```bash
# 整体编译（WPF 这次能编）+ 跑库单测
ssh winbuild 'cd C:\Dev_Code\caldesk && dotnet build CalDesk.slnx -c Debug --nologo && dotnet test tests/CalDesk.Tests --nologo -v q'

# 发布单文件 EXE（framework-dependent）
ssh winbuild 'cd C:\Dev_Code\caldesk && dotnet publish src/CalDesk.App/CalDesk.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableWindowsTargeting=true'
# 产物：C:\Dev_Code\caldesk\src\CalDesk.App\bin\Release\<tfm>\win-x64\publish\CalDesk.exe

# 日常更新
ssh winbuild 'cd C:\Dev_Code\caldesk && git -c credential.helper= pull'
```
> 编译错误直接在 Linux 这边看 ssh 回显，就地修、push、远端 `git pull` 重建——无需人工转贴。

## 5. 排错速查
| 现象 | 原因 / 修法 |
|---|---|
| `Permission denied (publickey)` 但 key 对 | authorized_keys ACL 或 home 解析；`sshd -t` + 见坑 2/4 |
| `sshd` 起不来 | `sshd.exe -t` 看报错；多半是 Match 块里放了全局专用指令（坑 3）或配置编码损坏 |
| `Unable to persist credentials ... wincredman` | GCM 抢认证 → `-c credential.helper=`（坑 6） |
| `Failed to authenticate user` / 401 | Gitea token 无效 → 重新生成 |
| `build` 登录被拒 | 空密码账户被策略拦截 → 给 `build` 设密码（坑 1） |
| 连不上 22 | 不在同一网段 → 上 Tailscale/ZeroTier，或查防火墙 |
