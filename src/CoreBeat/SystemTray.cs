using System.Drawing;
using System.IO;
using System.Windows;
using CoreBeat.Services;
using WF = System.Windows.Forms;

namespace CoreBeat;

/// <summary>
/// 系统托盘：左键单击=唤出主界面；右键=自定义深色菜单
/// （防睡眠档位/倒计时、悬浮球、开机自启、回放、提权重启、退出）。
/// </summary>
public sealed class SystemTray : IDisposable
{
    private readonly WF.NotifyIcon _icon;
    private readonly MainWindow _window;
    private readonly TrayMenuWindow _menu = new();
    private MiniHud? _hud;
    private SettingsWindow? _settingsWin;

    public SystemTray(MainWindow window)
    {
        _window = window;
        using var stream = LoadIconStream();
        _icon = new WF.NotifyIcon
        {
            Icon = new Icon(stream),
            Text = "芯跳 CoreBeat",
            Visible = true,
        };
        // 单击（左键）直接开主界面；双击兼容
        _icon.MouseClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) _window.ShowFromTray(); };
        _icon.DoubleClick += (_, _) => _window.ShowFromTray();
        // 右键 → 自定义菜单（用真实屏幕鼠标坐标，避免 e.X/Y 是图标相对值导致菜单位置错乱）
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Right)
            {
                _menu.SetItems(BuildEntries());
                var screen = WF.Control.MousePosition;
                _menu.ShowAt(new System.Windows.Point(screen.X, screen.Y));
            }
        };

        // 上次退出时宠物可见 → 启动即恢复显示
        if (App.HudVisiblePersisted) EnsureHud().Show2();

        // 配置了更新源 → 启动约 8s 后静默检查一次，有新版弹托盘气泡
        if (!string.IsNullOrEmpty(App.UpdateManifestUrl))
        {
            _updTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _updTimer.Tick += (_, _) => { _updTimer.Stop(); CheckUpdateNow(true); };
            _updTimer.Start();
        }
    }

    private System.Windows.Threading.DispatcherTimer? _updTimer;

    private static Stream LoadIconStream()
    {
        var info = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/CoreBeat.ico"));
        if (info is null) throw new InvalidOperationException("缺少内嵌图标资源 Assets/CoreBeat.ico");
        return info.Stream;
    }

    /// <summary>主界面"深层清理"入口：打开设置窗口并定位到垃圾清理页。</summary>
    public void OpenCleanSettings() => OpenSettingsDialog(false, true);
    /// <summary>首启引导用：显示/隐藏宠物并持久化。</summary>
    public void SetPetShown(bool on) => ShowHud(on);

    private MiniHud EnsureHud()
    {
        if (_hud == null)
        {
            _hud = new MiniHud();
            _hud.SetOpacity(App.HudOpacityPersisted); // 恢复上次透明度
        }
        return _hud;
    }

    /// <summary>显示/隐藏宠物并持久化该选择。</summary>
    private void ShowHud(bool show)
    {
        var h = EnsureHud();
        if (show) { if (!h.Visible) h.Show2(); } else h.Hide();
        App.SetHudVisiblePersisted(show);
    }
    private void ToggleHud()
    {
        var h = EnsureHud();
        ShowHud(!h.Visible);
    }

    /// <summary>打开软件设置窗口；petPage/cleanPage 用于定位到对应页。</summary>
    private void OpenSettingsDialog(bool petPage, bool cleanPage = false)
    {
        try
        {
            try { _settingsWin?.Close(); } catch { }
            _settingsWin = new SettingsWindow(new SettingsContext
            {
                GetHudVisible = () => _hud?.Visible == true,
                SetHudVisible = ShowHud,
                GetHudOpacity = () => _hud?.OpacityNow ?? App.HudOpacityPersisted,
                SetHudOpacity = v => { App.SetHudOpacityPersisted(v); EnsureHud().SetOpacity(v); },
                GetHudMetrics = () => App.GetHudMetrics(),
                SetHudMetric = (k, on) => { App.ToggleHudMetric(k, on); _hud?.ApplyConfig(); },
                GetPetStyle = () => App.PetStyle,
                SetPetStyle = style => { App.SetPetStyle(style); _hud?.SetStyle(style); },
                GetTheme = () => App.Theme,
                SetTheme = App.SetTheme,
                GetReleaseOnDouble = () => App.ReleaseOnDouble,
                SetReleaseOnDouble = App.SetReleaseOnDouble,
                GetCardShown = App.IsCardShown,
                SetCardShown = App.ToggleCardShown,
                GetAdminRun = () => App.AdminRunEnabled,
                SetAdminRun = App.SetAdminRun,
                GetNetTestUrl = () => App.NetTestUploadUrl,
                SetNetTestUrl = App.SetNetTestUrl,
            }, petPage, cleanPage);
            _settingsWin.Show();
        }
        catch (Exception ex)
        {
            App.Log("打开设置窗口失败: " + ex.Message);
        }
    }

    private System.Collections.Generic.IEnumerable<TrayEntry> BuildEntries()
    {
        var items = new System.Collections.Generic.List<TrayEntry>();
        var st = App.Awake.State;

        items.Add(new TrayEntry { Kind = TrayItemKind.Header, Text = "芯跳 CoreBeat" });
        items.Add(new TrayEntry { Kind = TrayItemKind.Item, Text = "显示主窗口", Click = () => _window.ShowFromTray() });
        items.Add(new TrayEntry { Kind = TrayItemKind.Item, Text = "隐藏主窗口", Click = () => _window.Hide() });
        items.Add(new TrayEntry { Kind = TrayItemKind.Separator });
        items.Add(new TrayEntry { Kind = TrayItemKind.Item, Text = "回放启动动画", Click = () => _window.ReplaySplash() });
        items.Add(new TrayEntry { Kind = TrayItemKind.Item, Text = "以管理员身份重启", Sub = "解锁 NVMe 温度", Click = RelaunchElevated });
        items.Add(new TrayEntry { Kind = TrayItemKind.Separator });

        int? minutes = st.RemainSec is { } s2 && st.Level != AwakeLevel.Off ? (int)Math.Round(s2 / 60.0) : null;
        string remain = st.RemainSec is { } r && st.Level != AwakeLevel.Off ? $"剩余 {r / 60} 分" : "";
        items.Add(new TrayEntry
        {
            Kind = TrayItemKind.Group,
            Text = "防睡眠",
            Sub = $"{st.Label} · {remain}",
            IsOpen = false,
            Children = new System.Collections.Generic.List<TrayEntry>
            {
                LevelEntry(AwakeLevel.Off, "关闭", st.Level),
                LevelEntry(AwakeLevel.PreventSleep, "① 仅防睡", st.Level),
                LevelEntry(AwakeLevel.PreventDisplay, "② 防睡 + 防熄屏", st.Level),
                LevelEntry(AwakeLevel.AntiLock, "③ 加强防锁", st.Level),
                new TrayEntry { Kind = TrayItemKind.Separator },
                TimeoutEntry(null, "倒计时：不限时", minutes),
                TimeoutEntry(30, "倒计时：30 分钟", minutes),
                TimeoutEntry(60, "倒计时：1 小时", minutes),
                TimeoutEntry(480, "倒计时：整夜（8 小时）", minutes),
            },
        });

        items.Add(new TrayEntry
        {
            Kind = TrayItemKind.Group,
            Text = "桌面宠物",
            IsOpen = false,
            Children = new System.Collections.Generic.List<TrayEntry>
            {
                new TrayEntry
                {
                    Kind = TrayItemKind.Check,
                    Text = "显示宠物",
                    Checked = _hud?.Visible == true,
                    Click = ToggleHud,
                },
                new TrayEntry
                {
                    Kind = TrayItemKind.Item,
                    Text = "宠物设置…",
                    Sub = "透明度 / 气泡显示项",
                    Click = () => OpenSettingsDialog(true),
                },
            },
        });
        items.Add(new TrayEntry { Kind = TrayItemKind.Separator });

        items.Add(new TrayEntry
        {
            Kind = TrayItemKind.Check,
            Text = "开机自启（默认关）",
            Checked = App.AutoStartEnabled,
            Click = () => App.SetAutoStart(!App.AutoStartEnabled),
        });
        items.Add(new TrayEntry { Kind = TrayItemKind.Separator });
        items.Add(new TrayEntry
        {
            Kind = TrayItemKind.Item,
            Text = "设置…",
            Sub = "防睡眠 / 开机自启 / 桌面宠物",
            Click = () => OpenSettingsDialog(false),
        });
        items.Add(new TrayEntry
        {
            Kind = TrayItemKind.Item,
            Text = "垃圾清理…",
            Sub = "临时 / 更新缓存 / 回收站",
            Click = () => OpenSettingsDialog(false, true),
        });
        items.Add(new TrayEntry
        {
            Kind = TrayItemKind.Item,
            Text = "一键清理临时文件",
            Sub = "占用中自动跳过",
            Click = QuickCleanNow,
        });
        items.Add(new TrayEntry
        {
            Kind = TrayItemKind.Item,
            Text = "检查更新…",
            Sub = string.IsNullOrEmpty(App.UpdateManifestUrl) ? "未配置更新源" : App.Version,
            Click = () => CheckUpdateNow(false),
        });
        items.Add(new TrayEntry { Kind = TrayItemKind.Separator });
        items.Add(new TrayEntry { Kind = TrayItemKind.Danger, Text = "退出", Click = App.RequestExit });
        return items;
    }

    /// <summary>检查更新：优先 update.json；未配置则走 GitHub Releases（零服务器）；有新版提示下载。</summary>
    private async void CheckUpdateNow(bool silent)
    {
        try
        {
            string ver = "", down = "", notes = "";
            using var http = new System.Net.Http.HttpClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CoreBeat/" + App.Version);

            if (!string.IsNullOrWhiteSpace(App.UpdateManifestUrl))
            {
                string json = await http.GetStringAsync(App.UpdateManifestUrl);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var r = doc.RootElement;
                ver = r.TryGetProperty("version", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString()! : "";
                down = r.TryGetProperty("url", out var u) && u.ValueKind == System.Text.Json.JsonValueKind.String ? u.GetString()! : "";
                notes = r.TryGetProperty("notes", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String ? n.GetString()! : "";
            }
            else if (!string.IsNullOrWhiteSpace(App.UpdateRepo))
            {
                string api = "https://api.github.com/repos/" + App.UpdateRepo + "/releases/latest";
                string json = await http.GetStringAsync(api);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var r = doc.RootElement;
                if (r.TryGetProperty("tag_name", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String)
                    ver = (t.GetString() ?? "").TrimStart('v', 'V');
                if (r.TryGetProperty("body", out var b) && b.ValueKind == System.Text.Json.JsonValueKind.String)
                    notes = b.GetString() ?? "";
                if (r.TryGetProperty("assets", out var as_) && as_.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    string? best = null;
                    foreach (var a in as_.EnumerateArray())
                    {
                        if (a.TryGetProperty("name", out var nm) && a.TryGetProperty("browser_download_url", out var du) && du.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            string name = nm.GetString() ?? "";
                            if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase)) { best = du.GetString(); break; }
                            best ??= du.GetString();
                        }
                    }
                    down = best ?? "";
                }
            }
            else
            {
                if (!silent) WF.MessageBox.Show("尚未配置更新源。\n可在 App.UpdateManifestUrl 填 update.json 地址，或在 App.UpdateRepo 填 GitHub 仓库（如 user/repo）。", "芯跳 CoreBeat", WF.MessageBoxButtons.OK, WF.MessageBoxIcon.Information);
                return;
            }

            if (string.IsNullOrEmpty(ver)) { if (!silent) WF.MessageBox.Show("更新清单缺少版本信息。", "芯跳 CoreBeat"); return; }

            if (App.CompareVersions(ver, App.Version) > 0)
            {
                if (silent) { _icon.BalloonTipTitle = "芯跳 CoreBeat 有新版本"; _icon.BalloonTipText = "v" + ver + " 已发布。"; _icon.ShowBalloonTip(5000); return; }
                string msg = $"发现新版本 v{ver}（当前 v{App.Version}）\n\n" + (string.IsNullOrEmpty(notes) ? "" : notes + "\n\n") + "是否打开下载页进行更新？";
                var go = WF.MessageBox.Show(msg, "芯跳 CoreBeat 更新", WF.MessageBoxButtons.YesNo, WF.MessageBoxIcon.Information);
                if (go == WF.DialogResult.Yes && !string.IsNullOrEmpty(down))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(down) { UseShellExecute = true });
            }
            else if (!silent)
            {
                WF.MessageBox.Show($"已是最新版本 v{App.Version}。", "芯跳 CoreBeat 更新", WF.MessageBoxButtons.OK, WF.MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            if (!silent) WF.MessageBox.Show("检查更新失败：" + ex.Message, "芯跳 CoreBeat 更新", WF.MessageBoxButtons.OK, WF.MessageBoxIcon.Warning);
            else App.Log("静默检查更新失败: " + ex.Message);
        }
    }

    /// <summary>托盘一键：快速清理用户/系统临时（不做删除确认）。</summary>
    private void QuickCleanNow()
    {
        try
        {
            var r = JunkCleaner.QuickClean();
            App.AddCleanFreed(r.Freed);
            string mb = r.Freed >= 1 << 20 ? (r.Freed / (double)(1 << 20)).ToString("0.0") + " MB" : (r.Freed / 1024.0).ToString("0") + " KB";
            WF.MessageBox.Show($"一键清理完成：释放 {mb}\n成功 {r.Ok} 项 · 跳过 {r.Fail} 项\n累计已释放 {App.CleanTotal / (1 << 20):0.0} MB", "芯跳 CoreBeat", WF.MessageBoxButtons.OK, WF.MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            App.Log("一键清理失败: " + ex.Message);
        }
    }

    private TrayEntry LevelEntry(AwakeLevel level, string text, AwakeLevel cur) => new()
    {
        Kind = TrayItemKind.Check,
        Text = text,
        Checked = cur == level,
        Click = () => App.Awake.SetLevel(level),
    };

    private TrayEntry TimeoutEntry(int? minutes, string text, int? cur) => new()
    {
        Kind = TrayItemKind.Check,
        Text = text,
        Checked = (minutes == null && cur == null) || (minutes != null && cur == minutes),
        Click = () => App.Awake.SetTimeoutMinutes(minutes),
    };

    private static void RelaunchElevated()
    {
        if (App.IsElevated)
        {
            WF.MessageBox.Show("当前已以管理员身份运行，温度传感器应可全部读取。", "芯跳 CoreBeat");
            return;
        }
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
            App.RequestExit();
        }
        catch (Exception ex)
        {
            App.Log("提权重启被取消或失败: " + ex.Message);
        }
    }

    /// <summary>防睡状态变化：同步托盘悬停文案（UI 线程调用）。</summary>
    public void NotifyAwake(AwakeState state)
    {
        _icon.Text = state.Level == AwakeLevel.Off
            ? "芯跳 CoreBeat"
            : $"芯跳 CoreBeat · {state.Label}";
    }

    public void Dispose()
    {
        try { _settingsWin?.Close(); } catch { }
        _menu.Dispose();
        _hud?.Dispose();
        _hud = null;
        if (_icon.Visible) _icon.Visible = false;
        _icon.Dispose();
        App.Log("托盘已释放");
    }
}
