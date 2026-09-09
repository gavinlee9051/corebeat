# ============================================================================
# 芯跳 CoreBeat —— 一键发布新版（推荐日常使用）
# 作用：更新 App.xaml.cs 版本号 -> 提交推送 main -> 打 v 前缀 tag 并推送 -> 触发 GitHub Actions 自动出包并建 Release
# 依赖：git 已登录（本机已存 PAT），仓库已配好 .github/workflows/release.yml（CI）
# 用法：
#   # 自动递增补丁号（如 0.6.1 -> 0.6.2），然后一键发布
#   powershell -ExecutionPolicy Bypass -File tools\publish-release.ps1
#   # 指定目标版本
#   powershell -ExecutionPolicy Bypass -File tools\publish-release.ps1 -Version 0.7.0
#   # 只预览要做什么，不真正改版本/不推送（推荐先跑一次）
#   powershell -ExecutionPolicy Bypass -File tools\publish-release.ps1 -DryRun
# ============================================================================

param(
  [string]$Version          = "",          # 目标版本；留空则自动递增当前补丁号
  [string]$CommitMessage    = "",          # 提交信息；留空自动生成
  [switch]$SkipBuild,                       # 跳过本地编译校验
  [switch]$DryRun                           # 只预览，不执行任何写操作
)

$ErrorActionPreference = 'Stop'
$root      = Split-Path -Parent $PSScriptRoot
$appCsPath = Join-Path $root 'src\CoreBeat\App.xaml.cs'

# --- 提取当前版本号（用 -match 才会填充 $Matches；注意捕获组） ---
$curText = [System.IO.File]::ReadAllText($appCsPath)
if ($curText -notmatch 'Version\s*=\s*"(\d+\.\d+\.\d+)"') { throw "未找到版本号，请检查 $appCsPath" }
$current = $Matches[1]
Write-Host "当前版本: $current"

# --- 确定目标版本 ---
if ([string]::IsNullOrWhiteSpace($Version)) {
  $parts = $current -split '\.'
  $parts[2] = [string]([int]$parts[2] + 1)
  $Version = ($parts -join '.')
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "版本号格式无效: $Version （应为 x.y.z，如 0.6.2）" }
if ($Version -eq $current) { throw "目标版本 ($Version) 与当前相同，请用 -Version 指定新号" }
$tag = 'v' + $Version
Write-Host "目标版本: $Version   tag: $tag"

# --- 预览（DryRun 到此为止） ---
if ($DryRun) {
  if ([string]::IsNullOrWhiteSpace($CommitMessage)) { $CommitMessage = "release: v$Version" }
  Write-Host "`n--- DRY-RUN 预览（未改动任何文件/未推送）---"
  Write-Host "将要修改: $appCsPath"
  Write-Host "  $current  ->  $Version"
  Write-Host "将要执行:"
  Write-Host "  git add -A"
  Write-Host "  git commit -m `"$CommitMessage`""
  Write-Host "  git push origin main"
  Write-Host "  git tag $tag"
  Write-Host "  git push origin $tag"
  Write-Host "  （push tag 后 GitHub Actions 自动出包并建 Release，去仓库 Actions 查看）"
  Write-Host "`n预览结束。去掉 -DryRun 再跑即真正发布。"
  exit 0
}

# --- 1) 更新版本号（UTF-8 无 BOM，保留中文注释） ---
$newText = [regex]::Replace($curText, 'Version\s*=\s*"(\d+\.\d+\.\d+)"', "`$Version")
[System.IO.File]::WriteAllText($appCsPath, $newText, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "已更新版本号: $appCsPath  -> $Version"

# --- 2) 本地编译校验（可选跳过） ---
if (-not $SkipBuild) {
  Write-Host "==> 本地编译校验 (dotnet build -c Release) ..."
  & dotnet build (Join-Path $root 'CoreBeat.sln') -c Release --nologo
  if ($LASTEXITCODE -ne 0) { throw "编译失败，发布中止" }
  Write-Host "编译通过。"
} else {
  Write-Host "==> 跳过了本地编译校验 (SkipBuild)"
}

# --- 3) 提交并推送 main ---
Write-Host "==> 提交并推送 main ..."
if ([string]::IsNullOrWhiteSpace($CommitMessage)) { $CommitMessage = "release: v$Version" }
git -C $root add -A
git -C $root commit -m $CommitMessage
git -C $root push origin main
if ($LASTEXITCODE -ne 0) { throw "推送 main 失败" }

# --- 4) 打 tag 并推送，触发 CI ---
Write-Host "==> 打 tag $tag 并推送（触发自动发布）..."
if (git -C $root rev-parse -q --verify "refs/tags/$tag") { throw "tag $tag 已存在，请用新版本号" }
git -C $root tag $tag
git -C $root push origin $tag
if ($LASTEXITCODE -ne 0) { throw "推送 tag 失败" }

Write-Host ""
Write-Host "======================================================"
Write-Host " 已触发自动发布 $tag"
Write-Host " 1) 到仓库 Actions 页查看进度（约 3~5 分钟绿灯即完成）"
Write-Host " 2) 完成后 Release 自动含: CoreBeat-Setup-x64.exe 与 CoreBeat-$Version.zip"
Write-Host " 3) 用户端托盘「检查更新…」即可发现 $Version"
Write-Host "======================================================"
