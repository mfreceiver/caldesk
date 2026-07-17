# 版本管理策略（CalDesk）

CalDesk 版本号的**唯一权威来源**。`Directory.Version.props`（git 派生）+
`scripts/release.sh`（打 tag）共同实现本规则。机制移植自 ocdroid 的 "go-around" 模式。

## 版本来源：git，不写源码

CalDesk **不在任何源码/props 里写版本号**。`Directory.Version.props` 的版本属性在 MSBuild
构建期由 git 派生（`Directory.Build.props` 早已移除手写的 `<Version>`）：

| .NET 字段 | 来源 | 形态 |
|---|---|---|
| `InformationalVersion` / `Version` / `PackageVersion` | `git describe --tags --always --dirty`（去前导 `v`） | 始终带 commit 锚点：`0.1.0`（tag 处）、`0.1.0-3-gabc1234`（tag 后）、`abc1234`（无 tag）、`…-dirty`（脏树）、`0.0.0-dev`（非 git 仓库）。「关于」对话框读这个。 |
| `AssemblyVersion` | `<major>.<minor>.0.0`（取自 tag） | 保守的 CLR 标识，同一兼容线内稳定 |
| `FileVersion` | `<major>.<minor>.<patch>.<commitCount>` | `commitCount = git rev-list --count HEAD`，单调递增，Windows 文件属性可见 |

`release.sh` 构建时传 `-p:ReleaseVersion=<新tag>`，仅覆盖 tag 部分（否则 `git describe` 会取上一个旧 tag）；
hash/commitCount 部分始终反映真实 HEAD。tag 创建后从该 tag 提交重建（不带 `-p:ReleaseVersion`），
tag 部分自然就是该 tag 名。**人不需要、也不应该手写版本号到任何地方。**

## 两类发版

| 场景 | 做法 | 产物版本 |
|---|---|---|
| **里程碑发版**（新 semver） | `./scripts/release.sh <patch\|minor\|major>`：门禁（`dotnet test`）→ 推算下一 tag → 生成 changelog → 打 annotated tag → 打印 push + Windows 发布命令。然后在 Windows 跑 `dotnet publish … -p:ReleaseVersion=<tag>`。 | `InformationalVersion = <tag>`（干净） |
| **同族小修复**（不加新 tag） | 修 bug → commit → 在 Windows 跑 `dotnet publish`（**不带** `-p:ReleaseVersion`）→ EXE 自带 `<tag>-<hash>`、`FileVersion` 更高，可覆盖升级。 | `InformationalVersion = <tag>-<hash>` |

> 核心收益：**tag 后的小修无需 bump 新 semver**，重建即得一个可追溯、可升级的 EXE（版本自带 commit 锚点）。

## bump 类型（仅里程碑发版）

`release.sh` 从最新 tag 推算：

| 类型 | tag 变化 | 何时使用 |
|---|---|---|
| `patch` | `0.1.0 → 0.1.1` | Bug 修复、内部重构、构建/发版工具改动 |
| `minor` | `0.1.0 → 0.2.0` | 新用户可见功能、向后兼容的新配置/行为 |
| `major` | `0.1.0 → 1.0.0` | 破坏性变更、存储/API 不兼容 |

## 运行时读版本

「关于」对话框**从程序集读取** `InformationalVersion`，不硬编码：

```csharp
var v = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
```

## 硬规则

- **禁止手改** `Directory.Build.props` / 各 csproj 的任何版本属性——它们由 git 派生，手写值毫无意义且会被下次构建的派生值取代。
- **禁止手动 `git tag`** 做里程碑发版。必须经 `./scripts/release.sh <type>`（内部推算 + 门禁 + 打 tag）。
- **禁止复用已 push 的 tag**（`release.sh` 不会覆盖；误打则本地删重建，已 push 的不要强推，用下一 patch 修正）。
- tag 必须打在 `main` 分支的提交上（`release.sh` 已强校验）。
- WPF 发布只能在 Windows；`release.sh` 在 Linux 跑门禁 + 打 tag，发布步骤在 Windows 执行（带 `-p:ReleaseVersion=<tag>`）。
