# CONTINUE.md · 芯跳 CoreBeat 项目交接说明

> 用途：供任何"新对话/新会话"的开发者（含 AI 助手）快速接续本项目。
> 最近更新：M5 交付准备完成 —— 中文《使用说明》、绿色版自包含发布脚本与产物（dist\CoreBeat，~166MB），发布版冒烟通过。
> 接手方式：先读本文件，再读 docs 下两份方案，然后从"下一步"开始。

## 0. 项目一句话
「芯跳 CoreBeat」：Windows 与 macOS 双平台的"会呼吸"系统资源健康小工具——
实时资源/温度监控 + 正在运行的软件/进程总览 + 防睡眠/防自动锁屏 + 置顶迷你 HUD，
注重 UI 质感与动效，内置小彩蛋。

## 1. 文件位置（工作区）
- 根目录：C:\Users\z1539\Documents\works\CoreBeat
- docs\方案-Windows-芯跳CoreBeat.md   —— Windows 版定稿方案（v1.0 已确认）
- docs\方案-macOS-芯跳CoreBeat.md    —— macOS 版定稿方案（v1.0 已确认）
- docs\CONTINUE.md                    —— 本交接说明
- CoreBeat.sln                        —— 解决方案（含 src\CoreBeat 主项目）
- src\CoreBeat\                       —— WPF 主程序（net8.0-windows + WebView2 SDK 1.0.4191.47）
  - App.xaml(.cs)                     —— 应用入口（日志/退出路径；关闭=藏托盘）
  - MainWindow.xaml(.cs)              —— 无边框主窗口 + DWM 圆角/亚克力 + WebView2 宿主 + 前端命令桥
  - SystemTray.cs                     —— 托盘：显示/隐藏/回放动画/退出
  - Native\Dwm.cs                     —— Win11 圆角/沉浸深色/系统亚克力（失败自动降级）
  - Services\                         —— M2 数据层服务
    · SensorService.cs                —— LibreHardwareMonitorLib 传感器（CPU/GPU/NVMe，读不到降级）
    · SystemMetrics.cs                —— 内存/运行时长/网络实时速率
    · ProcessMonitor.cs               —— 进程 1s 差分 + 图标缓存
    · AwakeService.cs                 —— 防睡眠三档 + 倒计时 + 微动 + 退出还原
    · MetricsPusher.cs                —— 1s 采样聚合 → JSON 推送桥（含 10s 诊断日志）
  - Assets\CoreBeat.ico               —— 应用/窗口/托盘图标（多尺寸，脚本生成）
  - Web\splash.html + css\app.css + js\app.js —— WebView2 渲染层（启动动画 + 实时仪表）
- tools\gen-icon.ps1                  —— 图标生成脚本（需要时重跑；注意须以 UTF-8 BOM 保存）

## 2. 已确认的关键决策（勿随意变更，改动需用户确认）
| 项 | 决定 |
|---|---|
| 软件名 | 芯跳 CoreBeat（"芯/心"同音双关，心电图视觉语言） |
| Windows 技术栈 | C# .NET 8 + WPF（原生内核）+ WebView2 渲染层 |
| macOS 技术栈 | Swift + SwiftUI 原生（独立代码库，MAUI 已否决） |
| Windows 目标 | Win11 23H2 为主，兼容 Win10 21H2+ |
| 硬件覆盖 | Intel/AMD CPU、NVIDIA/AMD 独显（含核显）、NVMe |
| v1 范围 | ①实时资源+温度 ②进程列表(图标/排序/结束) ③防睡眠/防自动锁(三档+倒计时) |
| v1 加值项 | 置顶迷你 HUD、24h 历史留存、温度/内存告警、开机自启（全含） |
| 彩蛋 | 主：连点 5 下 Logo 波形变跳动爱心+随机台词；副：温度卡连点 3 下俏皮气泡（>85℃ 换文案） |
| 权限策略 | 默认普通权限，传感器不可读时提示一键重启提权；不强求管理员 |
| 交付形态 | 绿色版单文件夹，双击即用 |
| 内存预算 | < 120MB；空闲 CPU 占用 < 1% |

## 3. 开发环境状态（已就绪 100%）
- 系统：Windows 11 23H2（build 22631），Pro for Workstations
- CPU：Intel Core i5-12400（6C/12T）；内存 32GB；磁盘 C 930GB/D 932GB 充足
- GPU：NVIDIA RTX 3060 Ti（有 Remote Display Adapter，用户常经 RDP 操作）
- .NET 8 SDK：8.0.424（C:\Program Files\dotnet）
- Microsoft.WindowsDesktop.App 8.0.30（WPF 运行时）✅
- WebView2 运行时 152.x ✅；git ✅；winget ✅；Visual Studio 未装（非必需）
- 注意：本助手会话沙箱无法访问外网、无法写工作区以外的路径 ——
  任何需要联网下载/NuGet 还原受限场景，应把命令交给用户在本机执行。

## 4. 当前进度
- 方案：Windows 与 macOS 两份均定稿并保存 ✅
- M1 骨架：已完成并启动验证 ✅（详见下方 M1 要点）
- M2 数据层：已完成并在本机验证真实读数 ✅
  - 传感器服务（Services\SensorService.cs，LibreHardwareMonitorLib 0.9.7-pre733 nightly）：
    实测结论（本机 ASUS TUF B660M-PLUS / i5-12400 / RTX 3060 Ti）：
    - GPU 温度/占用：普通权限可读 ✅（40°C / 核心负载）
    - NVMe 温度：需管理员（当前温度传感器在管理员会话才出现，实测 35~36°C）
    - **CPU 温度：管理员 + nightly 均无该传感器** —— LHM Ring0 驱动在本板未加载
      （内存完整性已关，仍不加载；判定为驱动/主板兼容问题）。已按方案降级显示"—"并如实提示，不再强求。
    - 存储"Warning/Critical Temperature"为告警阈值非实时温度，已过滤，防误读（如 70°C 假值）。
  - 托盘「以管理员身份重启」仍保留：用于解锁 NVMe 温度读取。
  - CPU 总占用（Services\PdhCpuSampler.cs）：**PDH `\Processor Information(_Total)\% Processor Utility`**
    —— 与 Windows 11 任务管理器完全同源（Win10 才是 % Processor Time，空闲会偏低，切勿再用）。
    注意本机（虚拟化/工作站）Utility 空闲 ≈15~25%，且随视频/网络出现 50~60% 尖峰，属该机正常特性。
    内核差分（NtQuery）作对照；4Hz 快速通道单独推送 CPU 让数值瞬时跟手。
    已用 Get-Counter 两口径实测验证：Utility≈23~25% ↔ 应用 17%~25% 同步。
  - GPU：占用（LHM GPU Core Load，实测 RDP 会话 ~24%）+ 温度 + 型号名称（前端第五体征卡）。
  - 系统指标（Services\SystemMetrics.cs）：内存 GlobalMemoryStatusEx、系统运行时长、网络实时速率
    （多网卡增量差分，实测有流量时读到 0.4~0.7Mbps）。
  - 进程服务（Services\ProcessMonitor.cs）：1s 差分 CPU%、内存 MB、应用图标
    （SHGetFileInfo 32px PNG base64，路径缓存，仅首次发送）。
  - 防睡眠服务（Services\AwakeService.cs + 托盘）：三档 SetThreadExecutionState
    （仅防睡 / +防熄屏 / 加强防锁叠加 45s 一次 2px 微动 mouse_event）+ 倒计时
    （30 分钟 / 1 小时 / 整夜 8 小时 / 不限时）到期自动还原；退出与注销前 Restore 还原系统设置。
  - 推送桥（Services\MetricsPusher.cs）：1s 采样 → JSON（t:"tick"）+ 4Hz CPU 快速通道（t:"fast"）；
    防睡状态变化即时推送（t:"awake"）；前端先仿真占位、收到真实数据后切换
    「实时数据 · 1s 刷新」，体征/温度仪表盘/进程榜(动态行+图标)/曲线全部接真值。
    通道说明：本机实测 WPF WebView2 152 的 PostWebMessageAsJson 原生→前端不投递，
    已统一改用 ExecuteScriptAsync 注入 window.cbOnNative(json)（前端保留 message 监听作兼容）。
- M3 主界面增强：已完成并启动验证 ✅
  - CPU 逐核占用柱（LHM 每线程 Load 按物理核取最大：6 核 6 条彩色微柱，实测 [19,5,3,3,2,2]）
    + 频率展示（当前无 Clock 传感器时如实显示"—"）。
  - 磁盘读写真实速率（Services\PdhDiskSampler.cs：PhysicalDisk(_Total)\Disk Read/Write Bytes/sec，
    与任务管理器性能页同源）→ MB/s 实时文本 + sparkline（替代"待 M3"占位）。
  - 进程详情浮层：点进程行 → PID/内存/线程数/启动时间/路径；「结束进程」两次点击确认；
    权限不足提示"以管理员身份重启"；不可结束自身。
  - 构建 0 警告 0 错误；启动验证日志含逐核与磁盘真实读数。

- M4 打磨：已实现并构建验证 ✅（逐批预览中）
  - 运行软件列表（按 exe 归并，chrome×N 一行，合计占用）自带搜索过滤框。
  - 3 组强调色预设（青绿/蓝/紫，点标题栏圆点，localStorage 持久化）。
  - 告警：温度≥85℃ 或内存≥90% → 右上气泡 + 提示音（WebAudio），10 分钟防重复。
  - 彩蛋：标题栏 Logo 连点 5 下 → 跳动爱心 + 随机台词（25s 还原）；
    温度大数字连点 3 次 → 俏皮气泡（>85℃ 换"发烧"文案）。
  - 置顶迷你 HUD（MiniHud.cs + Web\hud.html）：桌面角小窗（波形+CPU/内存/温度），
    数据经 App.Broadcast 广播；可拖拽、托盘调透明度(100~40%)、双击回主界面。
  - 24h 历史：Services\HistoryStore.cs 按分钟聚合落盘 history24.csv（重启不丢），
    标题栏 24h 按钮回看 CPU/内存趋势曲线。
  - 开机自启开关（托盘，默认关，HKCU Run 键）。
  - 进程结束命令支持 pids[] 整组结束；修复 send() 参数契约（cmd 字符串+extra）后 js→native 全通。

- M5 交付准备：已完成 ✅
  - 中文使用说明：docs\使用说明-芯跳CoreBeat.md（功能/权限边界/常见问题）。
  - WebView2 缺失引导：初始化失败弹窗 → 一键打开官方下载页。
  - 绿色版发布：tools\publish.ps1（Release/win-x64/self-contained 单文件夹，需联网还原）；
    已发布到 dist\CoreBeat（~166MB，双击 CoreBeat.exe 即用），发布版冒烟验证通过（日志无异常）。
  - 版本号升至 0.5.0。

M1 要点（回顾）：
- 工程结构：CoreBeat.sln + src\CoreBeat（net8.0-windows，WPF + WebView2 1.0.4191.47）
- 无边框主窗口（DWM 圆角/沉浸深色/亚克力降级安全）；HTML 自定义标题栏（拖动/最小化/最大化/收托盘经原生桥）
- 托盘 NotifyIcon（显示/隐藏/回放动画/退出；关闭窗口=藏托盘驻留）
- 「一次心跳」启动动画 + 假数据主界面（WebView2 纯 HTML/CSS/JS）
- 图标 Assets\CoreBeat.ico（tools\gen-icon.ps1 生成，须 UTF-8 BOM）

## 5. 下一步：M4 打磨（等用户确认 M3 预览后开工）
对应 Windows 方案 M4 里程碑（v1 加值项全含）：
1. 置顶迷你 HUD：桌面角落"一颗活的心脏"，只留波形+三指标，可拖拽/调透明度/双击回主界面。
2. 24h 历史曲线本地留存：重启不丢、可回看趋势。
3. 温度/内存异常告警：气泡提醒 + 可选提示音。
4. 彩蛋：主「恋恋芯跳」（Logo 连点 5 下 → 跳动爱心+随机台词，25s 还原）；
   副「发不发烫」（温度卡连点 3 次气泡，>85℃ 换文案）。
5. 3 组强调色预设 + 开机自启开关（默认关）。
6. 托盘防睡菜单状态可见性打磨（剩余时间/档位色标，已有基础）。

运行方式（用户本机，首次需联网还原）：
- dotnet restore CoreBeat.sln && dotnet build CoreBeat.sln -c Debug
- 运行 src\CoreBeat\bin\Debug\net8.0-windows\CoreBeat.exe
- 托盘「以管理员身份重启」用于解锁 NVMe 温度读取（出现 UAC 属正常）
- 日志：%LOCALAPPDATA%\CoreBeat\corebeat.log（每 10s 一行采样摘要便于验证）

## 6. 接手清单（给新会话的指令模板）
1. 读 docs\方案-Windows-芯跳CoreBeat.md 与 docs\CONTINUE.md。
2. 确认本机 dotnet 环境（dotnet --list-sdks）。
3. 执行 M1 步骤并逐步向用户展示可预览效果。
4. 每个里程碑结束先与用户确认，再进入下一阶段。
5. 温度库方案：LibreHardwareMonitorLib（NuGet），需评估其在 i5-12400 / RTX 3060 Ti 上的实际读数。

## 7. 风险提示（开发期）
- NuGet 还原可能需要联网：若本助手沙箱受限，需用户在本机执行 dotnet restore/build。
- RDP 会话下 GPU 传感器与动画表现与物理桌面略有差异，验收时请用户在物理桌面复核。
- 实测：CPU 负载/内存/GPU 普通权限可读；NVMe 温度需管理员（托盘已提供一键提权）；
  CPU 温度在 ASUS B660M + i5-12400 上 LHM（含 nightly、管理员）均不可读（Ring0 驱动未加载），
  已按方案降级显示"—"。若换主板/换 LHM 大版本可重测。
- Win11 任务管理器 CPU 数字 = % Processor Utility（非 % Processor Time）；本机 Utility 空闲即 ~15~25% 且常随视频/网络跳到 50%+，属机器特性。
- 防锁「加强档」微动用 mouse_event 2px/45s，对正常使用无感；RDP 会话下同样生效。

## 8. 后续想法 / 待办（v1.1+，暂未实现；用户已同意"先记录后面再弄"）

### 测速上行彻底解决：自建 LibreSpeed（国内准确）
- **背景**：CoreBeat 已内置网络测速（主界面「网速」磁贴 ⚡ → 测速弹窗）。
  下行用国内源（阿里云/清华/腾讯/中科大镜像，自动选最快，很准）；
  **上行**目前只能试 Cloudflare `__up` / httpbin / postman-echo 等，**国内多不可达 → 显示"不可达"**。
  （设置→通用 已有「测速上传地址」输入框，填了优先用作上传端点。）
- **待做（等用户提供地址）**：让用户在**国内可达主机**部署 **LibreSpeed**（免费）：
  - Docker：`docker run -d -p 80:80 ghcr.io/librespeed/speedtest`
  - 或 PHP standalone 版（上传走 `backend/empty.php`，读取并丢弃 body，适合测上行）。
  - 拿到 URL（如 `https://speed.xxx`）后：
    1. 填入 设置→通用→测速上传地址；或
    2. 发我，我把该地址同时作为**上行端点**与**下行/节点候选**接入 `Services\SpeedTestService.cs`（`upEndpoints` 优先 + `Nodes` 候选）。
- **验收**：国内环境测速，**下行 + 上行均有真实 Mbps**，节点显示自建 LibreSpeed；无自建时上行如实显示"不可达"（不伪造）。

### 其它 v1.1 已实现/随手记录（供后续会话了解现状，避免重复）
- 桌面宠物已改为**纯 WPF 透明窗口**（`MiniHud.cs`）：7 款 Codex 风格像素小兽、呼吸/心搏/轨道光球动效；拖拽（像素→DIP）、双击=释放内存（可开关）、气泡显示 CPU/内存/GPU（可配置）。
- 设置窗口（`SettingsWindow.cs`）玻璃化：左侧导航 通用/防睡眠/桌面宠物/垃圾清理 四页；主题（深/浅/跟随系统）、主色（靛蓝/深海蓝/心动紫，前端下发 `accent`）、宠物风格、气泡显示项、卡片显隐、双击释放内存、开机管理员运行（计划任务 `CoreBeatElevated`）、首次提权自绘弹窗等，均持久化到 `%LOCALAPPDATA%\CoreBeat\config.json`。
- 主界面 = 顶部 5 磁贴(固定 5 列，可选) + 下方 `auto-fit` 网格卡（整机负载已并入 + 垃圾清理/温度仪表盘/运行进程/磁盘网络/网速磁贴）；卡片显隐由通用勾选决定，布局随宽度自适应、内部各自滚动，无页面滚动条。**卡片顺序固定（已去掉拖拽）**。
- 垃圾清理（`Services\Cleaner.cs`）：安全/需管理员/谨慎三级目标 + 分组可收起列表 + 扫描/清理进度动画 + 完成浮窗；主界面有「垃圾清理」快捷卡（显示真实可释放量、一键清理带动画、深层清理跳设置页）。
- 内存释放：`Services\MemoryRelease.cs`（EmptyWorkingSet 式），双击宠物触发。
- 网络测速：`Services\SpeedTestService.cs` + 「网速」磁贴 ⚡ 测速弹窗（环形仪表、实时下/上行/延迟/抖动/丢包）。
- 开机自启：HKCU Run + `-autostart` 后台驻留；开机管理员运行走 schtasks `/RL HIGHEST`。
- 注意：提权实例会锁 exe，普通 shell 无法结束（需用户托盘退出后再 build）。

