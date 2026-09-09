using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using CoreBeat.Services;
using Microsoft.Web.WebView2.Core;

namespace CoreBeat;

/// <summary>
/// 主窗口：无边框 + DWM 圆角/暗色亚克力背景，内容区完全由 WebView2 渲染。
/// 前端通过 chrome.webview.postMessage 以 JSON 请求原生命令（拖动/最小化/最大化/关闭）；
/// 原生每 1s 推送一次指标 JSON（t:"tick"），防睡状态变化即时推送（t:"awake"）。
/// </summary>
public partial class MainWindow : Window
{
    private bool _webReady;
    private System.Windows.Threading.DispatcherTimer? _cleanTimer;
    private bool _allowClose;
    private MetricsPusher? _pusher;
    private bool _disposed;
    // 手动拖拽状态（前端逐帧上报屏幕坐标）
    private double _dragStartX, _dragStartY, _dragWinLeft, _dragWinTop;
    private bool _dragging;

    /// <summary>WebView2 用户数据目录（避免每次启动重建浏览器配置）。</summary>
    private static string WebViewUserDataFolder =>
        Path.Combine(App.LogDirectory, "WebView2", "Default");

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosingWindow;
        StateChanged += (_, _) => App.Log($"主窗口状态: {WindowState}");
    }

    /// <summary>允许直接关闭（由退出流程设置）。</summary>
    internal bool AllowClose
    {
        get => _allowClose;
        set => _allowClose = value;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // 无边框窗口的 Win11 观感：圆角 + 沉浸深色 + 系统亚克力底（失败自动降级为纯深色，无碍）
        Native.Dwm.Apply(this);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: WebViewUserDataFolder);
            await HeartWeb.EnsureCoreWebView2Async(environment);
            ConfigureWebView();
            _webReady = true;

            var page = Path.Combine(AppContext.BaseDirectory, "Web", "splash.html");
            HeartWeb.Source = new Uri(page);
            App.Log("WebView2 就绪，加载 " + page);

            // M2：启动 1s 采样推送 + 防睡状态订阅（在 WebView 就绪后，避免丢首包）
            StartServices();
        }
        catch (Exception ex)
        {
            App.Log("WebView2 初始化失败: " + ex);
            const string wvUrl = "https://developer.microsoft.com/microsoft-edge/webview2/#download-section";
            var choose = System.Windows.MessageBox.Show(
                "未检测到可用的 WebView2 运行时（界面渲染必需）。\n\n" +
                ex.Message + "\n\n是否打开 Microsoft 官方下载页获取 Evergreen 运行时？",
                "芯跳 CoreBeat",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (choose == MessageBoxResult.Yes)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(wvUrl) { UseShellExecute = true }); }
                catch { App.Log("打开 WebView2 下载页失败"); }
            }
            App.RequestExit();
        }
    }

    private void StartServices()
    {
        App.Awake.Changed += OnAwakeChanged;
        App.RegisterSink(SinkJson);

        _pusher = new MetricsPusher(json => App.Broadcast(json));
        _cleanTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _cleanTimer.Tick += (_, _) => PushCleanInfo(); // 卡片上的可清理量周期性刷新
        _cleanTimer.Start();
        App.Log("M2 采样推送已启动（1s）");
    }

    /// <summary>本窗口的数据接收器：采样线程 → UI 线程 → WebView。</summary>
    private void SinkJson(string json)
    {
        try
        {
            Dispatcher.Invoke(() => { SendJson(json); _pusher?.NotifyPushed(); });
        }
        catch { /* 窗口关闭后丢弃 */ }
    }

    private void OnAwakeChanged(AwakeState state)
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                SendJson(JsonSerializer.Serialize(new
                {
                    t = "awake",
                    level = (int)state.Level,
                    label = state.Label,
                    remainSec = state.RemainSec,
                }));
                App.Tray?.NotifyAwake(state);
            });
        }
        catch { /* 关闭竞态忽略 */ }
    }

    private void ConfigureWebView()
    {
        var web = HeartWeb.CoreWebView2;
        web.Settings.AreDefaultContextMenusEnabled = false;
        web.Settings.IsStatusBarEnabled = false;
        web.Settings.AreDevToolsEnabled = true;   // 开发期可按 F12 调试前端
        web.Settings.IsZoomControlEnabled = false;
        web.WebMessageReceived += OnWebMessage;
        web.NavigationCompleted += (_, _) =>
        {
            try
            {
                SendJson(JsonSerializer.Serialize(new { t = "theme", mode = App.Theme }));
                SendJson(JsonSerializer.Serialize(new { t = "cards", show = App.GetShownCards() }));
                PushCleanInfo();
            }
            catch { }
        };
    }

    /// <summary>前端 -> 原生：JSON 命令通道（协议见 Web/js/app.js）。</summary>
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw = e.WebMessageAsJson ?? "<null>";
        try
        {
            if (raw.Length > 160) App.Log("前端原始报文: " + raw[..160]);
            else App.Log("前端原始报文: " + raw);

            // 通用解包：兼容 WebView2 返回【对象 JSON】或【JSON 字符串】两种形态
            JsonElement root = ResolveRoot(raw, out var rootDoc);
            using (rootDoc)
            {
                if (root.ValueKind != JsonValueKind.Object) return;
                if (!root.TryGetProperty("cmd", out var cmdProp)) return;
                string cmd = cmdProp.ValueKind == JsonValueKind.String ? cmdProp.GetString() ?? string.Empty : string.Empty;

                if (cmd is not ("ping" or "jslog")) App.Log("前端命令: " + cmd);

                if (cmd == "accent" && root.TryGetProperty("hex", out var hx) && hx.ValueKind == JsonValueKind.String)
                {
                    App.SetAccent(hx.GetString()!);
                    return;
                }

                ExecuteCommand(root, cmd);
            }
        }
        catch (Exception ex)
        {
            App.Log("前端命令处理失败: " + ex.Message + "\n  RAW=" + (raw.Length > 300 ? raw[..300] : raw));
        }
    }

    /// <summary>Unwrap：若报文是 JSON 字符串且内容为对象，则解析其内层对象。</summary>
    private static JsonElement ResolveRoot(string raw, out JsonDocument holder)
    {
        holder = JsonDocument.Parse(raw);
        var root = holder.RootElement;
        if (root.ValueKind == JsonValueKind.String)
        {
            string inner = root.GetString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(inner) && inner.TrimStart().StartsWith("{"))
            {
                var innerDoc = JsonDocument.Parse(inner);
                holder.Dispose();          // 放弃外层（字符串）
                holder = innerDoc;         // 持有内层
                return holder.RootElement;
            }
        }
        return root;
    }

    private void ExecuteCommand(JsonElement root, string cmd)
    {
        switch (cmd)
        {
                case "dragBegin":
                    _dragStartX = GetNum(root, "x", 0);
                    _dragStartY = GetNum(root, "y", 0);
                    _dragWinLeft = Left;
                    _dragWinTop = Top;
                    _dragging = WindowState != WindowState.Maximized;
                    break;
                case "dragMove":
                    if (!_dragging) break;
                    Left = _dragWinLeft + (GetNum(root, "x", _dragStartX) - _dragStartX);
                    Top = _dragWinTop + (GetNum(root, "y", _dragStartY) - _dragStartY);
                    break;
                case "dragEnd":
                    _dragging = false;
                    break;
                case "minimize":
                    WindowState = WindowState.Minimized;
                    break;
                case "maximize":
                    ToggleMaximize();
                    break;
                case "close":
                    // 关闭 = 收进托盘
                    Hide();
                    break;
                case "quit":
                    App.RequestExit();
                    break;
                case "ping":
                    App.Log("前端 ping 到达");
                    SendJson(JsonSerializer.Serialize(new
                    {
                        cmd = "pong",
                        version = "0.5.0",
                        app = "芯跳 CoreBeat",
                        mode = "real",
                    }));
                    break;
                case "jslog":
                    // 前端诊断回传（错误/心跳），写入本地日志
                    if (root.TryGetProperty("text", out var logText))
                        App.Log("前端: " + (logText.ValueKind == JsonValueKind.String
                            ? logText.GetString()
                            : logText.GetRawText()));
                    break;
                case "detail":
                    HandleDetail(root);
                    break;
                case "kill":
                    HandleKill(root);
                    break;
                case "history":
                    SendJson(_pusher?.HistoryJson() ?? "{\"t\":\"history\",\"pts\":[]}");
                    break;
                case "setThemeMode":
                    if (root.TryGetProperty("mode", out var tm) && tm.ValueKind == JsonValueKind.String)
                        App.SetTheme(tm.GetString()!);
                    break;
                case "setOption":
                    if (root.TryGetProperty("key", out var ok) && ok.ValueKind == JsonValueKind.String &&
                        root.TryGetProperty("on", out var ov) && (ov.ValueKind is JsonValueKind.True or JsonValueKind.False))
                    {
                        bool on = ov.GetBoolean();
                        switch (ok.GetString())
                        {
                            case "autostart": App.SetAutoStart(on); break;
                            case "adminRun": App.SetAdminRun(on); break;
                            case "petShow": App.Tray?.SetPetShown(on); break;
                        }
                    }
                    break;
                case "cleanQuick":
                    RunCleanQuick();
                    break;
                case "openCleanSettings":
                    System.Windows.Application.Current?.Dispatcher.Invoke(() => App.Tray?.OpenCleanSettings());
                    break;
                case "speedtest":
                    StartSpeedTest();
                    break;
            }
        }

    private bool _speedBusy;
    private void StartSpeedTest()
    {
        if (_speedBusy) return;
        _speedBusy = true;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await CoreBeat.Services.SpeedTest.RunAsync((phase, val, text) =>
                    Dispatcher.Invoke(() =>
                    {
                        SendJson(JsonSerializer.Serialize(new { t = "st", phase, val, text }));
                        if (phase == "done") _speedBusy = false;
                    }));
            }
            catch { _speedBusy = false; }
        });
    }

    /// <summary>主界面卡片"一键清理"：后台清安全级，完成后推送剩余量与结果。</summary>
    private void RunCleanQuick()
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            (long Freed, int Ok, int Fail) r;
            try { r = CoreBeat.Services.JunkCleaner.QuickCleanSafe(); }
            catch (Exception ex) { App.Log("快捷清理失败: " + ex.Message); r = (0, 0, 1); }
            Dispatcher.Invoke(() =>
            {
                SendJson(JsonSerializer.Serialize(new { t = "cleanDone", freed = r.Freed, ok = r.Ok, fail = r.Fail }));
                PushCleanInfo();
            });
        });
    }

    /// <summary>后台扫描"安全级可清理总量"，推给主界面卡片。</summary>
    private void PushCleanInfo()
    {
        if (!_webReady) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var s = CoreBeat.Services.JunkCleaner.SafeSummary();
                Dispatcher.Invoke(() => SendJson(JsonSerializer.Serialize(new { t = "cleanInfo", total = s.Total, files = s.Files })));
            }
            catch { }
        });
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    /* ---------------- M3：进程详情 / 一键结束 ---------------- */

    private static int? TryGetPid(JsonElement root)
    {
        if (root.TryGetProperty("pid", out var pidEl) &&
            pidEl.ValueKind == JsonValueKind.Number &&
            pidEl.TryGetInt32(out int pid))
            return pid;
        return null;
    }

    private static double GetNum(JsonElement root, string name, double fallback)
    {
        if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number)
        {
            if (el.TryGetDouble(out double d)) return d;
            if (el.TryGetInt32(out int i)) return i;
        }
        return fallback;
    }

    private void HandleDetail(JsonElement root)
    {
        int? pid = TryGetPid(root);
        if (pid is null) return;
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid.Value);
            string? path = null, start = null, name = null;
            int threads = 0;
            double memMB = 0;
            try { path = proc.MainModule?.FileName; } catch { }
            try { start = proc.StartTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"); } catch { }
            try { threads = proc.Threads.Count; } catch { }
            try { memMB = proc.WorkingSet64 / 1048576.0; } catch { }
            try { name = proc.ProcessName; } catch { name = $"PID {pid}"; }

            SendJson(JsonSerializer.Serialize(new
            {
                t = "detail",
                pid = pid.Value,
                name,
                path,
                startUtc = start,
                threads,
                memMB = Math.Round(memMB, 1),
            }));
        }
        catch
        {
            SendJson(JsonSerializer.Serialize(new { t = "detail", pid = pid.Value, error = "进程不存在或已退出" }));
        }
    }

    private void HandleKill(JsonElement root)
    {
        // 支持单 pid 或 pids 数组（结束整个软件的多个进程）
        var pids = new List<int>();
        if (root.TryGetProperty("pids", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                int v;
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out v)) pids.Add(v);
            }
        }
        else if (TryGetPid(root) is { } one)
        {
            pids.Add(one);
        }
        if (pids.Count == 0) return;

        int ok = 0, denied = 0, gone = 0;
        foreach (var pid in pids)
        {
            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById(pid);
                proc.Kill(entireProcessTree: true);
                ok++;
                App.Log($"已请求结束进程 pid={pid}");
            }
            catch (System.ComponentModel.Win32Exception wex) when (wex.NativeErrorCode == 5)
            {
                denied++;
            }
            catch (InvalidOperationException)
            {
                gone++;   // 已退出视为达成
            }
            catch (Exception ex)
            {
                App.Log($"结束进程 pid={pid} 失败: {ex.Message}");
            }
        }

        int effective = ok + gone;
        if (effective > 0 && denied == 0)
        {
            SendJson(JsonSerializer.Serialize(new
            {
                t = "killResult",
                ok = true,
                msg = pids.Count == 1 ? "已结束进程" : $"已结束 {effective}/{pids.Count} 个进程",
            }));
        }
        else if (effective > 0)
        {
            SendJson(JsonSerializer.Serialize(new
            {
                t = "killResult",
                ok = true,
                needElevated = denied > 0,
                msg = $"已结束 {effective}/{pids.Count} 个；{denied} 个需管理员权限",
            }));
        }
        else if (denied > 0)
        {
            SendJson(JsonSerializer.Serialize(new
            {
                t = "killResult",
                ok = false,
                needElevated = true,
                msg = "权限不足：该系统/管理员进程需以管理员身份运行本工具后再试",
            }));
        }
        else
        {
            SendJson(JsonSerializer.Serialize(new
            {
                t = "killResult",
                ok = true,
                msg = "进程均已退出",
            }));
        }
    }

    /// <summary>
    /// 原生 -> 前端：注入 JSON 到页面回调。
    /// 说明：经实测 PostWebMessageAsJson 在本环境(WPF WebView2 152)不投递，ExecuteScriptAsync
    /// 注入 window.cbOnNative 稳定可达（页面同时保留 message 监听作兼容双通道）。
    /// </summary>
    private void SendJson(string json)
    {
        if (!_webReady) return;
        // 安全阀：超大注入串可能导致静默失败（表现为页面不更新），直接丢弃并提示
        if (json.Length > 2_000_000)
        {
            App.Log($"丢弃超大前端消息 {json.Length} 字符");
            return;
        }
        try
        {
            // json 为合法 JS 对象字面量，直接作为实参
            HeartWeb.ExecuteScriptAsync($"window.cbOnNative && window.cbOnNative({json});");
        }
        catch (Exception ex)
        {
            App.Log("发送前端消息失败: " + ex);
        }
    }

    private void OnClosingWindow(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        // 常规关闭 → 隐藏到托盘，进程驻留
        e.Cancel = true;
        Hide();
        App.Log("主窗口隐藏到托盘");
    }

    /// <summary>真正退出前的清理（幂等）。</summary>
    internal void ShutdownServices()
    {
        if (_disposed) return;
        _disposed = true;
        App.Awake.Changed -= OnAwakeChanged;
        App.UnregisterSink(SinkJson);
        _pusher?.Dispose();
        _pusher = null;
        App.Log("M2 采样推送已停止");
    }

    /// <summary>从托盘唤出主窗口。</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;   // 让窗口浮到最前，随后取消置顶
        Topmost = false;
    }

    /// <summary>托盘“回放启动动画”：重载 WebView2 页面。</summary>
    public void ReplaySplash()
    {
        if (_webReady)
        {
            HeartWeb.Reload();
            App.Log("回放启动动画");
        }
    }
}
