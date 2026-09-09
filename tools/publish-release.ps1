# ============================================================================
# CoreBeat one-click release (publish-release.ps1)
# Action: bump version in App.xaml.cs -> commit+push main -> push v*tag -> trigger GitHub Actions auto build/release
# Requires: git logged in (PAT present), repo has .github/workflows/release.yml (CI)
# Usage:
#   # auto bump patch (0.6.1 -> 0.6.2) and publish
#   powershell -ExecutionPolicy Bypass -File tools\publish-release.ps1
#   # pin target version
#   powershell -ExecutionPolicy Bypass -File tools\publish-release.ps1 -Version 0.7.0
#   # preview only (no file change / no push), recommend first run
#   powershell -ExecutionPolicy Bypass -File tools\publish-release.ps1 -DryRun
# NOTE: keep this file as UTF-8 WITH BOM so Windows PowerShell 5.1 reads Chinese correctly.
# ============================================================================

param(
  [string]$Version          = "",
  [string]$CommitMessage    = "",
  [switch]$SkipBuild,
  [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root      = Split-Path -Parent $PSScriptRoot
$appCsPath = Join-Path $root 'src\CoreBeat\App.xaml.cs'

# --- read current version (use -match to populate $Matches) ---
$curText = [System.IO.File]::ReadAllText($appCsPath)
if ($curText -notmatch 'Version\s*=\s*"(\d+\.\d+\.\d+)"') { throw "Cannot find version in $appCsPath" }
$current = $Matches[1]
Write-Host "Current version: $current"

# --- resolve target version ---
if ([string]::IsNullOrWhiteSpace($Version)) {
  $parts = $current -split '\.'
  $parts[2] = [string]([int]$parts[2] + 1)
  $Version = ($parts -join '.')
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid version: $Version (should be x.y.z like 0.6.2)" }
if ($Version -eq $current) { throw "Target version ($Version) equals current; pass a new one via -Version" }
$tag = 'v' + $Version
Write-Host "Target version: $Version   tag: $tag"

# --- preview (dry-run stops here) ---
if ($DryRun) {
  if ([string]::IsNullOrWhiteSpace($CommitMessage)) { $CommitMessage = "release: v$Version" }
  Write-Host ""
  Write-Host "--- DRY-RUN preview (nothing changed / nothing pushed) ---"
  Write-Host "Will modify: $appCsPath"
  Write-Host "  $current  ->  $Version"
  Write-Host "Will run:"
  Write-Host "  git add -A"
  Write-Host "  git commit -m `"$CommitMessage`""
  Write-Host "  git push origin main"
  Write-Host "  git tag $tag"
  Write-Host "  git push origin $tag"
  Write-Host "  (after push tag, GitHub Actions auto builds and creates the Release; watch repo Actions)"
  Write-Host ""
  Write-Host "Preview done. Drop -DryRun to actually publish."
  exit 0
}

# --- 1) bump version (UTF-8 no BOM, keep Chinese comments) ---
# keep the `Version = "` prefix and trailing quote; only swap the version number
$newText = [regex]::Replace($curText, '(Version\s*=\s*")[^"]*(")', '${1}' + $Version + '${2}')
[System.IO.File]::WriteAllText($appCsPath, $newText, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Bumped version: $appCsPath  -> $Version"

# --- 2) local build check (optional) ---
if (-not $SkipBuild) {
  Write-Host "==> local build check (dotnet build -c Release) ..."
  & dotnet build (Join-Path $root 'CoreBeat.sln') -c Release --nologo
  if ($LASTEXITCODE -ne 0) { throw "Build failed, publish aborted" }
  Write-Host "Build OK."
} else {
  Write-Host "==> skipped local build check (SkipBuild)"
}

# --- 3) commit and push main ---
Write-Host "==> commit and push main ..."
if ([string]::IsNullOrWhiteSpace($CommitMessage)) { $CommitMessage = "release: v$Version" }
git -C $root add -A
git -C $root commit -m $CommitMessage
git -C $root push origin main
if ($LASTEXITCODE -ne 0) { throw "push main failed" }

# --- 4) tag and push, trigger CI ---
Write-Host "==> tag $tag and push (triggers auto publish) ..."
if (git -C $root rev-parse -q --verify "refs/tags/$tag") { throw "tag $tag already exists, use a new version" }
git -C $root tag $tag
git -C $root push origin $tag
if ($LASTEXITCODE -ne 0) { throw "push tag failed" }

Write-Host ""
Write-Host "======================================================"
Write-Host " triggered auto publish $tag"
Write-Host " 1) repo Actions page shows progress (~3-5 min -> green done)"
Write-Host " 2) Release will contain CoreBeat-Setup-x64.exe and CoreBeat-$Version.zip"
Write-Host " 3) users check via tray 'Check updates' to discover $Version"
Write-Host "======================================================"
