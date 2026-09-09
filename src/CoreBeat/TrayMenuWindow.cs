using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WF = System.Windows.Forms;
// 消除与 WinForms 隐式 using 的类型名歧义
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;
using Cursors = System.Windows.Input.Cursors;
using Orientation = System.Windows.Controls.Orientation;

namespace CoreBeat;

public enum TrayItemKind { Header, Item, Check, Danger, Slider, Separator, Group, Back }

public sealed class TrayEntry
{
    public TrayItemKind Kind;
    public string Text = "";
    public string? Sub = null;         // 右侧说明（如当前档位剩余时间）
    public bool Checked;
    public Action? Click;
    public double SliderValue = 1.0;   // 0..1
    public Action<double>? SliderChanged;
    public System.Collections.Generic.List<TrayEntry>? Children;
    public bool IsOpen;
}

/// <summary>
/// 自定义深色托盘菜单（替代默认 WinForms 菜单）。
/// 用法：每次打开前构造条目列表 → ShowAt(屏幕坐标)。
/// </summary>
public sealed class TrayMenuWindow
{
    private readonly Window _win = new()
    {
        WindowStyle = WindowStyle.None,
        ResizeMode = ResizeMode.NoResize,
        Topmost = true,
        ShowInTaskbar = false,
        AllowsTransparency = true,
        Width = 240,
        SizeToContent = SizeToContent.Height,
        Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0, 0, 0, 0)),
        Focusable = true,
    };
    private readonly Border _root;
    private double _scale = 1.0;
    private Point _anchor; // 打开时的鼠标位置（DIP）
    private double _lastLeft = double.NaN, _lastTop = double.NaN; // 最近放置位置（DIP）
    private System.Collections.Generic.List<TrayEntry>? _entries;

    public TrayMenuWindow()
    {
        _root = new Border
        {
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(224, 22, 30, 48), 0),
                    new GradientStop(Color.FromArgb(238, 12, 18, 34), 1),
                },
                new System.Windows.Point(0, 0), new System.Windows.Point(0, 1)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(6),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 26,
                ShadowDepth = 0,
                Opacity = 0.6,
            },
        };
        _win.Content = new ScrollViewer { Content = _root, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        _win.Deactivated += (_, _) => Hide();
        _win.MouseLeave += (_, _) => { };
    }

    public bool Visible => _win.IsVisible;

    public void ShowAt(System.Windows.Point screenPos)
    {
        bool first = !_win.IsVisible;
        if (first)
        {
            _win.Left = screenPos.X;
            _win.Top = screenPos.Y;
            _win.Show();
            _win.Activate();
        }
        _win.UpdateLayout();
        try
        {
            var src = PresentationSource.FromVisual(_win);
            _scale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            if (_scale < 0.5 || _scale > 5) _scale = 1.0;
        }
        catch { _scale = 1.0; }
        _anchor = new Point(screenPos.X / _scale, screenPos.Y / _scale);
        FitToScreen(true);
        _win.Focus();
    }

    /// <summary>最近一次放置的左上角（DIP）；从未放置过返回 null。二级页用它原地顶替旧菜单，保证页面切换不跳动。</summary>
    public System.Windows.Point? LastTopLeft =>
        double.IsNaN(_lastLeft) ? null : new System.Windows.Point(_lastLeft, _lastTop);

    /// <summary>二级页 / 返回主菜单：直接顶替传入的旧菜单位置（原地切换，不重新以鼠标为锚点）。
    /// topLeftDip 为 null 时退回以当前光标定位。</summary>
    public void ShowInPlace(System.Windows.Point? topLeftDip)
    {
        bool has = topLeftDip is { } p && !double.IsNaN(p.X) && !double.IsNaN(p.Y);
        if (has) { _win.Left = topLeftDip!.Value.X; _win.Top = topLeftDip.Value.Y; }
        if (!_win.IsVisible) { _win.Show(); _win.Activate(); }
        _win.UpdateLayout();
        try
        {
            var src = PresentationSource.FromVisual(_win);
            _scale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            if (_scale < 0.5 || _scale > 5) _scale = 1.0;
        }
        catch { _scale = 1.0; }
        if (has) FitToScreen(false);
        else
        {
            var c = WF.Control.MousePosition;
            _anchor = new Point(c.X / _scale, c.Y / _scale);
            FitToScreen(true);
        }
        _win.Focus();
    }

    /// <summary>按窗口当前实际尺寸把它完整约束进鼠标所在显示器的工作区；组展开/收起后重新调用即可防止越界。</summary>
    private void FitToScreen(bool useAnchor)
    {
        double w = _win.ActualWidth > 20 ? _win.ActualWidth : 252;
        double h = _win.ActualHeight > 20 ? _win.ActualHeight : 200;
        var wa = MonitorWorkArea(useAnchor);
        double x = useAnchor ? _anchor.X - w / 2 : _win.Left;
        double y = useAnchor ? _anchor.Y - h - 10 : _win.Top;
        double xMin = wa.Left + 4, xMax = Math.Max(xMin, wa.Right - w - 4);
        double yMin = wa.Top + 4, yMax = Math.Max(yMin, wa.Bottom - h - 4);
        _win.Left = Math.Clamp(x, xMin, xMax);
        _win.Top = Math.Clamp(y, yMin, yMax);
        _lastLeft = _win.Left;
        _lastTop = _win.Top;
    }

    /// <summary>鼠标所在显示器的工作区（物理像素 → DIP），不再用主屏 WorkArea，避免副屏/任务栏位置导致的偏移。</summary>
    private System.Windows.Rect MonitorWorkArea(bool useAnchor)
    {
        double px = useAnchor ? _anchor.X * _scale : (_win.Left + _win.ActualWidth / 2) * _scale;
        double py = useAnchor ? _anchor.Y * _scale : (_win.Top + _win.ActualHeight / 2) * _scale;
        WF.Screen screen;
        try { screen = WF.Screen.FromPoint(new System.Drawing.Point((int)px, (int)py)); }
        catch { screen = WF.Screen.PrimaryScreen!; }
        var wa = screen!.WorkingArea;
        return new System.Windows.Rect(
            wa.Left / _scale, wa.Top / _scale,
            (wa.Right - wa.Left) / _scale, (wa.Bottom - wa.Top) / _scale);
    }

    public void SetItems(IEnumerable<TrayEntry> entries)
    {
        _entries = new System.Collections.Generic.List<TrayEntry>(entries);
        Rebuild();
    }

    private void Rebuild()
    {
        var sp = new StackPanel();
        if (_entries != null)
            foreach (var e in _entries) AppendTo(sp, e, 0);
        _root.Child = sp;
        _win.UpdateLayout();
        if (_win.IsVisible) FitToScreen(false); // 内容高度变化后重新贴回屏幕内
    }

    private void AppendTo(StackPanel sp, TrayEntry e, int depth)
    {
        sp.Children.Add(BuildRow(e, depth));
        if (e.Kind == TrayItemKind.Group && e.IsOpen && e.Children != null)
            foreach (var child in e.Children) AppendTo(sp, child, depth + 1);
    }

    private UIElement BuildRow(TrayEntry e, int depth)
    {
        var indent = new Thickness(depth > 0 ? 18 : 0, 0, 0, 0);
        switch (e.Kind)
        {
            case TrayItemKind.Separator:
                return new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), Margin = new Thickness(10, 3, 10, 3) };
            case TrayItemKind.Header:
            {
                var sp = new StackPanel { Margin = new Thickness(14, 8, 14, 3) };
                sp.Children.Add(new TextBlock { Text = e.Text, Foreground = new SolidColorBrush(Color.FromRgb(236, 243, 255)), FontWeight = FontWeights.SemiBold, FontSize = 12.5, Opacity = 0.92 });
                if (e.Sub is { Length: > 0 } s) sp.Children.Add(new TextBlock { Text = s, Foreground = new SolidColorBrush(Color.FromRgb(140, 155, 182)), FontSize = 10.5, Margin = new Thickness(0, 1, 0, 0) });
                return sp;
            }
            case TrayItemKind.Group:
                return GroupRow(e, indent);
            case TrayItemKind.Slider:
            {
                var sp = new StackPanel { Margin = new Thickness(14, 6, 14, 8) };
                sp.Children.Add(new TextBlock { Text = e.Text, Foreground = new SolidColorBrush(Color.FromRgb(234, 242, 255)), FontSize = 12 });
                var s = new Slider { Minimum = 20, Maximum = 100, Value = Math.Clamp(e.SliderValue * 100, 20, 100), TickFrequency = 5, IsSnapToTickEnabled = true, Margin = new Thickness(0, 4, 0, 0) };
                s.ValueChanged += (_, args) => { try { e.SliderChanged?.Invoke(args.NewValue / 100.0); } catch { } };
                sp.Children.Add(s);
                return sp;
            }
            case TrayItemKind.Back:
                return SimpleRow(e, indent, true, 90, 210, 255);
            case TrayItemKind.Danger:
                return SimpleRow(e, indent, false, 255, 130, 130);
            default:
                return SimpleRow(e, indent, false, 234, 242, 255);
        }
    }

    private UIElement SimpleRow(TrayEntry e, Thickness indent, bool bold, byte r, byte g, byte b)
    {
        var row = new Border { Padding = new Thickness(12, 6, 12, 6), CornerRadius = new CornerRadius(8), Margin = new Thickness(2, 1, 2, 1), Cursor = Cursors.Hand };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var bar = new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(2), Background = Brushes.Transparent, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        if (e.Kind == TrayItemKind.Check && e.Checked) bar.Background = new SolidColorBrush(Color.FromRgb(43, 245, 196));
        var prefix = e.Kind switch
        {
            TrayItemKind.Back => "\u2039  ",
            TrayItemKind.Check => e.Checked ? "\u2713  " : "     ",
            _ => "",
        };
        var txt = new TextBlock
        {
            Text = prefix + e.Text,
            Foreground = new SolidColorBrush(Color.FromRgb(r, g, b)),
            FontSize = 12,
            FontWeight = bold || e.Checked ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(bar, 0);
        Grid.SetColumn(txt, 1);
        grid.Children.Add(bar);
        grid.Children.Add(txt);
        row.Child = grid;
        row.Margin = indent;
        row.Background = (e.Kind == TrayItemKind.Check && e.Checked) ? CheckedBg() : Brushes.Transparent;
        row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255));
        row.MouseLeave += (_, _) => row.Background = (e.Kind == TrayItemKind.Check && e.Checked) ? CheckedBg() : Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => { Hide(); e.Click?.Invoke(); };
        return row;
    }

    private UIElement GroupRow(TrayEntry e, Thickness indent)
    {
        var row = new Border { Padding = new Thickness(12, 7, 12, 7), CornerRadius = new CornerRadius(8), Margin = new Thickness(2, 1, 2, 1), Cursor = Cursors.Hand };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var chev = new TextBlock { Text = e.IsOpen ? "\u25BC " : "\u25B6 ", Foreground = new SolidColorBrush(Color.FromRgb(140, 158, 190)), FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var txt = new TextBlock { Text = e.Text, Foreground = new SolidColorBrush(Color.FromRgb(234, 242, 255)), FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(chev);
        sp.Children.Add(txt);
        if (e.Sub is { Length: > 0 } s) sp.Children.Add(new TextBlock { Text = "   " + s, Foreground = new SolidColorBrush(Color.FromRgb(140, 158, 190)), FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center });
        row.Child = sp;
        row.Margin = indent;
        row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => { e.IsOpen = !e.IsOpen; Rebuild(); };
        return row;
    }

    private static Brush CheckedBg()
    {
        return new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(78, 43, 245, 196), 0),
                new GradientStop(Color.FromArgb(45, 43, 245, 196), 1),
            }, 90.0);
    }

    public void Hide() => _win.Hide();

    public void Dispose() => _win.Close();
}
