# 芯跳 CoreBeat 🫀

一块**会呼吸**的 Windows 系统健康小工具：实时掌握 CPU / GPU / 温度 / 内存 / 网速，
自带防睡眠防锁、桌面宠物、垃圾清理与真实网络测速。暗色/浅色毛玻璃 + 呼吸青绿·靛蓝主色，
把“监控”做成一块有心跳的仪表。

> Windows 端：C# .NET 8 + WPF（原生内核）+ WebView2 渲染层（Win11 23H2 优先，兼容 Win10 21H2+）。
> macOS 端为独立 SwiftUI 项目（规划中）。

## ✨ 功能
- **实时体征**：CPU（Task Manager 同口径 %Processor Utility）、逐核负载、内存、GPU、温度、网速（上下行）；
- **整机负载·心跳波形**：波形幅度与节奏随真实负载呼吸；
- **防睡眠 / 防锁**：三档档位 + 倒计时 + 加强防锁微动（退出自动还原）；
- **桌面宠物**：7 款像素伙伴，可拖拽，双击释放内存（可开关），气泡显示 CPU/内存/GPU；
- **垃圾清理**：安全/需管理员/谨慎三级目标，扫描→勾选→清理，真实可释放量、累计统计；
- **网络测速**：延迟/抖动/丢包 + 下行（国内多源自动选最快）+ 上行（端点可达时）真实吞吐；
- **个性化**：深/浅/跟随系统主题、靛蓝/深海蓝/心动紫主色、主界面卡片显隐（自适应布局）；
- **首启引导**：首次运行 4 步质感向导（欢迎 → 配色 → 习惯与权限 → 就绪）。

## 📥 安装
- **安装版（推荐）**：运行 `CoreBeat-Setup-x64.exe`（Inno 向导，**可自定义安装目录**，创建桌面/开始菜单快捷方式与卸载项）。
- **绿色版**：解压 `dist/CoreBeat-<版本>.zip`，双击 `CoreBeat.exe` 即用。
- 依赖：需系统装有 **WebView2 Evergreen** 运行时（绝大多数 Win10/11 已内置，缺失会引导下载）。

## 🔄 更新机制
托盘 **「检查更新…」**，启动后约 8s 静默检查，有新版本即提示下载覆盖安装（配置与历史保存在 `%LOCALAPPDATA%\CoreBeat`，更新不丢）。

- **路线 A（默认，当前已配置）**：GitHub Releases 零服务器。
  发布新版 = 上传 Release（Tag 如 `v0.6.1`，附件含 `CoreBeat-Setup-x64.exe`）。
  代码在 `src/CoreBeat/App.xaml.cs` 的 `UpdateRepo = "gavinlee9051/corebeat"`。
- **路线 B（自托管 update.json，备用，见下文）**：把 `UpdateManifestUrl` 指向 update.json 即可覆盖路线 A。

### 路线 B 备用说明（保留备用，需要时启用）
放一份静态 JSON（GitHub Pages / Gitee Pages / 对象存储 / 任意 nginx 均可）：
```json
{
  "version": "0.6.1",
  "url": "https://你的域名/corebeat/CoreBeat-Setup-x64.exe",
  "notes": "本次更新说明…"
}
```
然后把 `App.xaml.cs` 里 `UpdateManifestUrl` 填为该地址、并把 `UpdateRepo` 留空。

## 🔨 从源码构建
```powershell
dotnet restore CoreBeat.sln
dotnet build CoreBeat.sln -c Debug
# 运行
src\CoreBeat\bin\Debug\net8.0-windows\CoreBeat.exe
```
- 管理员模式可解锁 NVMe 温度 / 系统清理；托盘提供「以管理员身份重启」。
- 首次运行会提示是否提权（自绘弹窗）；可在「设置 → 通用 → 开机以管理员身份运行」一键配置。
- 日志：`%LOCALAPPDATA%\CoreBeat\corebeat.log`。

## 📦 新版发布操作手册（维护者）

> 一句话：**改版本号 → 提交推送 → 打 tag 推送**，剩下 GitHub Actions 自动完成。全程约 2 分钟。

### 第 0 步：改版本号
把 `src/CoreBeat/App.xaml.cs` 里的版本号改成目标版本，例如从 `0.6.1` 改成 `0.6.2`：
```csharp
public const string Version = "0.6.2";   // 只改这一行，需与 tag 一致
```
> 版本号一旦发布就不要再改；下一版用 `0.6.3`、`0.7.0` 等递增。改版本、但不打 tag，不会触发发布。

### 第 1 步：提交并推送代码
```bash
git add -A
git commit -m "feat: 0.6.2 更新说明"
git push origin main
```
> 提交信息可随意，只是记录。关键是**代码里版本号已改**、且**已推送到远端**。

### 第 2 步：打法版本号 tag → 触发自动发布（推荐）
```bash
git tag v0.6.2
git push origin v0.6.2
```
> - tag **必须带 `v` 前缀**（`v0.6.2`），且数字与第 0 步的 `Version` 完全一致。
> - 推到 tag 即触发 `.github/workflows/release.yml`：云端自动 **编译绿色版 → 编译 Inno 安装包 → 打 zip → 建 Release 并上传 → 更新 About**。
> - 在仓库 **Actions** 页能看到进度（约 3~5 分钟）；绿色即完成。

### 第 3 步：发布完成（无需人工上传）
Release 会自动带上两个资产：
- `CoreBeat-Setup-x64.exe`（安装版，可自定义目录）
- `CoreBeat-<版本>.zip`（绿色版）

用户端托盘「检查更新…」或启动静默检查（约 8s）即可发现新版本并提示下载覆盖安装。

---

### 本地一键发布（备用，本机需装 Inno Setup 6 与 gh）
```powershell
powershell -File tools\release.ps1 -Version 0.6.2 -Notes "本次更新说明…"
```
`tools\release.ps1` 会：编译 Inno 安装包 → 打绿色 zip → `gh release create v<版本>` 上传并同步 About。**与 CI 二选一即可，不要两者都跑同一版本号**（会重复建 Release）。

### 常见问题（FAQ）
- **tag 打错了 / 版本号改了但上次 tag 已发布** → 用新版本号（如 `v0.6.3`），**不要**删旧 tag 重推。
- **Release 中文显示 `??`** → 是控制台编码问题：`chcp 65001` 后再跑，或用 GitHub 网页编辑。CI 自动发布不受影响。
- **改了版本号没打 tag** → 不会发布；用户端看到的仍是旧版本。
- **想手动更新 About 描述** → 在仓库 Settings → 描述里直接改，或网页 Release 编辑页。

> ℹ️ 重复推送同一个 `v*` 标签不会重建 Release（tag 已存在即跳过）。发布新版务必递增 tag 而非复用。

## 🧹 其它
- 单实例由托盘驻留；关闭主窗口 = 收起到托盘。
- 24h 历史曲线本地留存（`history24.csv`）。
- 温度≥85℃ / 内存≥90% 气泡告警；标题栏 Logo 连点有彩蛋。
