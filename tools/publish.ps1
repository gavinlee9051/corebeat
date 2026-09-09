# 芯跳 CoreBeat —— 绿色版自包含发布
# 用法: powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\publish.ps1
# 产出: dist\CoreBeat\CoreBeat.exe 等（双击即用；需系统已装 WebView2 Evergreen 运行时）

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot          # CoreBeat 根目录
$out  = Join-Path $root 'dist\CoreBeat'

# 先清理可能占用 exe 的运行实例
Get-Process -Name CoreBeat -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish (Join-Path $root 'CoreBeat.sln') `
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false `
  -o $out

Write-Output ("发布完成: " + $out)
$exe = Join-Path $out 'CoreBeat.exe'
if (Test-Path $exe) {
  $f = Get-Item $exe
  Write-Output ("CoreBeat.exe  " + [math]::Round($f.Length / 1MB, 1) + " MB")
  $dirMB = [math]::Round(((Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 1)
  Write-Output ("文件夹合计 " + $dirMB + " MB（双击 " + $exe + " 即可运行）")
}
