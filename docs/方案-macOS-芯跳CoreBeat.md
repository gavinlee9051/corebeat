# 芯跳 CoreBeat · macOS 版开发方案 v1.0（已确认）

> 定位：与 Windows 版同名同品牌、功能镜像对齐的 macOS 原生实现。
> 名字：芯跳 CoreBeat（品牌一致）。
> 目标平台：macOS 14+（Sonoma 及以上，兼顾 Sequoia）。
> 目标硬件：Apple Silicon（M 系列为主），兼顾 Intel Mac。
> 技术栈（已确认）：Swift + SwiftUI 原生；Swift Charts 绘图；菜单栏常驻 + Popover。
> 说明：曾评估 .NET MAUI 以共享 C# 逻辑，因 UI 质感与动效上限明显低于原生，已否决；本版与 Windows 版为两套独立原生代码库，零代码共享。
> 姊妹文档：Windows 版方案见「方案-Windows-芯跳CoreBeat.md」。

## 1. 功能对齐（与 Windows 版镜像）
| Windows 能力 | macOS 对应 |
|---|---|
| 实时资源 + 温度总览 | 菜单栏 Popover 仪表 + 温度卡片 |
| 进程/软件列表 | NSRunningApplication + 原生图标；CPU/内存 1s 差分采样 |
| 防睡眠/防自动锁 | IOKit 电源断言（等效 caffeinate -di），闲置不触发自动锁屏 |
| 置顶迷你 HUD | 无边框置顶悬浮小窗，可拖拽/调透明 |
| 24h 历史留存 | 本地文件，回看趋势 |
| 告警 / 开机自启 | 通知中心告警 / LaunchAgent 登录项 |
| 小彩蛋 | 同款移植（见第 4 节） |

## 2. 温度与功耗读取（macOS 重点差异）
- Apple Silicon / Intel：温度经 SMC（System Management Controller）读取；受系统安全限制，通常需**一次性授权安装受信任提权助手**（SMAppService / LaunchDaemon 方式），提供明确安装引导与"一键卸载"。
- 无提权时的降级数据：CPU 热压力（thermal pressure，sysctl 可读）+ 功耗估算，仍可绘制负载波形。
- 同类做法参考：Stats、iStat Menus（均以提权助手读 SMC）。
- 降级原则：读不到即显示"—"并引导授权，其余功能不中断。

## 3. 防睡眠/防自动锁（诚实边界）
- IOPMAssertion 电源断言阻止空闲休眠与显示器关闭。
- 系统"闲置自动锁屏"随显示器/屏保不触发而自然被抑制。
- 手动锁屏（Ctrl+Cmd+Q / 触发角）无法拦截 —— 如实标注。

## 4. 小彩蛋（已确认加入，与 Windows 版一致）
- 主彩蛋「恋恋芯跳」：连点 5 下菜单栏/面板 Logo → 波形合成为跳动爱心，浮现随机暖心台词，约 25 秒自动还原。
- 副彩蛋「发不发烫」：温度卡连点 3 次 → 俏皮气泡；温度 >85℃ 换文案"这芯在发烧，建议去吹吹风～"。

## 5. UI 与动效
- 视觉：深色为主 + 跟随系统深浅色；macOS 材质（vibrancy / Sequoia Liquid Glass）玻璃感。
- 呈现入口：菜单栏图标（心跳会"跳"）+ Popover 主面板 + 可选悬浮 HUD。
- 招牌动效与 Windows 版同语言：心跳波形 / 呼吸鼓动 / 弹性入场 / 数字滚动；SwiftUI 弹簧动画 + Canvas 实现，原生 60fps。

## 6. 架构要点
- Swift Package / Xcode 工程；模块划分：SensorService / ProcessService / AwakeService / HistoryStore / UI。
- 采样与 UI 分离：DispatchSourceTimer + @Observable 状态流。
- 打包：开发阶段本地运行；分发需 Developer ID 签名 + 公证（notarization）说明；个人使用可免公证。
- 隐私：无网络上传，全本地；沙盒与提权助手边界在文档写清。

## 7. 里程碑
| 阶段 | 内容 |
|---|---|
| M1 骨架 | Xcode 工程、菜单栏图标、心跳启动动画 |
| M2 数据层 | SMC/提权助手、进程、电源断言联调 |
| M3 主界面 | Popover 仪表 + 波形 + 进程榜 |
| M4 打磨 | HUD、历史、告警、彩蛋、深浅色 |
| M5 交付 | Apple Silicon 真机验证、签名/公证指引、打包 |

## 8. 风险与对策
- Apple Silicon 温度读取需提权助手 → 明确安装引导与卸载路径；授权失败时降级为热压力曲线。
- 系统版本差异 → 以最低 macOS 14 兼容为基准。
- 公证需 Apple 开发者账号 → 提供"个人自用免公证"与"对外分发需签名公证"两条指引。

## 9. 跨平台决策记录
- Windows = C# .NET 8（WPF）+ WebView2；macOS = Swift + SwiftUI：两套独立原生代码库，视觉语言 / 数据口径 / 功能完全对齐。
- 若未来要求单代码库双平台，需降级 .NET MAUI，届时单独评审（当前已否决）。

## 10. 非目标（v2 候选）
远程监控、手机联动、多机汇总等（与 Windows 版一致）。

## 11. 需求记录
- 技术路线：确认采用 Swift + SwiftUI 原生（独立代码库）。
- 名称：芯跳 CoreBeat（与 Windows 版一致）。
- 小彩蛋：v1.0 已确认加入（见第 4 节）。
