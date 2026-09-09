using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CoreBeat.Services;
using WF = System.Windows.Forms;
// 消除与 WinForms 隐式 using 的类型名歧义
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;
using CheckBox = System.Windows.Controls.CheckBox;
using RadioButton = System.Windows.Controls.RadioButton;
using Slider = System.Windows.Controls.Slider;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using Orientation = System.Windows.Controls.Orientation;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using TextBox = System.Windows.Controls.TextBox;

namespace CoreBeat;

/// <summary>设置窗口上下文：由 SystemTray 注入，避免设置窗口直接持有私有宠物实例。</summary>
public sealed class SettingsContext
{
    public Func<bool> GetHudVisible = () => false;
    public Action<bool> SetHudVisible = _ => { };
    public Func<double> GetHudOpacity = () => 0.92;
    public Action<double> SetHudOpacity = _ => { };
    public Func<IReadOnlyCollection<string>> GetHudMetrics = () => Array.Empty<string>();
    public Action<string, bool> SetHudMetric = (_, _) => { };
    public Func<int> GetPetStyle = () => 0;
    public Action<int> SetPetStyle = _ => { };
    public Func<string> GetTheme = () => "dark";
    public Action<string> SetTheme = _ => { };
    public Func<bool> GetReleaseOnDouble = () => false;
    public Action<bool> SetReleaseOnDouble = _ => { };
    public Func<string, bool> GetCardShown = _ => true;
    public Action<string, bool> SetCardShown = (_, _) => { };
    public Func<bool> GetAdminRun = () => false;
    public Action<bool> SetAdminRun = _ => { };
    public Func<string> GetNetTestUrl = () => "";
    public Action<string> SetNetTestUrl = _ => { };
}

/// <summary>
/// 软件级设置窗口（替代原来的托盘二级菜单）：
/// 左侧导航分页 —— 「通用」（开机自启 / 防睡眠）与「桌面宠物」（显示 / 透明度 / 气泡显示项）。
/// 全玻璃拟物外观，与托盘菜单同风格；窗口关闭即销毁，下次打开重新读取当前配置。
/// </summary>
public sealed class SettingsWindow
{
    private readonly SettingsContext _ctx;
    private readonly Window _win = null!; // 构造函数内赋值（先于事件 lambda 挂接，故用 null! 抑制告警）
    private readonly Border _navGeneral, _navAwake, _navPet;
    private Border _navClean = null!;
    private FrameworkElement _pageGeneral, _pageAwake, _pagePet, _pageClean;

    // 宠物页控件（在 BuildPetPage 中赋值）
    private CheckBox _petVisible = null!;
    private Slider _petOpacity = null!;
    private TextBlock _petOpacityText = null!;
    private CheckBox _mCpu = null!, _mMem = null!, _mGpu = null!;
    // 通用页控件（在 BuildGeneralPage 中赋值）
    private CheckBox _autoStart = null!;
    private CheckBox _adminRunCb = null!;
    private CheckBox _releaseCb = null!;
    private TextBox _netUpTb = null!;
    private TextBlock _awakeStatus = null!;
    private readonly RadioButton[] _levelRadios = new RadioButton[4];
    private readonly RadioButton[] _timeoutRadios = new RadioButton[4];
    private readonly System.Collections.Generic.List<(string Key, CheckBox Cb)> _cardCbs = new();
    // 清理页
    private readonly System.Collections.Generic.List<CleanItem> _cleanItems = new();
    private readonly System.Collections.Generic.HashSet<string> _cleanSel = new();
    private StackPanel _cleanHost = null!;
    private TextBlock _cleanStatusTxt = null!;
    private Border _cleanGoBtn = null!;
    private Border _cleanProgTrack = null!, _cleanProgFill = null!;
    private readonly System.Collections.Generic.HashSet<string> _cleanOpen = new();
    private bool _selSyncing;
    private bool _cleanBusy;
    // 完成提示浮窗
    private Border _toastBody = null!;
    private TextBlock _toastText = null!;
    private readonly System.Collections.Generic.List<(Border Btn, TextBlock Txt, int Idx)> _petButtons = new();
    private readonly RadioButton[] _themeRadios = new RadioButton[3];

    private bool _syncing; // 阻止程序化赋值触发事件造成回环

    private static bool _light; // 浅色主题（跟随软件主题）
    private static Color Accent => App.Accent;
    private static Color AccA(int a) => Color.FromArgb((byte)a, Accent.R, Accent.G, Accent.B); // 主色+透明度
    private static Color TextMain => _light ? Color.FromRgb(26, 35, 54) : Color.FromRgb(234, 242, 255);
    private static Color TextDim => _light ? Color.FromRgb(76, 88, 112) : Color.FromRgb(140, 158, 190);

    private Border _root = null!;
    private Grid _pageStack = null!;
    private int _currentPage;

    public SettingsWindow(SettingsContext ctx, bool openPetPage, bool openCleanPage = false)
    {
        _ctx = ctx;
        _light = ResolveLight(ctx.GetTheme());

        var root = new Border
        {
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(246, 26, 34, 54), 0),
                    new GradientStop(Color.FromArgb(252, 10, 16, 30), 1),
                },
                new System.Windows.Point(0, 0), new System.Windows.Point(0, 1)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
        };
        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });       // 标题栏
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 内容
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });       // 底部按钮

        // ---------- 标题栏 ----------
        var title = new Grid();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Accent), Margin = new Thickness(2, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center });
        titleRow.Children.Add(new TextBlock { Text = "芯跳 CoreBeat 设置", Foreground = new SolidColorBrush(TextMain), FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var closeBtn = new Border
        {
            Width = 34,
            Height = 30,
            CornerRadius = new CornerRadius(9),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        closeBtn.Child = new TextBlock { Text = "\u2715", Foreground = new SolidColorBrush(TextDim), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        closeBtn.MouseEnter += (_, _) => closeBtn.Background = new SolidColorBrush(Color.FromArgb(70, 255, 120, 120));
        closeBtn.MouseLeave += (_, _) => closeBtn.Background = Brushes.Transparent;
        closeBtn.MouseLeftButtonUp += (_, _) => _win.Close();
        var titleBar = new Border { Child = titleRow };
        title.Children.Add(titleBar);
        title.Children.Add(closeBtn);
        titleBar.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed && e.OriginalSource is not TextBlock) { try { _win.DragMove(); } catch { } } };

        // ---------- 左侧导航 ----------
        var navPanel = new StackPanel { Margin = new Thickness(0, 8, 16, 0) };
        navPanel.Children.Add(new TextBlock { Text = "页面", Foreground = new SolidColorBrush(TextDim), FontSize = 10.5, Margin = new Thickness(12, 0, 0, 6) });
        _navGeneral = NavButton("通用", SelectGeneral);
        _navAwake = NavButton("防睡眠", SelectAwake);
        _navPet = NavButton("桌面宠物", SelectPet);
        _navClean = NavButton("垃圾清理", SelectClean);
        navPanel.Children.Add(_navGeneral);
        navPanel.Children.Add(_navAwake);
        navPanel.Children.Add(_navPet);
        navPanel.Children.Add(_navClean);

        var navCol = new Grid();
        navCol.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        navCol.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        var navSeparator = new Border { Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), Margin = new Thickness(0, 10, 16, 10) };
        Grid.SetColumn(navSeparator, 1);
        navCol.Children.Add(navPanel);
        navCol.Children.Add(navSeparator);

        // ---------- 右侧页面（玻璃内卡，双栏观感） ----------
        _pageGeneral = BuildGeneralPage();
        _pageAwake = BuildAwakePage();
        _pagePet = BuildPetPage();
        _pageClean = BuildCleanPage();
        var pageStack = new Grid { Margin = new Thickness(4, 6, 6, 6) };
        _pageStack = pageStack;
        pageStack.Children.Add(_pageGeneral);
        pageStack.Children.Add(_pageAwake);
        pageStack.Children.Add(_pagePet);
        pageStack.Children.Add(_pageClean);
        var pageHost = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(0, 4, 4, 4),
            Child = pageStack,
        };

        var content = new Grid { Margin = new Thickness(18, 4, 4, 0) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(navCol, 0);
        Grid.SetColumn(pageHost, 1);
        content.Children.Add(navCol);
        content.Children.Add(pageHost);

        // ---------- 底部 ----------
        var bottom = new Grid();
        var okBtn = new Border
        {
            Padding = new Thickness(20, 7, 20, 7),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(AccA(40)),
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        okBtn.Child = new TextBlock { Text = "完成", Foreground = new SolidColorBrush(TextMain), FontSize = 12.5, FontWeight = FontWeights.SemiBold };
        okBtn.MouseEnter += (_, _) => okBtn.Background = new SolidColorBrush(AccA(80));
        okBtn.MouseLeave += (_, _) => okBtn.Background = new SolidColorBrush(AccA(40));
        okBtn.MouseLeftButtonUp += (_, _) => _win.Close();
        bottom.Children.Add(okBtn);

        Grid.SetRow(title, 0); Grid.SetRow(content, 1); Grid.SetRow(bottom, 2);
        grid.Children.Add(title); grid.Children.Add(content); grid.Children.Add(bottom);
        root.Child = grid;
        _root = root;
        StyleRoot();

        // 完成提示浮窗（底部居中，玻璃卡片）
        _toastText = new TextBlock { Foreground = Brushes.White, FontSize = 12.5, FontWeight = FontWeights.SemiBold };
        _toastBody = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 20, 28, 46)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(110, Accent.R, Accent.G, Accent.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 9, 16, 9),
            Child = _toastText,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 26),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 22, ShadowDepth = 3, Opacity = 0.45 },
        };
        var overlay = new Grid { Margin = new Thickness(16) };
        overlay.Children.Add(root);
        overlay.Children.Add(_toastBody);

        _win = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Width = 680,
            Height = 660,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI, Microsoft YaHei"),
            SizeToContent = SizeToContent.Manual,
            Content = overlay,
        };
        _win.Deactivated += (_, _) => { };
        _win.Closed += (_, _) => { App.Awake.Changed -= OnAwakeChanged; App.AccentChanged -= OnAccentChanged; };
        App.Awake.Changed += OnAwakeChanged;
        App.AccentChanged += OnAccentChanged;

        // 初始页
        _syncing = true;
        SelectPage(openCleanPage ? 3 : openPetPage ? 2 : 1);
        _syncing = false;
    }

    public void Show()
    {
        RefreshAll();
        CenterOnMouse(1.0);   // 先按 1.0 缩放预定位，避免左上角闪现一帧
        _win.Show();
        _win.Activate();
        CenterOnMouse();      // 再用实际 DPI 精确定位
    }

    // ---------- 通用页 ----------
    private FrameworkElement BuildGeneralPage()
    {
        var sp = new StackPanel { Margin = new Thickness(14, 8, 14, 8) };
        sp.Children.Add(PageTitle("通用设置"));
        _autoStart = new CheckBox { Content = "开机自启（随 Windows 启动）", Foreground = new SolidColorBrush(TextMain), FontSize = 12.5, Margin = new Thickness(0, 2, 0, 10) };
        _autoStart.Template = CheckTemplate();
        _autoStart.Checked += (_, _) => { if (!_syncing) App.SetAutoStart(true); };
        _autoStart.Unchecked += (_, _) => { if (!_syncing) App.SetAutoStart(false); };
        sp.Children.Add(_autoStart);

        _adminRunCb = new CheckBox { Content = "开机以管理员身份运行（需一次授权，解锁完整功能）", Foreground = new SolidColorBrush(TextMain), FontSize = 12.5, Margin = new Thickness(0, 0, 0, 12) };
        _adminRunCb.Template = CheckTemplate();
        _adminRunCb.Checked += (_, _) => { if (!_syncing) _ctx.SetAdminRun(true); };
        _adminRunCb.Unchecked += (_, _) => { if (!_syncing) _ctx.SetAdminRun(false); };
        sp.Children.Add(_adminRunCb);

        _releaseCb = new CheckBox { Content = "双击宠物释放内存（真实回收，默认关）", Foreground = new SolidColorBrush(TextMain), FontSize = 12.5, Margin = new Thickness(0, 0, 0, 14) };
        _releaseCb.Template = CheckTemplate();
        _releaseCb.Checked += (_, _) => { if (!_syncing) _ctx.SetReleaseOnDouble(true); };
        _releaseCb.Unchecked += (_, _) => { if (!_syncing) _ctx.SetReleaseOnDouble(false); };
        sp.Children.Add(_releaseCb);

        sp.Children.Add(new TextBlock { Text = "测速上传地址（可选，国内可 POST 且丢弃数据）", Foreground = new SolidColorBrush(TextDim), FontSize = 11, Margin = new Thickness(0, 2, 0, 4) });
        _netUpTb = new TextBox { FontSize = 11.5, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 12), Foreground = new SolidColorBrush(TextMain), Background = new SolidColorBrush(_light ? Color.FromRgb(255,255,255) : Color.FromRgb(28,36,58)), BorderBrush = new SolidColorBrush(Color.FromArgb(50,255,255,255)), TextWrapping = TextWrapping.NoWrap };
        _netUpTb.TextChanged += (_, _) => { if (!_syncing) _ctx.SetNetTestUrl(_netUpTb.Text); };
        sp.Children.Add(_netUpTb);

        sp.Children.Add(SectionTitle("软件主题"));
        string[] themeTexts = { "深色", "浅色", "跟随系统" };
        string[] themeModes = { "dark", "light", "system" };
        for (int i = 0; i < 3; i++)
        {
            var rb = new RadioButton { Content = themeTexts[i], Foreground = new SolidColorBrush(TextMain), FontSize = 12.5, Margin = new Thickness(0, 0, 0, 5), GroupName = "themeMode" };
            rb.Template = RadioTemplate();
            string mode = themeModes[i];
            rb.Checked += (_, _) => { if (!_syncing) { _ctx.SetTheme(mode); ApplyTheme(mode); } };
            _themeRadios[i] = rb;
            sp.Children.Add(rb);
        }

        sp.Children.Add(SectionTitle("主界面卡片"));
        var wp = new WrapPanel { Orientation = Orientation.Horizontal };
        (string Key, string Label)[] cards =
        {
            ("tileCpu", "CPU 总占用"), ("tileMem", "内存总占用"), ("tileTemp", "温度"),
            ("tileGpu", "GPU 占用"), ("tileUp", "已运行"), ("ecg", "整机负载波形"),
            ("gauge", "温度仪表盘"), ("procs", "运行进程"), ("disk", "磁盘 / 网络"),
            ("clean", "垃圾清理"),
        };
        foreach (var c in cards)
        {
            var cb = new CheckBox { Content = c.Label, Foreground = new SolidColorBrush(TextMain), FontSize = 12, Margin = new Thickness(0, 0, 0, 6), Width = 190 };
            cb.Template = CheckTemplate();
            string key = c.Key;
            cb.Checked += (_, _) => { if (!_syncing) _ctx.SetCardShown(key, true); };
            cb.Unchecked += (_, _) => { if (!_syncing) _ctx.SetCardShown(key, false); };
            wp.Children.Add(cb);
            _cardCbs.Add((key, cb));
        }
        sp.Children.Add(wp);

        var scroller = new ScrollViewer { Content = sp, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        return scroller;
    }

    // ---------- 防睡眠页 ----------
    private FrameworkElement BuildAwakePage()
    {
        var sp = new StackPanel { Margin = new Thickness(14, 8, 14, 8) };
        sp.Children.Add(PageTitle("防睡眠"));
        sp.Children.Add(SectionTitle("防睡眠档位"));
        string[] levelTexts = { "关闭（不拦截）", "① 仅防睡", "② 防睡 + 防熄屏", "③ 加强防锁" };
        for (int i = 0; i < 4; i++)
        {
            var rb = new RadioButton { Content = levelTexts[i], Foreground = new SolidColorBrush(TextMain), FontSize = 12.5, Margin = new Thickness(0, 0, 0, 5), GroupName = "awakeLevel" };
            rb.Template = RadioTemplate();
            int captured = i;
            rb.Checked += (_, _) => { if (!_syncing) App.Awake.SetLevel((AwakeLevel)captured); };
            _levelRadios[i] = rb;
            sp.Children.Add(rb);
        }

        sp.Children.Add(SectionTitle("防睡倒计时"));
        string[] timeTexts = { "不限时", "30 分钟", "1 小时", "整夜（8 小时）" };
        int?[] timeValues = { null, 30, 60, 480 };
        for (int i = 0; i < 4; i++)
        {
            int? minutes = timeValues[i];
            var rb = new RadioButton { Content = timeTexts[i], Foreground = new SolidColorBrush(TextMain), FontSize = 12.5, Margin = new Thickness(0, 0, 0, 5), GroupName = "awakeTimeout" };
            rb.Template = RadioTemplate();
            rb.Checked += (_, _) => { if (!_syncing) App.Awake.SetTimeoutMinutes(minutes); };
            _timeoutRadios[i] = rb;
            sp.Children.Add(rb);
        }

        _awakeStatus = new TextBlock { Foreground = new SolidColorBrush(TextDim), FontSize = 11, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        sp.Children.Add(_awakeStatus);
        var scroller = new ScrollViewer { Content = sp, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        return scroller;
    }

    // ---------- 宠物页 ----------
    private FrameworkElement BuildPetPage()
    {
        var sp = new StackPanel { Margin = new Thickness(14, 8, 14, 8) };
        sp.Children.Add(PageTitle("桌面宠物"));
        _petVisible = new CheckBox { Content = "显示桌面宠物", Foreground = new SolidColorBrush(TextMain), FontSize = 12.5, Margin = new Thickness(0, 2, 0, 10) };
        _petVisible.Template = CheckTemplate();
        _petVisible.Checked += (_, _) => { if (!_syncing) _ctx.SetHudVisible(true); };
        _petVisible.Unchecked += (_, _) => { if (!_syncing) _ctx.SetHudVisible(false); };
        sp.Children.Add(_petVisible);

        sp.Children.Add(SectionTitle("选择宠物"));
        (string Name, string Desc, int Idx)[] pets =
        {
            ("猫猫", "最初的伙伴", 0),
            ("小蓝", "专注的小机器人", 1),
            ("水滴", "清凉提神", 2),
            ("火苗", "热情的小火苗", 3),
            ("咕咕", "夜猫子伙伴", 4),
            ("石头", "沉稳的小石头", 5),
            ("芽芽", "生机勃勃", 6),
        };
        foreach (var p in pets) sp.Children.Add(BuildPetCard(p.Name, p.Desc, p.Idx));

        sp.Children.Add(SectionTitle("透明度"));
        var opRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 12) };
        _petOpacity = new Slider { Minimum = 20, Maximum = 100, TickFrequency = 5, IsSnapToTickEnabled = true, Width = 200, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Accent) };
        _petOpacity.Template = SliderTemplate();
        _petOpacityText = new TextBlock { Text = "92%", Foreground = new SolidColorBrush(TextDim), FontSize = 11.5, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _petOpacity.ValueChanged += (_, e) =>
        {
            _petOpacityText.Text = (int)e.NewValue + "%";
            if (!_syncing) _ctx.SetHudOpacity(e.NewValue / 100.0);
        };
        opRow.Children.Add(_petOpacity);
        opRow.Children.Add(_petOpacityText);
        sp.Children.Add(opRow);

        sp.Children.Add(SectionTitle("气泡显示项"));
        _mCpu = MetricCheck("CPU 占用", "cpu");
        _mMem = MetricCheck("内存占用", "mem");
        _mGpu = MetricCheck("GPU 占用", "gpu");
        sp.Children.Add(_mCpu);
        sp.Children.Add(_mMem);
        sp.Children.Add(_mGpu);

        sp.Children.Add(new TextBlock
        {
            Text = "提示：点击宠物弹出实时气泡；按住宠物可拖动。",
            Foreground = new SolidColorBrush(TextDim),
            FontSize = 10.5,
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });
        var scroller = new ScrollViewer { Content = sp, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        return scroller;
    }

    // ---------- 清理页 ----------
    private FrameworkElement BuildCleanPage()
    {
        var sp = new StackPanel { Margin = new Thickness(14, 8, 14, 8) };
        sp.Children.Add(PageTitle("垃圾清理"));
        sp.Children.Add(new TextBlock
        {
            Text = "只清确定无用的临时/缓存文件；占用中或无权限的会逐个跳过，不影响运行中的程序。",
            Foreground = new SolidColorBrush(TextDim),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });

        _cleanStatusTxt = new TextBlock { Foreground = new SolidColorBrush(TextDim), FontSize = 11.5, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap };
        sp.Children.Add(_cleanStatusTxt);

        // 进度条（扫描=呼吸动画 / 清理=确定进度）
        _cleanProgTrack = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), Margin = new Thickness(0, 0, 0, 8) };
        var fillHost = new Border { CornerRadius = new CornerRadius(3), ClipToBounds = true, Child = _cleanProgTrack };
        var grid = new Grid();
        var bg = new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(_light ? (byte)26 : (byte)40, 128, 128, 128)) };
        _cleanProgFill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop(AccA(255), 0), new GradientStop(AccA(160), 1) }, 0.0) };
        grid.Children.Add(bg);
        grid.Children.Add(_cleanProgFill);
        _cleanProgTrack.Child = grid;
        sp.Children.Add(fillHost);

        _cleanHost = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        sp.Children.Add(_cleanHost);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(MakeBtn("扫描", async () => await ScanClean()));
        _cleanGoBtn = MakeBtn("立即清理", async () => await CleanSelected(), true);
        _cleanGoBtn.Opacity = 0.45;
        row.Children.Add(_cleanGoBtn);
        sp.Children.Add(row);

        RenderCleanRows();
        var scroller = new ScrollViewer { Content = sp, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        return scroller;
    }

    private static Border MakeBtn(string text, Action click, bool primary = false)
    {
        static Brush Bg(Border rt) => (rt.Tag is true)
            ? new SolidColorBrush(Color.FromArgb(235, Accent.R, Accent.G, Accent.B))
            : new SolidColorBrush(AccA(34));
        var b = new Border
        {
            Padding = new Thickness(18, 7, 18, 7),
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(primary ? Color.FromArgb(235, Accent.R, Accent.G, Accent.B) : AccA(34)),
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Tag = primary,
        };
        b.Child = new TextBlock { Text = text, Foreground = primary ? Brushes.White : new SolidColorBrush(TextMain), FontSize = 12.5, FontWeight = FontWeights.SemiBold };
        b.MouseEnter += (_, _) => b.Background = primary ? new SolidColorBrush(AccA(255)) : new SolidColorBrush(AccA(66));
        b.MouseLeave += (_, _) => b.Background = Bg(b);
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    private async System.Threading.Tasks.Task ScanClean()
    {
        if (_cleanBusy) return;
        _cleanBusy = true;
        CleanBusy(true);
        _cleanStatusTxt.Text = "正在扫描可清理项…";
        try
        {
            var list = await System.Threading.Tasks.Task.Run(JunkCleaner.Scan);
            _cleanItems.Clear();
            _cleanItems.AddRange(list);
            if (_cleanSel.Count == 0)
            {
                foreach (var it in list)
                    if (it.Tier == CleanTier.Safe || (it.Tier == CleanTier.Admin && JunkCleaner.IsAdmin())) _cleanSel.Add(it.Key);
            }
            RenderCleanRows();
            SetCleanStatus();
        }
        catch (Exception ex) { _cleanStatusTxt.Text = "扫描失败：" + ex.Message; }
        finally
        {
            CleanBusy(false);
            _cleanBusy = false;
        }
    }

    private async System.Threading.Tasks.Task CleanSelected()
    {
        if (_cleanBusy) return;
        var sel = new System.Collections.Generic.List<CleanItem>();
        foreach (var it in _cleanItems) if (_cleanSel.Contains(it.Key)) sel.Add(it);
        if (sel.Count == 0) { _cleanStatusTxt.Text = "请先勾选要清理的项目。"; return; }

        _cleanBusy = true;
        CleanBusy(true);
        _cleanStatusTxt.Text = "正在清理…（占用中的文件会自动跳过）";
        try
        {
            long freed = 0; int ok = 0, fail = 0;
            for (int i = 0; i < sel.Count; i++)
            {
                var it = sel[i];
                CleanProgress((i + 1.0) / sel.Count);
                var r = await System.Threading.Tasks.Task.Run(() => JunkCleaner.Clean(it));
                freed += r.Freed; ok += r.Ok; fail += r.Fail;
            }
            CleanProgress(1.0);
            string size = Fmt(freed);
            App.AddCleanFreed(freed);
            string hint = ok == 0 ? "；文件可能正被程序占用或需管理员，未释放" : "";
            _cleanStatusTxt.Text = $"清理完成：释放 {size} · 成功 {ok} · 跳过 {fail}{hint} · 累计已释放 {Fmt(App.CleanTotal)}";
            Toast($"清理完成 · 释放 {size}（成功 {ok} / 跳过 {fail}{hint}）");
            var list = await System.Threading.Tasks.Task.Run(JunkCleaner.Scan);
            _cleanItems.Clear();
            _cleanItems.AddRange(list);
            RenderCleanRows();
        }
        catch (Exception ex) { _cleanStatusTxt.Text = "清理失败：" + ex.Message; }
        finally
        {
            CleanBusy(false);
            _cleanBusy = false;
        }
    }

    private void CleanBusy(bool busy)
    {
        if (_cleanGoBtn != null) _cleanGoBtn.Opacity = busy ? 0.45 : 1.0;
        if (_cleanProgFill == null || _cleanProgTrack == null) return;
        if (!busy)
        {
            _cleanProgFill.BeginAnimation(FrameworkElement.WidthProperty, null);
            _cleanProgFill.Width = 0;
            return;
        }
        _cleanProgFill.Width = 46;
        var trackW = Math.Max(120, _cleanProgTrack.ActualWidth);
        var anim = new DoubleAnimation(46, trackW, TimeSpan.FromMilliseconds(750)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        _cleanProgFill.BeginAnimation(FrameworkElement.WidthProperty, anim);
    }

    private void CleanProgress(double frac)
    {
        if (_cleanProgFill == null || _cleanProgTrack == null) return;
        _cleanProgFill.BeginAnimation(FrameworkElement.WidthProperty, null);
        _cleanProgFill.Width = Math.Max(120, _cleanProgTrack.ActualWidth) * Math.Clamp(frac, 0, 1);
    }

    /// <summary>底部浮窗提示（淡入，数秒后淡出）。</summary>
    private void Toast(string text)
    {
        if (_toastBody == null || _toastText == null) return;
        _toastText.Text = text;
        _toastBody.Visibility = Visibility.Visible;
        _toastBody.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.8) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(320));
            fade.Completed += (_, _) => _toastBody.Visibility = Visibility.Collapsed;
            _toastBody.BeginAnimation(UIElement.OpacityProperty, fade);
        };
        t.Start();
    }

    private void RenderCleanRows()
    {
        if (_cleanHost == null) return;
        _cleanHost.Children.Clear();
        if (_cleanItems.Count == 0)
        {
            _cleanHost.Children.Add(new TextBlock { Text = "尚未扫描。点「扫描」查看可清理项。", Foreground = new SolidColorBrush(TextDim), FontSize = 12, Margin = new Thickness(0, 4, 0, 4) });
            SetCleanStatus();
            return;
        }

        (string Title, string[] Keys)[] groups =
        {
            ("系统清理项", new[] { "userTemp", "winTemp", "updateCache", "thumb", "appLog", "systemTraces", "dxcache", "inetcache", "werReport", "deliveryCache", "winLogs", "crashDumps" }),
            ("应用清理项", new[] { "chromeCache", "edgeCache", "braveCache", "chromeHistory", "edgeHistory", "braveHistory" }),
            ("其他清理项", new[] { "recycle" }),
        };
        var byKey = new System.Collections.Generic.Dictionary<string, CleanItem>();
        foreach (var it in _cleanItems) byKey[it.Key] = it;

        foreach (var (title, keys) in groups)
        {
            var kids = new System.Collections.Generic.List<CleanItem>();
            foreach (var k in keys) { if (byKey.TryGetValue(k, out var it) && (it.Size > 0 || it.Files > 0)) kids.Add(it); }
            if (kids.Count == 0) continue;
            kids.Sort((a, b) => (int)(b.Size - a.Size));
            AddCleanGroup(title, kids);
        }
        SetCleanStatus();
    }

    private void AddCleanGroup(string title, System.Collections.Generic.List<CleanItem> kids)
    {
        long total = 0; foreach (var k in kids) total += k.Size;

        var parent = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0) };
        parent.Template = CheckTemplate();
        parent.Checked += (_, _) => { if (!_selSyncing) SetGroupSel(kids, true); };
        parent.Unchecked += (_, _) => { if (!_selSyncing) SetGroupSel(kids, false); };

        bool open = _cleanOpen.Contains(title);
        var chev = new TextBlock { Text = open ? "▼" : "▶", Foreground = new SolidColorBrush(TextDim), FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var titleTb = new TextBlock { Text = title, Foreground = new SolidColorBrush(TextMain), FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var sizeTb = new TextBlock { Text = Fmt(total), Foreground = new SolidColorBrush(TextDim), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(parent, 0); Grid.SetColumn(chev, 1); Grid.SetColumn(titleTb, 2); Grid.SetColumn(sizeTb, 3);
        grid.Children.Add(parent); grid.Children.Add(chev); grid.Children.Add(titleTb); grid.Children.Add(sizeTb);

        var header = new Border { Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(AccA(26)), Margin = new Thickness(0, 4, 0, 2), Cursor = Cursors.Hand, Child = grid };
        var body = new StackPanel();
        if (open)
        {
            foreach (var k in kids)
            {
                var nameTb = new TextBlock { Text = k.Name, Foreground = new SolidColorBrush(TextMain), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
                var szTb = new TextBlock { Text = Fmt(k.Size), Foreground = new SolidColorBrush(TextDim), FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(nameTb, 0); Grid.SetColumn(szTb, 1);
                g.Children.Add(nameTb); g.Children.Add(szTb);
                var cb = new CheckBox { Content = g, Foreground = new SolidColorBrush(TextMain), FontSize = 12.5, Margin = new Thickness(0, 0, 0, 7), IsChecked = _cleanSel.Contains(k.Key), Background = Brushes.Transparent, Tag = k.Key };
                cb.Template = CheckTemplate();
                string key = k.Key;
                cb.Checked += (_, _) => { if (!_selSyncing) { _cleanSel.Add(key); RenderCleanRows(); } };
                cb.Unchecked += (_, _) => { if (!_selSyncing) { _cleanSel.Remove(key); RenderCleanRows(); } };
                body.Children.Add(cb);
            }
        }

        SyncParent(parent, kids);

        header.MouseLeftButtonUp += (_, _) =>
        {
            if (_cleanOpen.Contains(title)) _cleanOpen.Remove(title); else _cleanOpen.Add(title);
            RenderCleanRows(); // 整表重建：展开/收起按当前状态生成子项
        };

        _cleanHost.Children.Add(header);
        _cleanHost.Children.Add(body);
    }

    private void SetGroupSel(System.Collections.Generic.List<CleanItem> kids, bool on)
    {
        _selSyncing = true;
        foreach (var k in kids)
        {
            if (on) _cleanSel.Add(k.Key); else _cleanSel.Remove(k.Key);
            // 按 Tag=key 精确同步子项复选框（不依赖文案）
            foreach (var el in _cleanHost.Children)
                if (el is StackPanel sp)
                    foreach (var c in sp.Children)
                        if (c is CheckBox ccb && ccb.Tag is string t && t == k.Key) ccb.IsChecked = on;
        }
        _selSyncing = false;
        SetCleanStatus();
    }

    private void SyncParent(CheckBox parent, System.Collections.Generic.List<CleanItem> kids)
    {
        bool all = kids.TrueForAll(k => _cleanSel.Contains(k.Key));
        _selSyncing = true;
        parent.IsChecked = all;
        _selSyncing = false;
    }

    private void SetCleanStatus()
    {
        if (_cleanStatusTxt == null) return;
        long total = 0, sel = 0; int tf = 0, sf = 0;
        foreach (var it in _cleanItems)
        {
            total += it.Size; tf += it.Files;
            if (_cleanSel.Contains(it.Key)) { sel += it.Size; sf += it.Files; }
        }
        bool needAdmin = _cleanItems.Exists(i => i.Tier == CleanTier.Admin && i.Size > 0) && !JunkCleaner.IsAdmin();
        _cleanStatusTxt.Text = $"共发现 {Fmt(total)}（{tf} 项）· 勾选可释放 {Fmt(sel)}（{sf} 项）"
            + (needAdmin ? "；部分需管理员，可提权后清" : "") + "。";
    }

    private static string Fmt(long b)
    {
        double v = b;
        if (v >= 1L << 30) return (v / (1L << 30)).ToString("0.0") + " GB";
        if (v >= 1L << 20) return (v / (1L << 20)).ToString("0.0") + " MB";
        return (v / 1024.0).ToString("0") + " KB";
    }

    private CheckBox MetricCheck(string text, string key)
    {
        var cb = new CheckBox { Content = text, Foreground = new SolidColorBrush(TextMain), FontSize = 12.5, Margin = new Thickness(0, 0, 0, 6) };
        cb.Template = CheckTemplate();
        cb.Checked += (_, _) => { if (!_syncing) _ctx.SetHudMetric(key, true); };
        cb.Unchecked += (_, _) => { if (!_syncing) _ctx.SetHudMetric(key, false); };
        return cb;
    }

    // ---------- 宠物卡片（Codex 风：头像 + 名字 + 简介 + 选择/已选） ----------
    private Border BuildPetCard(string name, string desc, int idx)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(_light ? (byte)210 : (byte)64, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(_light ? (byte)48 : (byte)56, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 4),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var avatar = new Border { Width = 56, Height = 60, Child = MiniHud.BuildPreview(idx), VerticalAlignment = VerticalAlignment.Center };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 10, 0) };
        text.Children.Add(new TextBlock { Text = name, Foreground = new SolidColorBrush(TextMain), FontSize = 13, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = desc, Foreground = new SolidColorBrush(TextDim), FontSize = 11, Margin = new Thickness(0, 2, 0, 0) });

        var btn = new Border
        {
            Padding = new Thickness(16, 6, 16, 6),
            CornerRadius = new CornerRadius(9),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        btn.Child = new TextBlock { Text = "选择", FontSize = 11.5, FontWeight = FontWeights.SemiBold };
        btn.MouseLeftButtonUp += (_, _) => { _ctx.SetPetStyle(idx); UpdatePetSelection(idx); };

        Grid.SetColumn(avatar, 0); Grid.SetColumn(text, 1); Grid.SetColumn(btn, 2);
        grid.Children.Add(avatar); grid.Children.Add(text); grid.Children.Add(btn);
        card.Child = grid;
        _petButtons.Add((btn, (TextBlock)btn.Child, idx));
        UpdatePetButton(btn, idx == _ctx.GetPetStyle());
        return card;
    }

    private void UpdatePetSelection(int sel)
    {
        foreach (var (btn, _, idx) in _petButtons) UpdatePetButton(btn, idx == sel);
    }

    private void UpdatePetButton(Border btn, bool on)
    {
        var txt = (TextBlock)btn.Child;
        txt.Text = on ? "已选" : "选择";
        txt.Foreground = on ? Brushes.White : new SolidColorBrush(TextDim);
        btn.Background = on ? new SolidColorBrush(Color.FromArgb(235, Accent.R, Accent.G, Accent.B)) : Brushes.Transparent;
        btn.BorderBrush = on ? new SolidColorBrush(Accent) : new SolidColorBrush(Color.FromArgb(40, 160, 180, 205));
        btn.BorderThickness = new Thickness(on ? 1 : 0);
    }

    // ---------- 通用小部件 ----------
    private static TextBlock PageTitle(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(TextMain),
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 9),
    };

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Accent),
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 2, 0, 6),
    };

    // ================= 自定义玻璃控件模板（替代原生复选框/单选框/滑块） =================

    /// <summary>复选：玻璃圆角框 + 选中时青绿勾选。</summary>
    private static ControlTemplate CheckTemplate()
    {
        var root = new FrameworkElementFactory(typeof(Border), "root");
        root.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        root.SetValue(Border.PaddingProperty, new Thickness(10, 5, 12, 5));
        root.SetValue(Border.MarginProperty, new Thickness(0));
        root.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        root.SetValue(Border.CursorProperty, Cursors.Hand);

        var dock = new FrameworkElementFactory(typeof(DockPanel));
        var box = new FrameworkElementFactory(typeof(Border), "box");
        box.SetValue(FrameworkElement.WidthProperty, 20d);
        box.SetValue(FrameworkElement.HeightProperty, 20d);
        box.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        box.SetValue(Border.BorderThicknessProperty, new Thickness(1.6));
        box.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(150, 168, 196)));
        box.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)));
        box.SetValue(DockPanel.DockProperty, Dock.Left);
        box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var check = new FrameworkElementFactory(typeof(TextBlock), "check");
        check.SetValue(TextBlock.TextProperty, "\u2713");
        check.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Accent));
        check.SetValue(TextBlock.FontSizeProperty, 13d);
        check.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        check.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        check.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        box.AppendChild(check);

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 0, 0));
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(ContentPresenter.ContentSourceProperty, "Content");

        dock.AppendChild(box);
        dock.AppendChild(content);
        root.AppendChild(dock);

        var t = new ControlTemplate(typeof(CheckBox));
        t.VisualTree = root;
        t.Triggers.Add(new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true,
            Setters = { new Setter(Border.BorderBrushProperty, new SolidColorBrush(Accent), "box"),
                        new Setter(Border.BackgroundProperty, new SolidColorBrush(AccA(44)), "box"),
                        new Setter(UIElement.VisibilityProperty, Visibility.Visible, "check"),
                        new Setter(Border.BackgroundProperty, new SolidColorBrush(AccA(24)), "root") } });
        t.Triggers.Add(new Trigger { Property = UIElement.IsMouseOverProperty, Value = true,
            Setters = { new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), "root") } });
        return t;
    }

    /// <summary>单选：玻璃圆环（选中时青绿填充 + 内点）。</summary>
    private static ControlTemplate RadioTemplate()
    {
        var root = new FrameworkElementFactory(typeof(Border), "root");
        root.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        root.SetValue(Border.PaddingProperty, new Thickness(10, 5, 12, 5));
        root.SetValue(Border.MarginProperty, new Thickness(0));
        root.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        root.SetValue(Border.CursorProperty, Cursors.Hand);

        var dock = new FrameworkElementFactory(typeof(DockPanel));
        var ring = new FrameworkElementFactory(typeof(Border), "ring");
        ring.SetValue(FrameworkElement.WidthProperty, 20d);
        ring.SetValue(FrameworkElement.HeightProperty, 20d);
        ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        ring.SetValue(Border.BorderThicknessProperty, new Thickness(1.6));
        ring.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(150, 168, 196)));
        ring.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)));
        ring.SetValue(DockPanel.DockProperty, Dock.Left);
        ring.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var dot = new FrameworkElementFactory(typeof(Border), "dot");
        dot.SetValue(FrameworkElement.WidthProperty, 10d);
        dot.SetValue(FrameworkElement.HeightProperty, 10d);
        dot.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        dot.SetValue(Border.BackgroundProperty, new SolidColorBrush(Accent));
        dot.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        dot.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        dot.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        ring.AppendChild(dot);

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 0, 0));
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(ContentPresenter.ContentSourceProperty, "Content");

        dock.AppendChild(ring);
        dock.AppendChild(content);
        root.AppendChild(dock);

        var t = new ControlTemplate(typeof(RadioButton));
        t.VisualTree = root;
        t.Triggers.Add(new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true,
            Setters = { new Setter(Border.BorderBrushProperty, new SolidColorBrush(Accent), "ring"),
                        new Setter(Border.BackgroundProperty, new SolidColorBrush(AccA(44)), "ring"),
                        new Setter(UIElement.VisibilityProperty, Visibility.Visible, "dot"),
                        new Setter(Border.BackgroundProperty, new SolidColorBrush(AccA(24)), "root") } });
        t.Triggers.Add(new Trigger { Property = UIElement.IsMouseOverProperty, Value = true,
            Setters = { new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), "root") } });
        return t;
    }

    /// <summary>滑块：细高亮渐变轨道 + 玻璃圆形 thumb（替代原生灰滑块）。</summary>
    private static ControlTemplate SliderTemplate()
    {
        string xaml = @"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Slider'>
  <Grid Height='24' Margin='0,2'>
    <Track x:Name='PART_Track' VerticalAlignment='Center'>
      <Track.DecreaseRepeatButton>
        <RepeatButton Height='6' VerticalAlignment='Center'>
          <RepeatButton.Template>
            <ControlTemplate TargetType='RepeatButton'>
              <Border Height='6' CornerRadius='3'>
                <Border.Background>
                  <LinearGradientBrush StartPoint='0,0' EndPoint='1,0'>
                    <GradientStop Color='#2BF5C4' Offset='0'/>
                    <GradientStop Color='#19BE96' Offset='1'/>
                  </LinearGradientBrush>
                </Border.Background>
              </Border>
            </ControlTemplate>
          </RepeatButton.Template>
        </RepeatButton>
      </Track.DecreaseRepeatButton>
      <Track.IncreaseRepeatButton>
        <RepeatButton Height='6' VerticalAlignment='Center'>
          <RepeatButton.Template>
            <ControlTemplate TargetType='RepeatButton'>
              <Border Height='6' CornerRadius='3' Background='#26FFFFFF'/>
            </ControlTemplate>
          </RepeatButton.Template>
        </RepeatButton>
      </Track.IncreaseRepeatButton>
      <Track.Thumb>
        <Thumb Width='20' Height='20' VerticalAlignment='Center'>
          <Thumb.Template>
            <ControlTemplate TargetType='Thumb'>
              <Border Width='20' Height='20' CornerRadius='10' BorderThickness='2' BorderBrush='#2BF5C4' Background='#E9F3FA'>
                <Border.Effect>
                  <DropShadowEffect Color='Black' BlurRadius='8' ShadowDepth='1' Opacity='0.35'/>
                </Border.Effect>
              </Border>
            </ControlTemplate>
          </Thumb.Template>
        </Thumb>
      </Track.Thumb>
    </Track>
  </Grid>
</ControlTemplate>";
        string hex = "#" + Accent.R.ToString("X2") + Accent.G.ToString("X2") + Accent.B.ToString("X2");
        string hexA = "#" + ((byte)(Accent.R * 0.62)).ToString("X2") + ((byte)(Accent.G * 0.62)).ToString("X2") + ((byte)(Accent.B * 0.62)).ToString("X2");
        xaml = xaml.Replace("#2BF5C4", hex).Replace("#19BE96", hexA);
        return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    private static Border NavButton(string text, Action onClick)
    {
        var b = new Border
        {
            Padding = new Thickness(14, 9, 14, 9),
            CornerRadius = new CornerRadius(9),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 2, 0, 2),
        };
        b.Child = new TextBlock { Text = text, Foreground = new SolidColorBrush(TextMain), FontSize = 13, FontWeight = FontWeights.SemiBold };
        b.MouseLeftButtonUp += (_, _) => onClick();
        b.MouseEnter += (_, _) => { if (b.Tag is not true) b.Background = new SolidColorBrush(Color.FromArgb(42, 255, 255, 255)); };
        b.MouseLeave += (_, _) => b.Background = b.Tag is true ? ActiveNavBrush() : Brushes.Transparent;
        return b;
    }

    private void SelectGeneral() => SelectPage(0);
    private void SelectAwake() => SelectPage(1);
    private void SelectPet() => SelectPage(2);
    private void SelectClean() => SelectPage(3);

    /// <summary>软件主题变化：重刷设置窗口外观（深/浅玻璃 + 文字配色）与页面。</summary>
    private void ApplyTheme(string mode)
    {
        _light = ResolveLight(mode);
        StyleRoot();
        RebuildPages();
        RefreshAll();
    }

    private static bool ResolveLight(string mode) =>
        mode == "light" || (mode == "system" && IsSystemLight());

    private static bool IsSystemLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    /// <summary>按深浅刷新主玻璃背景 + 柔和投影（质感）。</summary>
    private void StyleRoot()
    {
        _root.Background = _light
            ? new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromRgb(244, 247, 252), 0),
                    new GradientStop(Color.FromRgb(235, 239, 247), 0.45),
                    new GradientStop(Color.FromRgb(224, 230, 241), 1),
                },
                new System.Windows.Point(0, 0), new System.Windows.Point(0, 1))
            : new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(246, 26, 34, 54), 0),
                    new GradientStop(Color.FromArgb(252, 10, 16, 30), 1),
                },
                new System.Windows.Point(0, 0), new System.Windows.Point(0, 1));
        _root.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 28,
            ShadowDepth = _light ? 2 : 7,
            Opacity = _light ? 0.22 : 0.5,
        };
    }

    /// <summary>主题切换后重建三页内容（重新实例化控件以套用新文字配色）。</summary>
    private void RebuildPages()
    {
        _pageStack.Children.Clear();
        _pageGeneral = BuildGeneralPage();
        _pageAwake = BuildAwakePage();
        _pagePet = BuildPetPage();
        _pageClean = BuildCleanPage();
        _pageStack.Children.Add(_pageGeneral);
        _pageStack.Children.Add(_pageAwake);
        _pageStack.Children.Add(_pagePet);
        _pageStack.Children.Add(_pageClean);
        SelectPage(_currentPage);
    }

    private void SelectPage(int page)
    {
        _currentPage = page;
        _pageGeneral.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        _pageAwake.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        _pagePet.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
        _pageClean.Visibility = page == 3 ? Visibility.Visible : Visibility.Collapsed;
        SetNav(_navGeneral, page == 0);
        SetNav(_navAwake, page == 1);
        SetNav(_navPet, page == 2);
        SetNav(_navClean, page == 3);
    }

    private static void SetNav(Border nav, bool active)
    {
        nav.Tag = active;
        if (active)
        {
            nav.Background = ActiveNavBrush();
            if (nav.Child is TextBlock t) t.Foreground = Brushes.White;
        }
        else
        {
            nav.Background = Brushes.Transparent;
            if (nav.Child is TextBlock t) t.Foreground = new SolidColorBrush(TextMain);
        }
    }

    private static Brush ActiveNavBrush()
    {
        return new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(AccA(110), 0),
                new GradientStop(AccA(60), 1),
            }, 90.0);
    }

    private void OnAwakeChanged(AwakeState state) => _win.Dispatcher.InvokeAsync(RefreshAwake);
    private void OnAccentChanged() => _win.Dispatcher.InvokeAsync(() => { RebuildPages(); RefreshAll(); });

    private void RefreshAll()
    {
        _syncing = true;
        _autoStart.IsChecked = App.AutoStartEnabled;
        _adminRunCb.IsChecked = _ctx.GetAdminRun();
        _releaseCb.IsChecked = _ctx.GetReleaseOnDouble();
        _netUpTb.Text = _ctx.GetNetTestUrl();
        var metrics = _ctx.GetHudMetrics();
        _petVisible.IsChecked = _ctx.GetHudVisible();
        _petOpacity.Value = Math.Clamp(_ctx.GetHudOpacity() * 100, 20, 100);
        _mCpu.IsChecked = metrics.Contains("cpu");
        _mMem.IsChecked = metrics.Contains("mem");
        _mGpu.IsChecked = metrics.Contains("gpu");
        UpdatePetSelection(_ctx.GetPetStyle());
        int themeIdx = _ctx.GetTheme() switch { "light" => 1, "system" => 2, _ => 0 };
        _themeRadios[themeIdx].IsChecked = true;
        foreach (var (k, cb) in _cardCbs) cb.IsChecked = _ctx.GetCardShown(k);
        RefreshAwake();
        _syncing = false;
    }

    private void RefreshAwake()
    {
        var st = App.Awake.State;
        _syncing = true;
        _levelRadios[(int)st.Level].IsChecked = true;
        int idx;
        if (st.RemainSec is not { } r) idx = 0;
        else
        {
            int m = (int)Math.Round(r / 60.0);
            int[] cats = { 30, 60, 480 };
            int best = 0, bestD = int.MaxValue;
            for (int i = 0; i < cats.Length; i++)
            {
                int d = Math.Abs(m - cats[i]);
                if (d < bestD) { bestD = d; best = i; }
            }
            idx = best + 1; // 选中与当前剩余最接近的档位（剩余实时减少，需近似匹配）
        }
        _timeoutRadios[idx].IsChecked = true;
        _syncing = false;
        _awakeStatus.Text = st.Level == AwakeLevel.Off
            ? "当前：防睡眠未启用"
            : $"当前：{st.Label}" + (st.RemainSec is { } s ? $" · 剩余 {s / 60} 分" : "");
    }

    private void CenterOnMouse(double forcedScale = -1)
    {
        try
        {
            double scale = forcedScale > 0 ? forcedScale : (PresentationSource.FromVisual(_win)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0);
            if (scale < 0.5 || scale > 5) scale = 1.0;
            var wa = WF.Screen.FromPoint(new System.Drawing.Point((int)WF.Control.MousePosition.X, (int)WF.Control.MousePosition.Y)).WorkingArea;
            _win.Left = (wa.Left + (wa.Right - wa.Left - _win.Width * scale) / 2) / scale;
            _win.Top = (wa.Top + (wa.Bottom - wa.Top - _win.Height * scale) / 2) / scale;
        }
        catch { _win.WindowStartupLocation = WindowStartupLocation.CenterScreen; }
    }

    public void Close() => _win.Close();
}
