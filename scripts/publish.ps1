# scripts/publish.ps1 — publish CalDesk as a single-file EXE into <repo>\release\
#
# Usage (on Windows, or via ssh winbuild):
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1                # version = git describe
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Version 0.1.0 # clean version
#
# Produces: <repo>\release\CalDesk.exe  (single-file, framework-dependent; target needs .NET 10 desktop runtime)
param(
    [string]$Version = ""   # passed as -p:ReleaseVersion=<Version>; empty => git-derived describe
)

$ErrorActionPreference = "Stop"
$repoRoot  = Split-Path -Parent $PSScriptRoot        # scripts\.. = repo root
Set-Location $repoRoot
$releaseDir = Join-Path $repoRoot "release"

# Clean previous output so release\ only holds the latest build.
if (Test-Path $releaseDir) { Remove-Item -Recurse -Force $releaseDir }
New-Item -ItemType Directory -Force $releaseDir | Out-Null

$props = @(
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableWindowsTargeting=true",
    "-p:DebugType=none",          # no embedded debug info
    "-p:DebugSymbols=false"        # no .pdb side files
)
if ($Version) { $props += "-p:ReleaseVersion=$Version" }

Write-Host "==> dotnet publish -> release\  (Version=$(if ($Version) {$Version} else {'git-describe'}))" -ForegroundColor Cyan
dotnet publish src/CalDesk.App/CalDesk.App.csproj `
    -c Release -r win-x64 --self-contained false `
    -o $releaseDir --nologo @props
if ($LASTEXITCODE -ne 0) { Write-Error "publish failed (exit $LASTEXITCODE)"; exit $LASTEXITCODE }

$exe = Join-Path $releaseDir "CalDesk.exe"
if (-not (Test-Path $exe)) { Write-Error "CalDesk.exe not found in $releaseDir"; exit 1 }
$mb = [math]::Round((Get-Item $exe).Length / 1MB, 2)
Write-Host "==> OK: $exe  ($mb MB)" -ForegroundColor Green
Write-Host "    (single-file, framework-dependent — target needs .NET 10 desktop runtime)"
