using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CoreBeat.Services;
using Microsoft.Win32;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;
using Cursors = System.Windows.Input.Cursors;

namespace CoreBeat;

/// <summary>
/// 芯跳 CoreBeat 应用入口。
/// 生命周期约定：主窗口“关闭”= 隐藏到托盘；真正退出只能走托盘菜单/App.RequestExit()。
/// </summary>
public partial class App : System.Windows.Application
{
    /// <summary>主窗口（单实例）。</summary>
    public static MainWindow? RootWindow { get; private set; }

    /// <summary>系统托盘。</summary>
    public static SystemTray? Tray { get; private set; }

    /// <summary>防睡眠 / 防锁服务（托盘与前端共享；退出时还原系统设置）。</summary>
    public static AwakeService Awake { get; } = new();

    /// <summary>本地日志目录（%LOCALAPPDATA%\CoreBeat）。</summary>
    public static string LogDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoreBeat");

    /// <summary>是否已以管理员身份运行（解锁全部温度读取）。</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /* ---- Web 广播：主界面与迷你 HUD 都接收 1s 数据 ---- */
    private static readonly object SinkLock = new();
    private static readonly List<Action<string>> WebSinks = new();

    public static void RegisterSink(Action<string> sink)
    {
        lock (SinkLock) { WebSinks.Add(sink); }
    }

    public static void UnregisterSink(Action<string> sink)
    {
        lock (SinkLock) { WebSinks.Remove(sink); }
    }

    public static void Broadcast(string json)
    {
        List<Action<string>> copy;
        lock (SinkLock) { copy = WebSinks.ToList(); }
        foreach (var sink in copy)
        {
            try { sink(json); }
            catch { /* 单个接收方失败不影响其余 */ }
        }
    }

    /* ---- 开机自启（默认关；写 HKCU Run 键） ---- */
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "CoreBeat";

    public static bool AutoStartEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(RunValueName) is string s && s.Length > 0;
            }
            catch { return false; }
        }
    }

    public static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return;
            if (enable)
            {
                string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                if (exe.Length > 0) key.SetValue(RunValueName, $"\"{exe}\" -autostart");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
            Log($"开机自启 -> {(enable ? "开启" : "关闭")}");
        }
        catch (Exception ex)
        {
            Log("设置开机自启失败: " + ex.Message);
        }
    }

    /* ---- 桌面宠物气泡显示项（默认 CPU/内存/GPU，持久化到 config.json） ---- */
    private static HashSet<string> HudMetricSet = new() { "cpu", "mem", "gpu" };
    private static readonly object HudMetricLock = new();

    public static string[] GetHudMetrics()
    {
        lock (HudMetricLock) { return HudMetricSet.ToArray(); }
    }

    public static void ToggleHudMetric(string key, bool on)
    {
        lock (HudMetricLock)
        {
            if (on) HudMetricSet.Add(key); else HudMetricSet.Remove(key);
        }
        SaveConfig();
    }

    /* ---- 宠物外观持久化：是否显示 / 透明度 ---- */
    private static bool _hudVisible;
    private static double _hudOpacity = 0.92;
    private static bool _releaseOnDouble; // 双击宠物释放内存（默认关）

    public static bool ReleaseOnDouble => _releaseOnDouble;
    public static void SetReleaseOnDouble(bool enable)
    {
        if (_releaseOnDouble == enable) return;
        _releaseOnDouble = enable;
        SaveConfig();
    }

    /* ---- 版本与更新源（发布方替换 UpdateManifestUrl 为实际地址） ---- */
    public const string Version = "0.6.4";
    public const string UpdateManifestUrl = "";  // 路线B（自托管 update.json）：https://example.com/corebeat/update.json ；留空则走 UpdateRepo
    public const string UpdateRepo = "gavinlee9051/corebeat"; // 路线A：GitHub Releases，发布 Release 即触发更新

    /// <summary>比较版本号字符串（a>b>0, a==b==0, a&lt;b&lt;0）。</summary>
    public static int CompareVersions(string a, string b)
    {
        try
        {
            var pa = a.Trim().Split('.');
            var pb = b.Trim().Split('.');
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                int x = i < pa.Length && int.TryParse(pa[i], out var v) ? v : 0;
                int y = i < pb.Length && int.TryParse(pb[i], out var w) ? w : 0;
                if (x != y) return x.CompareTo(y);
            }
            return 0;
        }
        catch { return 0; }
    }
    private static string _netTestUp = ""; // 自定义测速上传地址（可选）
    public static string NetTestUploadUrl => _netTestUp;
    public static void SetNetTestUrl(string url) { url = url ?? ""; if (_netTestUp == url) return; _netTestUp = url.Trim(); SaveConfig(); }

    private static long _cleanTotal;
    public static long CleanTotal => _cleanTotal;
    public static void AddCleanFreed(long freed) { if (freed <= 0) return; _cleanTotal += freed; SaveConfig(); }

    private static bool _elevPrompted;
    public static bool ElevPrompted => _elevPrompted;
    public static void SetElevPrompted(bool v) { if (_elevPrompted == v) return; _elevPrompted = v; SaveConfig(); }

    private const string AdminTaskName = "CoreBeatElevated";
    public static bool AdminRunEnabled
    {
        get
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("schtasks", "/Query /TN \"" + AdminTaskName + "\" /F")
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(3000);
                return p is { ExitCode: 0 };
            }
            catch { return false; }
        }
    }
    public static void SetAdminRun(bool enable)
    {
        try
        {
            string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (exe.Length == 0) return;
            string args = enable
                ? "/Create /TN \"" + AdminTaskName + "\" /TR \"\\\"" + exe + "\\\" -autostart\" /SC ONLOGON /RL HIGHEST /F"
                : "/Delete /TN \"" + AdminTaskName + "\" /F";
            var psi = new System.Diagnostics.ProcessStartInfo("schtasks", args)
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(5000);
            Log("设置开机管理员运行(" + enable + "): exit=" + (p?.ExitCode.ToString() ?? "?"));
        }
        catch (Exception ex) { Log("设置开机管理员失败: " + ex.Message); }
    }

    /// <summary>以管理员身份重启当前程序（UAC），并退出当前实例。</summary>
    public static void ElevateAndRestart()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "CoreBeat.exe",
                Verb = "runas",
                UseShellExecute = true,
                Arguments = "-elevated",
            };
            System.Diagnostics.Process.Start(psi);
            RequestExit();
        }
        catch (Exception ex) { Log("提权重启被取消或失败: " + ex.Message); }
    }

    /// <summary>跟随 Windows 应用主题：真/假 = 浅色/深色。</summary>
    private static bool IsAppLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    /// <summary>自绘弹窗（跟随浅/深主题，与主界面风格一致）。</summary>
    private static bool? ShowElevationDialog()
    {
        bool light = IsAppLight();
        var accent = Color.FromRgb(107, 124, 255);
        bool? result = null;
        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Background = light ? new SolidColorBrush(Color.FromRgb(243, 246, 251)) : new System.Windows.Media.LinearGradientBrush(
                new System.Windows.Media.GradientStopCollection
                {
                    new System.Windows.Media.GradientStop(Color.FromRgb(20, 26, 46), 0),
                    new System.Windows.Media.GradientStop(Color.FromRgb(9, 13, 25), 1),
                }, new System.Windows.Point(0, 0), new System.Windows.Point(0, 1)),
            ShowInTaskbar = false,
            Width = 470,
            Height = 244,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        void CloseDialog() { win.Close(); }
        win.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { result = false; CloseDialog(); } };

        Color textMain = light ? Color.FromRgb(27, 35, 54) : Color.FromRgb(238, 243, 255);
        Color textDim = light ? Color.FromRgb(86, 99, 128) : Color.FromRgb(158, 174, 208);
        Color brandDim = light ? Color.FromRgb(96, 112, 146) : Color.FromRgb(160, 176, 216);
        Color secondaryTxt = light ? Color.FromRgb(43, 58, 92) : Color.FromRgb(212, 222, 242);

        System.Windows.Controls.TextBlock Make(string text, double size, System.Windows.Media.Color col, bool bold, bool wrap, System.Windows.Thickness m)
            => new() { Text = text, FontSize = size, Foreground = new System.Windows.Media.SolidColorBrush(col), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, Margin = m };

        var btn = new System.Windows.Controls.StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        System.Windows.Controls.Border MakeBtn(string text, bool primary, Action cb, System.Windows.Media.Color txtCol)
        {
            var b = new System.Windows.Controls.Border
            {
                Padding = new Thickness(19, 8, 19, 8),
                CornerRadius = new CornerRadius(10),
                Background = primary ? new System.Windows.Media.SolidColorBrush(accent) : new System.Windows.Media.SolidColorBrush(light ? Color.FromArgb(16, 20, 34, 64) : Color.FromArgb(22, 255, 255, 255)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(primary ? Colors.Transparent : Color.FromArgb(60, 120, 140, 172)),
                BorderThickness = new Thickness(primary ? 0 : 1),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 12, 0),
                Effect = primary ? new System.Windows.Media.Effects.DropShadowEffect { Color = accent, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.45 } : null,
            };
            b.Child = new System.Windows.Controls.TextBlock { Text = text, Foreground = new System.Windows.Media.SolidColorBrush(txtCol), FontSize = 12.5, FontWeight = FontWeights.SemiBold };
            b.MouseEnter += (_, _) => b.Background = primary
                ? new System.Windows.Media.SolidColorBrush(Color.FromRgb(126, 140, 255))
                : new System.Windows.Media.SolidColorBrush(light ? Color.FromArgb(32, 20, 34, 64) : Color.FromArgb(40, 255, 255, 255));
            b.MouseLeave += (_, _) => b.Background = primary
                ? new System.Windows.Media.SolidColorBrush(accent)
                : new System.Windows.Media.SolidColorBrush(light ? Color.FromArgb(16, 20, 34, 64) : Color.FromArgb(22, 255, 255, 255));
            b.MouseLeftButtonUp += (_, _) => cb();
            return b;
        }

        // 顶部品牌行
        var brandRow = new System.Windows.Controls.StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        brandRow.Children.Add(new System.Windows.Controls.Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), Background = new System.Windows.Media.SolidColorBrush(accent), Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center });
        brandRow.Children.Add(Make("芯跳 CoreBeat", 13, brandDim, true, false, new Thickness(0)));

        var sp = new System.Windows.Controls.StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        sp.Children.Add(brandRow);
        sp.Children.Add(Make("是否以管理员权限运行？", 15, textMain, true, false, new Thickness(0, 0, 0, 8)));
        sp.Children.Add(Make("将以管理员身份重启，以解锁显示 NVMe 温度、系统垃圾清理（需管理员/系统项）等完整功能。若选择暂不，可之后在托盘随时提权。", 11.5, textDim, false, true, new Thickness(0, 0, 0, 0)));
        btn.Children.Add(MakeBtn("立即提权", true, () => { result = true; CloseDialog(); }, System.Windows.Media.Colors.White));
        btn.Children.Add(MakeBtn("暂不", false, () => { result = false; CloseDialog(); }, secondaryTxt));
        sp.Children.Add(btn);

        var root = new System.Windows.Controls.Border
        {
            Background = light ? new System.Windows.Media.SolidColorBrush(Color.FromArgb(0, 0, 0, 0)) : Brushes.Transparent,
            BorderBrush = new System.Windows.Media.SolidColorBrush(light ? Color.FromArgb(50, 90, 110, 160) : Color.FromArgb(90, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Child = sp,
        };
        win.Content = new Grid { Margin = new Thickness(16), Children = { root } };
        win.ShowDialog();
        return result;
    }
    public static readonly string[] CardKeys = { "tileCpu", "tileMem", "tileTemp", "tileGpu", "tileUp", "ecg", "gauge", "procs", "disk", "clean" };
    private static HashSet<string> CardSet = new(CardKeys);
    private static readonly object CardLock = new();

    public static bool IsCardShown(string key)
    {
        lock (CardLock) { return CardSet.Contains(key); }
    }
    public static string[] GetShownCards()
    {
        lock (CardLock) { return CardSet.ToArray(); }
    }
    public static void ToggleCardShown(string key, bool on)
    {
        lock (CardLock)
        {
            if (on) CardSet.Add(key); else CardSet.Remove(key);
        }
        SaveConfig();
        try { Broadcast(JsonSerializer.Serialize(new { t = "cards", show = GetShownCards() })); } catch { }
    }

    public static bool HudVisiblePersisted => _hudVisible;
    public static void SetHudVisiblePersisted(bool visible)
    {
        if (_hudVisible == visible) return;
        _hudVisible = visible;
        SaveConfig();
    }

    public static double HudOpacityPersisted => _hudOpacity;
    public static void SetHudOpacityPersisted(double opacity)
    {
        opacity = Math.Clamp(opacity, 0.2, 1.0);
        if (Math.Abs(_hudOpacity - opacity) < 0.005) return;
        _hudOpacity = opacity;
        SaveConfig();
    }

    /* ---- 用户配置（主题 / 宠物风格），持久化到 %LOCALAPPDATA%\CoreBeat\config.json ---- */
    private const string ThemeDark = "dark";
    private const string ThemeLight = "light";
    private const string ThemeSystem = "system";

    private static string _theme = ThemeDark;
    private static int _petStyle;
    private static Color _accent = Color.FromRgb(107, 124, 255); // 默认靛蓝

    public static string Theme => _theme;
    public static int PetStyle => _petStyle;
    public static Color Accent => _accent;

    /// <summary>主界面主题色改变（前端下发），供设置窗口等同步。hex 如 "#2bf5c4"。</summary>
    public static event Action? AccentChanged;
    public static void SetAccent(string hex)
    {
        try
        {
            var c = (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            if (c.A != 255) c = Color.FromRgb(c.R, c.G, c.B);
            if (_accent == c) return;
            _accent = c;
            SaveConfig();
            AccentChanged?.Invoke();
            Log("主界面主题色 -> " + hex);
        }
        catch { Log("无效主题色: " + hex); }
    }

    private static string ConfigPath => Path.Combine(LogDirectory, "config.json");

    /// <summary>设置软件主题（dark/light/system），实时广播给主界面并持久化。</summary>
    public static void SetTheme(string mode)
    {
        mode = mode switch { ThemeLight => ThemeLight, ThemeSystem => ThemeSystem, _ => ThemeDark };
        if (_theme == mode) return;
        _theme = mode;
        SaveConfig();
        try { Broadcast(JsonSerializer.Serialize(new { t = "theme", mode })); } catch { }
        Log("软件主题 -> " + mode);
    }

    /// <summary>设置桌面宠物风格（索引），持久化；宠物窗口由 SystemTray 负责重建。</summary>
    public static void SetPetStyle(int style)
    {
        style = Math.Clamp(style, 0, 6);
        if (_petStyle == style) return;
        _petStyle = style;
        SaveConfig();
        Log("宠物风格 -> " + style);
    }

    private static void LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            var r = doc.RootElement;
            _theme = r.TryGetProperty("theme", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : ThemeDark;
            _petStyle = r.TryGetProperty("petStyle", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
            _petStyle = Math.Clamp(_petStyle, 0, 6);
            if (r.TryGetProperty("accent", out var a) && a.ValueKind == JsonValueKind.String)
            {
                try { _accent = (Color)System.Windows.Media.ColorConverter.ConvertFromString(a.GetString()!); } catch { }
            }
            // 迁移旧版青绿主色 → 默认靛蓝
            if (_accent == Color.FromRgb(43, 245, 196)) _accent = Color.FromRgb(107, 124, 255);
            if (r.TryGetProperty("hudVisible", out var hv) && hv.ValueKind is JsonValueKind.True or JsonValueKind.False)
                _hudVisible = hv.GetBoolean();
            if (r.TryGetProperty("hudOpacity", out var ho) && ho.ValueKind == JsonValueKind.Number)
                _hudOpacity = Math.Clamp(ho.GetDouble(), 0.2, 1.0);
            if (r.TryGetProperty("hudMetrics", out var hm) && hm.ValueKind == JsonValueKind.Array && hm.GetArrayLength() > 0)
            {
                lock (HudMetricLock)
                {
                    HudMetricSet = new HashSet<string>();
                    foreach (var k in hm.EnumerateArray())
                        if (k.ValueKind == JsonValueKind.String) HudMetricSet.Add(k.GetString()!);
                }
            }
            if (r.TryGetProperty("releaseOnDouble", out var rd) && rd.ValueKind is JsonValueKind.True or JsonValueKind.False)
                _releaseOnDouble = rd.GetBoolean();
            if (r.TryGetProperty("elevPrompted", out var ep) && ep.ValueKind is JsonValueKind.True or JsonValueKind.False)
                _elevPrompted = ep.GetBoolean();
            if (r.TryGetProperty("netTestUp", out var nu) && nu.ValueKind == JsonValueKind.String)
                _netTestUp = nu.GetString() ?? "";
            if (r.TryGetProperty("cleanTotal", out var ct) && ct.ValueKind == JsonValueKind.Number)
                _cleanTotal = ct.GetInt64();
            if (r.TryGetProperty("cards", out var cd) && cd.ValueKind == JsonValueKind.Array && cd.GetArrayLength() > 0)
            {
                lock (CardLock)
                {
                    CardSet = new HashSet<string>();
                    foreach (var k in cd.EnumerateArray())
                        if (k.ValueKind == JsonValueKind.String && System.Array.IndexOf(CardKeys, k.GetString()) >= 0)
                            CardSet.Add(k.GetString()!);
                }
            }
        }
        catch { /* 配置损坏则用默认值 */ }
    }

    private static System.Windows.Threading.DispatcherTimer? _saveTimer;
    /// <summary>配置写盘防抖：连续修改在 300ms 空闲后落盘一次（避免滑块拖动等高频全文件写）。</summary>
    private static void SaveConfig()
    {
        if (_saveTimer == null)
        {
            _saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); WriteConfigNow(); };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private static void WriteConfigNow()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            string[] metrics;
            lock (HudMetricLock) { metrics = HudMetricSet.ToArray(); }
            string[] cards;
            lock (CardLock) { cards = CardSet.ToArray(); }
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new
            {
                theme = _theme,
                petStyle = _petStyle,
                accent = _accent.ToString(),
                hudVisible = _hudVisible,
                hudOpacity = _hudOpacity,
                hudMetrics = metrics,
                releaseOnDouble = _releaseOnDouble,
                elevPrompted = _elevPrompted,
                netTestUp = _netTestUp,
                cleanTotal = _cleanTotal,
                cards = cards,
            }));
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        SessionEnding += OnSessionEnding;

        Log($"---- 芯跳 CoreBeat 启动 (elevated={IsElevated}, args={string.Join(' ', e.Args)}) ----");
        LoadConfig();
        SaveConfig(); // 确保 config.json 始终含完整字段

        bool autostart = Array.Exists(e.Args, a => a.Equals("-autostart", StringComparison.OrdinalIgnoreCase));

        RootWindow = new MainWindow();
        Tray = new SystemTray(RootWindow);
        if (autostart)
        {
            Log("开机自启：后台驻留（主界面隐藏，宠物按上次状态恢复）");
        }
        else
        {
            RootWindow.Show();

            // 首次启动提权提示（仅弹一次；延迟到主窗口显示后再弹，避免隐形模态锁住界面）
            if (!IsElevated && !_elevPrompted)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        bool? choose = ShowElevationDialog();
                        SetElevPrompted(true); // 作答后记录：弹窗异常/被关时下次仍可提示
                        if (choose == true) ElevateAndRestart();
                    }
                    catch (Exception ex) { Log("提权弹窗失败: " + ex.Message); }
                }));
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log("应用退出");
        if (_saveTimer != null) { _saveTimer.Stop(); }
        WriteConfigNow(); // 退出前落盘最后一次防抖结果
        RootWindow?.ShutdownServices();
        Tray?.Dispose();
        Tray = null;
        Awake.Restore();   // 退出即还原防睡眠设置
        base.OnExit(e);
    }

    private static void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        // 注销/关机前还原系统设置
        Awake.Restore();
    }

    /// <summary>唯一退出路径：托盘“退出”。</summary>
    public static void RequestExit()
    {
        if (RootWindow is not null)
        {
            RootWindow.AllowClose = true;
            RootWindow.ShutdownServices();
        }
        Current?.Shutdown();
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(
                Path.Combine(LogDirectory, "corebeat.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // 日志失败绝不影响主流程
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("未处理异常: " + e.Exception);
        System.Windows.MessageBox.Show(
            "芯跳 CoreBeat 遇到未处理的错误：\n\n" + e.Exception.Message + "\n\n详情已写入日志，应用即将退出。",
            "芯跳 CoreBeat",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
        Current?.Shutdown();
    }
}
