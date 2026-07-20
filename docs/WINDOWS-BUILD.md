# CalDesk — Windows 端「拉取 + 编译」流程（Kilo 新会话用）

> 项目的开发主要在 Linux 上进行；本机 Windows 只负责：**拉取最新代码 → 在 Windows 编译 WPF 外壳 `CalDesk.App` → 跑测试**。本文件是这条流程的可复用运行手册。

## 一、一次性前置条件
- **.NET 10 SDK ≥ 10.0.302**（含桌面运行时 `10.0.10`）。检查：`dotnet --version`
- **Git 凭据**已配置（HTTPS 凭据助手 / PAT / SSH key），`git pull` 免输入。
- 仓库已克隆在 `<your clone path>`，远程：
  `origin = https://github.com/mfreceiver/caldesk.git`（用 HTTPS）。

## 二、标准流程（每次更新）
```powershell
cd <your clone path>
git pull
dotnet restore CalDesk.slnx
dotnet build CalDesk.slnx -c Debug -nologo      # 目标：0 error / 0 warning
dotnet test tests/CalDesk.Tests --nologo        # 目标：全部通过
```
- 解决方案是 **`CalDesk.slnx`**（不是 `.sln`）。
- 需要 **Windows** 才能编译（`CalDesk.App` 是 WPF，`net10.0-windows`）。
- 发布单文件 EXE：`powershell -ExecutionPolicy Bypass -File scripts\publish.ps1`（dev/试用构建，版本自动带 commit 锚点）；里程碑发版先在 Linux 跑 `./scripts/release.sh <patch|minor|major>` 打 tag 并 push，再用 `publish.ps1 -Version <tag>` 出干净版本号。产物 → `release\CalDesk.exe`（~29 MB，framework-dependent）。

## 三、Windows 构建适配（已提交，`git pull` 后即编即测）
> 让上游（Linux 编写）的代码在 Windows 上通过编译/测试所需的改动，**全部位于 `src/CalDesk.App/**` 与 `tests/**`，未改核心库**。已在 `131ebeb` 提交回远端，无需手工补。

1. **`src/CalDesk.App/CalDesk.App.csproj`** → 增加 `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`（`LibraryImport` 源生器 + 凭据 P/Invoke 结构体需要 unsafe）。
2. **`src/CalDesk.App/Views/WeekWindow.xaml`** → 「今天」徽章由 `<Run Visibility=...>` 改为水平 `StackPanel` 内独立 `<TextBlock>`（`Run` 是 `Inline`，无 `Visibility` 属性）。
3. **`src/CalDesk.App/ViewModels/QuickAddViewModel.cs`** → 周重复的 `switch` 改为三元 `==` 比较（字面量拼接方法调用非常量，不能作 `switch` 常量模式）。
4. **`src/CalDesk.App/App.xaml.cs`** → 月/周窗口三元真分支强制 `(Window?)`（`MonthWindow` 与 `WeekWindow` 间无隐式转换）。
5. **`tests/CalDesk.Tests/StorageRepositoryTests.cs`** → `CleanupDb` 增加 `SqliteConnection.ClearAllPools()` + 删除重试（Windows 上连接池锁住 temp `.db`，导致 teardown 删文件报“文件被占用”）。

> ✅ **已验证**：`131ebeb` 在 Windows 构建机上 `dotnet build CalDesk.slnx` = **0 错误 / 0 警告**；`dotnet test` = **全部通过**。第二节“拉取即编译即测”成立。

## 四、在 Kilo 新会话里如何使用
在新会话直接发送（任选其一）：
- 推荐：
  > 按 `docs/WINDOWS-BUILD.md` 拉取最新代码，并在 Windows 上完成 `restore / build / test`；若编译失败，按第三节核对 5 处修复。
- 或更简：
  > 执行 `docs/WINDOWS-BUILD.md` 的第二节流程。

预期结果：`dotnet build` 0 error；`dotnet test` 全部通过。

## 五、常见问题
- **`找不到 SDK / net10.0`**：本机未装 .NET 10 SDK。安装：`winget install Microsoft.DotNet.SDK.10`。
- **测试报“文件被占用”**：见第三节第 5 条（`ClearAllPools` + 重试）。
- **`git pull` 提示要凭据**：确认已配置 HTTPS 凭据（Windows 凭据管理器 / PAT / SSH key）。
- **克隆失败**：确认用 `https://` 克隆，且网络可达 GitHub。
- **`git pull` 报“本地改动会被覆盖”**：第三节那 5 处修复是**未提交**改动。若无冲突直接 pull 即可；若冲突，先 `git stash` → `git pull` → `git stash pop`，再按第三节确认 5 处仍在。若工作树被重置/全新克隆，修复会丢失，需按第三节逐条补回。
