# 芯跳 CoreBeat —— 一键发布：绿色版 + 安装包 + GitHub Release + 仓库 About
# 前置：已装 Inno Setup 6（ISCC）、已登录 gh（gh auth login）；代码中 App.Version 已改为目标版本。
# 用法：powershell -ExecutionPolicy Bypass -File tools\release.ps1 -Version 0.6.1 -Notes "本次更新说明…"

param(
  [string]$Version = "",
  [string]$Notes = "",
  [string]$Repo = "gavinlee9051/corebeat"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root 'dist'

if ([string]::IsNullOrWhiteSpace($Version)) { $Version = "0.6.1" }
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "${env:ProgramFiles}\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1

# 1) 绿色版（自包含，双击即用）
Write-Host "==> [1/4] publish green dist"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\publish.ps1')

# 2) Inno 安装包（可自定义安装目录）
if ($iscc) {
  Write-Host "==> [2/4] compile installer"
  & $iscc (Join-Path $root 'tools\installer.iss')
} else {
  Write-Warning "未找到 Inno Setup ISCC.exe，跳过安装包（仅绿色版）。"
}

# 3) 绿色 zip
Write-Host "==> [3/4] zip green dist"
$zip = Join-Path $out ("CoreBeat-" + $Version + ".zip")
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out 'CoreBeat\*') -DestinationPath $zip -CompressionLevel Optimal

# 4) GitHub Release + About
Write-Host "==> [4/4] GitHub release v$Version"
$assets = @()
if (Test-Path (Join-Path $out 'CoreBeat-Setup-x64.exe')) { $assets += Join-Path $out 'CoreBeat-Setup-x64.exe' }
if (Test-Path $zip) { $assets += $zip }
if ([string]::IsNullOrWhiteSpace($Notes)) { $Notes = "芯跳 CoreBeat v$Version 发布。详见 README。" }

gh release create ("v" + $Version) @assets --repo $Repo --title ("芯跳 CoreBeat v" + $Version) --notes $Notes

# 仓库 About 描述（GitHub 网页上也可手动改）
$about = "芯跳 CoreBeat：会呼吸的 Windows 系统健康小工具 —— 实时 CPU/GPU/温度/内存/网速监控 · 防睡眠防锁 · 桌面宠物 · 垃圾清理 · 真实网络测速"
gh repo edit $Repo --description $about

Write-Host "完成：安装包/绿色 zip 已上传 Release v$Version，About 已更新。"
