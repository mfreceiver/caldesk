#!/usr/bin/env bash
# scripts/release.sh — CalDesk 发版唯一入口（打 semver tag）。
# 详见 docs/VERSIONING.md。版本模型（go-around pattern，移植自 ocdroid）：
#   版本号不写在任何文件里，唯一来源是 git——
#     InformationalVersion/Version = git describe --tags --always --dirty（去前导 v）
#     FileVersion 第 4 段          = git rev-list --count HEAD（单调递增）
#   详见 Directory.Version.props。
#
# 本脚本只做「给仓库打一个新 semver tag + 质量门禁」：
#   1. 校验 类型、分支=main、已跟踪文件干净
#   2. 质量门禁：dotnet test（核心库，Linux 可跑；WPF 外壳的发布在 Windows 单独做）
#   3. 由最新 git tag 推算下一版本（patch|minor|major）
#   4. 生成 changelog（上个 tag..HEAD 的提交分组）
#   5. 创建 annotated tag（注释 = changelog）——不 commit 任何版本文件、不自动 push
#   6. 打印 push 命令 + Windows 发布命令（带 -p:ReleaseVersion=<tag>）
#
# 「同版本族重发小修复」：tag 后的小修，直接 commit →
#   在 Windows 跑 dotnet publish（不带 -p:ReleaseVersion）→ EXE 自带 <tag>-<hash>、
#   文件版本号更高，可覆盖升级，无需新 tag。只有里程碑式发版才跑本脚本打新 tag。

set -euo pipefail

TYPE="${1:-}"
[[ "$TYPE" =~ ^(patch|minor|major)$ ]] || { echo "用法: ./scripts/release.sh <patch|minor|major>"; exit 1; }

REPO_ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
cd "$REPO_ROOT"

# --- 1. git 前置校验 ---
BRANCH=$(git branch --show-current)
[[ "$BRANCH" == "main" ]] || { echo "❌ 当前分支=$BRANCH，发版必须在 main"; exit 1; }

# 允许 untracked，但已跟踪文件不能有改动
if [[ -n "$(git status --porcelain --untracked-files=no)" ]]; then
  echo "❌ 已跟踪文件有未提交改动，先 commit 或 stash："; git status -s; exit 1
fi

# --- 2. 质量门禁（核心库单测）---
if command -v dotnet >/dev/null 2>&1; then
  echo "==> dotnet test（核心库质量门禁）"
  dotnet test tests/CalDesk.Tests --nologo -v q
else
  echo "⚠  未找到 dotnet，跳过测试门禁（请确保已在别处通过）"
fi

# --- 3. 推算下一版本 ---
LATEST="$(git describe --tags --abbrev=0 2>/dev/null || true)"
if [[ -z "$LATEST" ]]; then
  echo "（无历史 tag，初始化为 0.1.0）"
  NEXT="0.1.0"
else
  BASE="${LATEST#v}"
  MAJOR="$(echo "$BASE" | cut -d. -f1)"
  MINOR="$(echo "$BASE" | cut -d. -f2)"
  PATCH="$(echo "$BASE" | cut -d. -f3)"
  PATCH="${PATCH:-0}"
  case "$TYPE" in
    major) MAJOR=$((MAJOR+1)); MINOR=0; PATCH=0;;
    minor) MINOR=$((MINOR+1)); PATCH=0;;
    patch) PATCH=$((PATCH+1));;
  esac
  NEXT="$MAJOR.$MINOR.$PATCH"
fi
TAG="v$NEXT"

echo "==> 下一版本: $TAG （基于 $([ -n "$LATEST" ] && echo "$LATEST" || echo "无tag")，bump=$TYPE）"

# --- 4. changelog（上个 tag..HEAD，按 conventional 前缀分组）---
RANGE="$([ -n "$LATEST" ] && echo "$LATEST..HEAD" || echo "HEAD")"
{
  echo "CalDesk $NEXT"
  echo ""
  feat=$(git log "$RANGE" --pretty=format:"- %s" --no-merges 2>/dev/null | grep -iE '^[0-9a-f]+ - (feat|add|新增|支持)' || true)
  fix=$(git log "$RANGE" --pretty=format:"- %s" --no-merges 2>/dev/null | grep -iE '^[0-9a-f]+ - (fix|修复|修正)' || true)
  other=$(git log "$RANGE" --pretty=format:"- %s" --no-merges 2>/dev/null | grep -ivE '^[0-9a-f]+ - (feat|fix|add|新增|支持|修复|修正)' || true)
  if [[ -n "$feat" ]]; then echo "## 新增 / Features"; echo "$feat"; echo ""; fi
  if [[ -n "$fix" ]]; then echo "## 修复 / Fixes"; echo "$fix"; echo ""; fi
  if [[ -n "$other" ]]; then echo "## 其他"; echo "$other"; echo ""; fi
} > /tmp/caldesk-changelog.txt
echo "--- changelog ---"; cat /tmp/caldesk-changelog.txt; echo "-----------------"

# --- 5. 创建 annotated tag（不 push）---
git tag -a "$TAG" -F /tmp/caldesk-changelog.txt
rm -f /tmp/caldesk-changelog.txt
echo ""
echo "✅ 已创建 tag $TAG（未 push）"
echo ""
echo "下一步（人工确认后执行）："
echo "  git push origin main $TAG"
echo "  # 然后在 Windows 上发布带干净版本号的 EXE："
echo "  dotnet publish src/CalDesk.App/CalDesk.App.csproj -c Release -r win-x64 \\"
echo "    --self-contained false -p:PublishSingleFile=true \\"
echo "    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableWindowsTargeting=true \\"
echo "    -p:ReleaseVersion=$NEXT"
