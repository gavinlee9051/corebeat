using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using CoreBeat.Services;
// 消除与 WinForms 隐式 using 的类型名歧义
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace CoreBeat;

public sealed class MiniHud : IDisposable
{
    private readonly Window _window = new()
    {
        Title = "芯跳桌面宠物",
        WindowStyle = WindowStyle.None,
        ResizeMode = ResizeMode.NoResize,
        Topmost = true,
        ShowInTaskbar = false,
        Width = 120, Height = 124,
        AllowsTransparency = true,
        Background = Brushes.Transparent,
        Opacity = 0.92,
    };
    private readonly SolidColorBrush _accent = new(App.Accent);
    private readonly TextBlock _tipText = new();
    private readonly Popup _tip;
    private readonly Border _tipBody;
    private readonly Action<string> _sink;
    private bool _disposed;
    private double _cpu, _memPct, _gpuPct;
    private int _downX, _downY, _winLeft, _winTop;
    private bool _pressed, _moved;
    private double _dpiScale = 1.0;
    private long _lastClickTicks;
    private readonly System.Windows.Threading.DispatcherTimer _clickTimer = new() { Interval = TimeSpan.FromMilliseconds(280) };

    public MiniHud()
    {
        _tipText.Foreground = new SolidColorBrush(Color.FromRgb(236, 243, 255));
        _tipText.FontSize = 12.5;
        _tipText.FontWeight = FontWeights.SemiBold;
        _tipBody = new Border
        {
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(235, 30, 40, 66), 0),
                    new GradientStop(Color.FromArgb(244, 12, 18, 36), 1),
                },
                new Point(0, 0), new Point(0, 1)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(140, 107, 124, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 5, 9, 5),
            Child = _tipText,
            Effect = new DropShadowEffect { Color = Color.FromRgb(90, 110, 230), BlurRadius = 16, ShadowDepth = 0, Opacity = 0.3 },
        };
        _tip = new Popup
        {
            AllowsTransparency = true,
            PlacementTarget = _window,
            Placement = PlacementMode.Top,
            VerticalOffset = -6,
            StaysOpen = true,
            Child = _tipBody,
        };

        var pet = BuildPet(App.PetStyle);
        _window.Content = pet;
        _window.SourceInitialized += (_, _) =>
        {
            try
            {
                var src = PresentationSource.FromVisual(_window);
                if (src?.CompositionTarget != null) _dpiScale = src.CompositionTarget.TransformToDevice.M11;
                if (_dpiScale < 0.5 || _dpiScale > 5) _dpiScale = 1.0;
            }
            catch { _dpiScale = 1.0; }
            var wa = SystemParameters.WorkArea;
            _window.Left = wa.Right - _window.Width - 16;
            _window.Top = wa.Bottom - _window.Height - 14;
        };
        WireInput();
        _sink = json => { try { _window.Dispatcher.Invoke(() => OnData(json)); } catch { } };
        App.RegisterSink(_sink);
        App.AccentChanged += OnAccentChanged; // 主色变化时重建宠物配色
    }

    private void OnAccentChanged()
    {
        try { _window.Dispatcher.Invoke(() => { if (!_disposed) _window.Content = BuildPet(App.PetStyle); }); }
        catch { }
    }

    private Grid BuildPet(int style)
    {
        var host = new Grid();
        var inner = new Grid();
        inner.Children.Add(PetCanvas(style));
        // 整体呼吸：轻微缩放（所有风格通用）
        var breathe = new ScaleTransform(1, 1);
        inner.RenderTransform = breathe;
        inner.RenderTransformOrigin = new Point(0.5, 0.5);
        var br = new DoubleAnimation(1, 1.05, TimeSpan.FromSeconds(1.5)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        breathe.BeginAnimation(ScaleTransform.ScaleXProperty, br);
        breathe.BeginAnimation(ScaleTransform.ScaleYProperty, br.Clone());
        host.Children.Add(inner);
        var bob = new TranslateTransform();
        bob.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-3, 0, TimeSpan.FromSeconds(1.2)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        host.RenderTransform = bob;
        return host;
    }

    /// <summary>切换宠物风格（0 猫猫 / 1 小蓝 / 2 水滴 / 3 火苗 / 4 咕咕 / 5 石头 / 6 芽芽）。</summary>
    public void SetStyle(int style)
    {
        try { _window.Dispatcher.Invoke(() => { if (!_disposed) _window.Content = BuildPet(style); }); }
        catch { }
    }

    /// <summary>供设置页/预览复用：按风格生成宠物画布（静态色板）。</summary>
    private static Canvas PetCanvas(int style) => style switch
    {
        1 => BuildCodex(),
        2 => BuildDewey(),
        3 => BuildFireball(),
        4 => BuildHoots(),
        5 => BuildRocky(),
        6 => BuildSeedy(),
        _ => BuildKitty(),
    };

    /// <summary>设置页卡片用的宠物缩略预览（真实渲染后 58px）。</summary>
    public static FrameworkElement BuildPreview(int style)
    {
        var c = PetCanvas(style);
        c.Width = 120; c.Height = 124;
        return new Viewbox { Width = 58, Height = 60, Stretch = Stretch.Uniform, Child = c };
    }

    private static void AddR(Canvas c, double x, double y, double w, double h, Brush f, double r = 4)
    {
        var rct = new Rectangle { Width = w, Height = h, Fill = f, RadiusX = r, RadiusY = r };
        Canvas.SetLeft(rct, x); Canvas.SetTop(rct, y); c.Children.Add(rct);
    }
    private static void AddE(Canvas c, double x, double y, double rx, double ry, Brush f, Brush? st = null, double sw = 0)
    {
        var e = new Ellipse { Width = rx * 2, Height = ry * 2, Fill = f };
        if (st != null) { e.Stroke = st; e.StrokeThickness = sw; }
        Canvas.SetLeft(e, x - rx); Canvas.SetTop(e, y - ry); c.Children.Add(e);
    }

    /// <summary>风格0：最初的猫猫（默认）。</summary>
    private static Canvas BuildKitty()
    {
        var c = new Canvas { Background = Brushes.Transparent };
        var accent = new SolidColorBrush(App.Accent);
        var dark = new SolidColorBrush(Color.FromRgb(34, 48, 74));
        var edge = new SolidColorBrush(Color.FromRgb(58, 78, 120));
        var body = new SolidColorBrush(Color.FromRgb(255, 150, 138));
        var head = new SolidColorBrush(Color.FromRgb(255, 186, 176));
        var cheek = new SolidColorBrush(Color.FromRgb(255, 130, 148));
        var brow = new SolidColorBrush(Color.FromRgb(74, 56, 56));
        var white = Brushes.White;
        c.Children.Add(new Path { Data = Geometry.Parse("M 78 100 C 100 94 100 76 95 72"), Stroke = body, StrokeThickness = 7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        AddE(c, 46, 113, 10, 7, body); AddE(c, 78, 113, 10, 7, body);
        AddE(c, 37, 90, 5.5, 9, body); AddE(c, 83, 90, 5.5, 9, body);
        AddE(c, 60, 96, 19, 22, body, edge, 1);
        AddE(c, 60, 101, 11, 13, new SolidColorBrush(Color.FromRgb(255, 244, 240)));
        c.Children.Add(new Path { Data = Geometry.Parse("M 52 74 L 44 66 M 68 74 L 76 66 M 52 74 Q 60 82 68 74 Z"), Fill = accent });
        AddE(c, 60, 46, 33, 31, head, edge, 1);
        c.Children.Add(new Line { X1 = 46, Y1 = 22, X2 = 46, Y2 = 12, Stroke = white, StrokeThickness = 3 });
        AddE(c, 46, 9, 4, 4, accent);
        c.Children.Add(new Line { X1 = 74, Y1 = 22, X2 = 74, Y2 = 12, Stroke = white, StrokeThickness = 3 });
        AddE(c, 74, 9, 4, 4, accent);
        AddE(c, 44, 48, 7, 9.5, dark); AddE(c, 46, 44.5, 2.6, 2.6, white);
        AddE(c, 76, 48, 7, 9.5, dark); AddE(c, 78, 44.5, 2.6, 2.6, white);
        c.Children.Add(new Line { X1 = 36, Y1 = 38, X2 = 50, Y2 = 38, Stroke = brow, StrokeThickness = 2.4 });
        c.Children.Add(new Line { X1 = 70, Y1 = 38, X2 = 84, Y2 = 38, Stroke = brow, StrokeThickness = 2.4 });
        AddE(c, 30, 54, 6, 3.6, cheek); AddE(c, 90, 54, 6, 3.6, cheek);
        c.Children.Add(new Path { Data = Geometry.Parse("M 54 56 Q 60 61 66 56"), Stroke = brow, StrokeThickness = 2 });
        return c;
    }

    /// <summary>风格1：小蓝机器人。</summary>
    private static Canvas BuildCodex()
    {
        var c = new Canvas();
        var body = new SolidColorBrush(Color.FromRgb(47, 83, 184));
        var face = new SolidColorBrush(Color.FromRgb(157, 184, 245));
        var dark = new SolidColorBrush(Color.FromRgb(22, 35, 79));
        var accent = new SolidColorBrush(App.Accent);
        AddR(c, 43, 42, 34, 50, body, 10);
        AddR(c, 46, 48, 28, 20, face, 7);
        AddR(c, 51, 53, 4, 8, dark); AddR(c, 65, 53, 4, 8, dark);
        AddE(c, 60, 74, 4, 3, accent);
        AddR(c, 30, 54, 8, 26, body, 4); AddR(c, 82, 54, 8, 26, body, 4);
        AddR(c, 46, 92, 9, 11, body, 3); AddR(c, 65, 92, 9, 11, body, 3);
        c.Children.Add(new Line { X1 = 60, Y1 = 40, X2 = 60, Y2 = 22, Stroke = dark, StrokeThickness = 3 });
        AddE(c, 60, 19, 4, 4, accent);
        return c;
    }

    /// <summary>风格1：Dewey —— 蓝色水滴。</summary>
    private static Canvas BuildDewey()
    {
        var c = new Canvas();
        c.Children.Add(new Path
        {
            Fill = new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(Color.FromRgb(150, 205, 255), 0),
                new GradientStop(Color.FromRgb(58, 132, 224), 1),
            }, 90.0),
            Stroke = new SolidColorBrush(Color.FromArgb(160, 30, 90, 170)),
            StrokeThickness = 1.5,
            Data = Geometry.Parse("M 60 26 C 49 40 41 54 41 69 A 19 19 0 1 0 79 69 C 79 54 71 40 60 26 Z"),
        });
        AddE(c, 52, 60, 3.4, 4.2, new SolidColorBrush(Color.FromRgb(20, 45, 90)));
        AddE(c, 68, 60, 3.4, 4.2, new SolidColorBrush(Color.FromRgb(20, 45, 90)));
        AddE(c, 49, 50, 4, 3, new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)));
        return c;
    }

    /// <summary>风格2：Fireball —— 火焰。</summary>
    private static Canvas BuildFireball()
    {
        var c = new Canvas();
        c.Children.Add(new Path
        {
            Fill = new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(Color.FromRgb(255, 216, 120), 0),
                new GradientStop(Color.FromRgb(255, 122, 45), 1),
            }, 90.0),
            Stroke = new SolidColorBrush(Color.FromArgb(150, 200, 90, 20)),
            StrokeThickness = 1.5,
            Data = Geometry.Parse("M 60 24 C 47 40 39 56 39 72 A 21 21 0 1 0 81 72 C 81 56 73 40 60 24 Z"),
        });
        c.Children.Add(new Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(230, 255, 236, 150)),
            Data = Geometry.Parse("M 60 46 C 53 58 49 66 49 75 A 11 11 0 1 0 71 75 C 71 66 67 58 60 46 Z"),
        });
        AddE(c, 54, 66, 3.4, 4, new SolidColorBrush(Color.FromRgb(140, 60, 20)));
        AddE(c, 66, 66, 3.4, 4, new SolidColorBrush(Color.FromRgb(140, 60, 20)));
        return c;
    }

    /// <summary>风格3：Hoots —— 橙色猫头鹰。</summary>
    private static Canvas BuildHoots()
    {
        var c = new Canvas();
        var orange = new SolidColorBrush(Color.FromRgb(240, 161, 58));
        var belly = new SolidColorBrush(Color.FromRgb(247, 196, 106));
        AddR(c, 44, 46, 8, 10, orange, 3); AddR(c, 68, 46, 8, 10, orange, 3);
        AddE(c, 60, 66, 27, 24, orange);
        AddE(c, 60, 74, 17, 14, belly);
        AddE(c, 51, 58, 8, 9, Brushes.White); AddE(c, 69, 58, 8, 9, Brushes.White);
        AddE(c, 51, 60, 3.2, 4, new SolidColorBrush(Color.FromRgb(40, 30, 20)));
        AddE(c, 69, 60, 3.2, 4, new SolidColorBrush(Color.FromRgb(40, 30, 20)));
        c.Children.Add(new Path { Data = Geometry.Parse("M 57 66 L 63 66 L 60 71 Z"), Fill = new SolidColorBrush(Color.FromRgb(240, 130, 40)) });
        return c;
    }

    /// <summary>风格4：Rocky —— 岩石小兽（顶生苔藓）。</summary>
    private static Canvas BuildRocky()
    {
        var c = new Canvas();
        AddE(c, 60, 70, 27, 24, new SolidColorBrush(Color.FromRgb(154, 163, 173)));
        AddE(c, 60, 80, 22, 12, new SolidColorBrush(Color.FromRgb(110, 120, 132)));
        AddE(c, 60, 54, 25, 12, new SolidColorBrush(Color.FromRgb(127, 200, 107)));
        AddE(c, 47, 76, 2.4, 3, new SolidColorBrush(Color.FromRgb(40, 40, 45)));
        AddE(c, 73, 76, 2.4, 3, new SolidColorBrush(Color.FromRgb(40, 40, 45)));
        AddR(c, 62, 60, 3, 8, new SolidColorBrush(Color.FromRgb(178, 186, 194)), 1);
        return c;
    }

    /// <summary>风格5：Seedy —— 盆栽新芽。</summary>
    private static Canvas BuildSeedy()
    {
        var c = new Canvas();
        AddR(c, 46, 86, 28, 22, new SolidColorBrush(Color.FromRgb(210, 106, 79)), 5);
        AddR(c, 42, 84, 36, 7, new SolidColorBrush(Color.FromRgb(184, 90, 67)), 4);
        AddR(c, 58, 64, 4, 22, new SolidColorBrush(Color.FromRgb(76, 174, 98)), 2);
        AddE(c, 54, 62, 11, 6, new SolidColorBrush(Color.FromRgb(76, 174, 98)));
        AddE(c, 66, 62, 11, 6, new SolidColorBrush(Color.FromRgb(76, 174, 98)));
        AddE(c, 60, 54, 6, 6, new SolidColorBrush(Color.FromRgb(108, 199, 122)));
        AddE(c, 52, 92, 2, 2.4, new SolidColorBrush(Color.FromRgb(50, 30, 20)));
        AddE(c, 66, 92, 2, 2.4, new SolidColorBrush(Color.FromRgb(50, 30, 20)));
        return c;
    }

    private Canvas BuildCat()
    {
        var c = new Canvas { Background = Brushes.Transparent };
        var dark = new SolidColorBrush(Color.FromRgb(34, 48, 74));
        var edge = new SolidColorBrush(Color.FromRgb(58, 78, 120));
        var body = new SolidColorBrush(Color.FromRgb(255, 150, 138));
        var head = new SolidColorBrush(Color.FromRgb(255, 186, 176));
        var cheek = new SolidColorBrush(Color.FromRgb(255, 130, 148));
        var brow = new SolidColorBrush(Color.FromRgb(74, 56, 56));
        var white = Brushes.White;

        void Add(Shape s) => c.Children.Add(s);
        Ellipse E(double x, double y, double rx, double ry, Brush f, Brush? st = null, double sw = 0)
        {
            var e = new Ellipse { Width = rx * 2, Height = ry * 2, Fill = f };
            if (st != null) { e.Stroke = st; e.StrokeThickness = sw; }
            Canvas.SetLeft(e, x - rx);
            Canvas.SetTop(e, y - ry);
            return e;
        }

        Add(new Path { Data = Geometry.Parse("M 78 100 C 100 94 100 76 95 72"), Stroke = body, StrokeThickness = 7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        Add(E(46, 113, 10, 7, body)); Add(E(78, 113, 10, 7, body));
        Add(E(37, 90, 5.5, 9, body)); Add(E(83, 90, 5.5, 9, body));
        Add(E(60, 96, 19, 22, body, edge, 1));
        Add(E(60, 101, 11, 13, new SolidColorBrush(Color.FromRgb(255, 244, 240))));
        Add(new Path { Data = Geometry.Parse("M 52 74 L 44 66 M 68 74 L 76 66 M 52 74 Q 60 82 68 74 Z"), Fill = _accent });
        Add(E(60, 46, 33, 31, head, edge, 1));
        Add(new Line { X1 = 46, Y1 = 22, X2 = 46, Y2 = 12, Stroke = white, StrokeThickness = 3 });
        Add(E(46, 9, 4, 4, _accent));
        Add(new Line { X1 = 74, Y1 = 22, X2 = 74, Y2 = 12, Stroke = white, StrokeThickness = 3 });
        Add(E(74, 9, 4, 4, _accent));
        Add(E(44, 48, 7, 9.5, dark)); Add(E(46, 44.5, 2.6, 2.6, white));
        Add(E(76, 48, 7, 9.5, dark)); Add(E(78, 44.5, 2.6, 2.6, white));
        Add(new Line { X1 = 36, Y1 = 38, X2 = 50, Y2 = 38, Stroke = brow, StrokeThickness = 2.4 });
        Add(new Line { X1 = 70, Y1 = 38, X2 = 84, Y2 = 38, Stroke = brow, StrokeThickness = 2.4 });
        Add(E(30, 54, 6, 3.6, cheek)); Add(E(90, 54, 6, 3.6, cheek));
        Add(new Path { Data = Geometry.Parse("M 54 56 Q 60 61 66 56"), Stroke = brow, StrokeThickness = 2 });
        return c;
    }

    /// <summary>风格1：能量核心（径向渐变光球 + 光晕 + 三颗轨道火花旋转 + 核心脉冲）。</summary>
    private Canvas BuildOrb()
    {
        var c = new Canvas();
        // 外层光晕
        var glow = new Ellipse { Width = 96, Height = 96, Fill = new RadialGradientBrush(new GradientStopCollection
        {
            new GradientStop(Color.FromArgb(80, 107, 124, 255), 0),
            new GradientStop(Color.FromArgb(0, 107, 124, 255), 1),
        }) };
        Canvas.SetLeft(glow, 60 - 48); Canvas.SetTop(glow, 62 - 48); c.Children.Add(glow);
        // 径向渐变核心：亮白高光 → 青绿 → 深青
        var core = new Ellipse { Width = 54, Height = 54, Fill = new RadialGradientBrush(new GradientStopCollection
        {
            new GradientStop(Color.FromRgb(224, 255, 249), 0),
            new GradientStop(Color.FromRgb(107, 124, 255), 0.5),
            new GradientStop(Color.FromRgb(8, 110, 104), 1),
        }) };
        Canvas.SetLeft(core, 60 - 27); Canvas.SetTop(core, 62 - 27);
        var cs = new ScaleTransform(1, 1);
        core.RenderTransform = cs; core.RenderTransformOrigin = new Point(0.5, 0.5);
        var cp = new DoubleAnimation(1, 1.1, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        cs.BeginAnimation(ScaleTransform.ScaleXProperty, cp);
        cs.BeginAnimation(ScaleTransform.ScaleYProperty, cp.Clone());
        c.Children.Add(core);
        // 细环
        var ring = new Ellipse { Width = 62, Height = 62, Stroke = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), StrokeThickness = 1.2 };
        Canvas.SetLeft(ring, 60 - 31); Canvas.SetTop(ring, 62 - 31); c.Children.Add(ring);
        // 内高光
        var hi = new Ellipse { Width = 12, Height = 9, Fill = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)) };
        Canvas.SetLeft(hi, 51); Canvas.SetTop(hi, 51); c.Children.Add(hi);
        // 三颗轨道火花
        var orbit = new Canvas();
        for (int k = 0; k < 3; k++)
        {
            double a = k * 120 * Math.PI / 180;
            double ox = 60 + 34 * Math.Cos(a), oy = 62 + 34 * Math.Sin(a);
            var dot = new Ellipse { Width = 5, Height = 5, Fill = _accent, Effect = new DropShadowEffect { Color = Color.FromRgb(107, 124, 255), BlurRadius = 9, ShadowDepth = 0, Opacity = 0.9 } };
            Canvas.SetLeft(dot, ox - 2.5); Canvas.SetTop(dot, oy - 2.5); orbit.Children.Add(dot);
        }
        var rot = new RotateTransform(0, 60, 62);
        orbit.RenderTransform = rot;
        rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(7)) { RepeatBehavior = RepeatBehavior.Forever });
        c.Children.Add(orbit);
        return c;
    }

    /// <summary>风格2：像素方块猫。</summary>
    private Canvas BuildPixelCat()
    {
        var c = new Canvas();
        var dark = new SolidColorBrush(Color.FromRgb(47, 74, 108));
        var body = new SolidColorBrush(Color.FromRgb(33, 53, 82));
        void R(double x, double y, double w, double h, Brush f)
        {
            var r = new Rectangle { Width = w, Height = h, Fill = f, RadiusX = 4, RadiusY = 4 };
            Canvas.SetLeft(r, x); Canvas.SetTop(r, y); c.Children.Add(r);
        }
        R(33, 22, 15, 15, dark); R(72, 22, 15, 15, dark);
        R(31, 30, 58, 45, dark);
        R(38, 71, 44, 40, body);
        R(40, 42, 15, 12, Brushes.White); R(65, 42, 15, 12, Brushes.White);
        R(45, 45, 6, 8, Brushes.Black); R(69, 45, 6, 8, Brushes.Black);
        R(56, 56, 8, 5, _accent);
        return c;
    }

    /// <summary>风格3：心跳（渐变描边 + 光晕 + 快速心搏脉冲）。</summary>
    private Canvas BuildHeart()
    {
        var c = new Canvas();
        var heart = new Path
        {
            Data = Geometry.Parse("M 60 40 C 46 28 30 34 30 52 C 30 68 46 76 60 88 C 74 76 90 68 90 52 C 90 34 74 28 60 40 Z"),
            Fill = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromRgb(150, 255, 240), 0),
                    new GradientStop(Color.FromRgb(107, 124, 255), 0.6),
                    new GradientStop(Color.FromRgb(12, 140, 132), 1),
                }, 90.0),
            Stroke = new SolidColorBrush(Color.FromArgb(200, 107, 124, 255)),
            StrokeThickness = 1.8,
            Effect = new DropShadowEffect { Color = Color.FromRgb(107, 124, 255), BlurRadius = 30, ShadowDepth = 0, Opacity = 0.7 },
        };
        heart.RenderTransformOrigin = new Point(0.5, 0.5);
        var beat = new ScaleTransform(1, 1);
        heart.RenderTransform = beat;
        var bp = new DoubleAnimation(1, 1.1, TimeSpan.FromSeconds(0.34)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        beat.BeginAnimation(ScaleTransform.ScaleXProperty, bp);
        beat.BeginAnimation(ScaleTransform.ScaleYProperty, bp.Clone());
        c.Children.Add(heart);
        var hi = new Ellipse { Width = 13, Height = 8, Fill = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)) };
        Canvas.SetLeft(hi, 44); Canvas.SetTop(hi, 52); c.Children.Add(hi);
        return c;
    }

    private void WireInput()
    {
        _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); ShowTip(); }; // 单击延迟弹出气泡
        _window.MouseLeftButtonDown += (_, _) =>
        {
            _window.CaptureMouse();
            var p = System.Windows.Forms.Cursor.Position;
            _downX = p.X; _downY = p.Y;
            _winLeft = (int)_window.Left; _winTop = (int)_window.Top;
            _pressed = true; _moved = false;
        };
        _window.MouseMove += (_, _) =>
        {
            if (!_pressed) return;
            var p = System.Windows.Forms.Cursor.Position;
            if (!_moved && Math.Abs(p.X - _downX) + Math.Abs(p.Y - _downY) < 6) return;
            _moved = true;
            _window.Left = _winLeft + (p.X - _downX) / _dpiScale;
            _window.Top = _winTop + (p.Y - _downY) / _dpiScale;
        };
        _window.MouseLeftButtonUp += (_, e) =>
        {
            _window.ReleaseMouseCapture();
            bool wasMoved = _moved;
            _pressed = false; _moved = false;
            if (wasMoved) return;

            // 手动测速双击：两次抬起间隔 < 500ms 视为双击（ClickCount 在 RDP/透明窗口下不可靠）
            long now = Environment.TickCount64;
            bool isDouble = _lastClickTicks != 0 && now - _lastClickTicks < 500;
            _lastClickTicks = now;

            _clickTimer.Stop();
            CloseTip();
            if (isDouble) { OnDoubleClick(); return; }
            _clickTimer.Start(); // 单击延迟弹气泡
        };
        _window.LostMouseCapture += (_, _) => { _pressed = false; _moved = false; };
    }

    /// <summary>双击动作：开启"双击释放内存"时真实回收物理内存，并气泡反馈；未开启则提示到何处开启。</summary>
    private void OnDoubleClick()
    {
        if (!App.ReleaseOnDouble)
        {
            ShowTipText("未开启：设置 → 通用 → 双击释放内存", 2.2);
            return;
        }
        try { MemoryRelease.ReleaseAll(); } catch { }
        ShowTipText("已释放内存", 1.8);
    }

    private void ShowTip() => ShowTipText(null, 3.2);

    private void ShowTipText(string? text, double seconds)
    {
        if (text is null) UpdateTipText(); else _tipText.Text = text;
        _tip.IsOpen = true;
        var hide = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        hide.Tick += (_, _) => { hide.Stop(); CloseTip(); };
        hide.Start();
    }
    private void CloseTip() => _tip.IsOpen = false;

    private void UpdateTipText()
    {
        var keys = App.GetHudMetrics();
        var parts = new System.Collections.Generic.List<string>();
        if (System.Array.IndexOf(keys, "cpu") >= 0) parts.Add("CPU " + ((int)_cpu).ToString() + "%");
        if (System.Array.IndexOf(keys, "mem") >= 0) parts.Add("内存 " + ((int)_memPct).ToString() + "%");
        if (System.Array.IndexOf(keys, "gpu") >= 0) parts.Add("GPU " + ((int)_gpuPct).ToString() + "%");
        _tipText.Text = parts.Count > 0 ? string.Join("   ", parts) : "未配置显示项";
    }

    private void OnData(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (!r.TryGetProperty("t", out var tv) || tv.ValueKind != JsonValueKind.String) return;
            string type = tv.GetString() ?? "";
            if (type == "fast")
            {
                if (r.TryGetProperty("cpu", out var c) && c.ValueKind == JsonValueKind.Number) _cpu = c.GetDouble();
            }
            else if (type == "tick")
            {
                if (r.TryGetProperty("cpu", out var c) && c.ValueKind == JsonValueKind.Number) _cpu = c.GetDouble();
                if (r.TryGetProperty("mem", out var m) && m.ValueKind == JsonValueKind.Object && m.TryGetProperty("pct", out var p) && p.ValueKind == JsonValueKind.Number) _memPct = p.GetDouble();
                if (r.TryGetProperty("gpu", out var g) && g.ValueKind == JsonValueKind.Object && g.TryGetProperty("pct", out var gp) && gp.ValueKind == JsonValueKind.Number) _gpuPct = gp.GetDouble();
            }
        }
        catch { }
        if (_tip.IsOpen) try { _window.Dispatcher.Invoke(UpdateTipText); } catch { }
    }

    public void ApplyConfig() { }
    public void Show2() { if (!_window.IsVisible) _window.Show(); _window.Activate(); }
    public void Hide() => _window.Hide();
    public bool Visible => _window.IsVisible;
    public void SetOpacity(double value)
    {
        value = Math.Clamp(value, 0.2, 1.0);
        _window.Opacity = value;
        App.Log("桌面宠物透明度 -> " + value.ToString("0.00"));
    }
    public double OpacityNow => _window.Opacity;
    public void CycleOpacity()
    {
        double[] steps = { 1.0, 0.8, 0.5, 0.4 };
        double cur = _window.Opacity;
        double next = 1.0;
        foreach (var s in steps) if (s < cur - 0.02) { next = s; break; }
        if (next >= cur - 0.02) next = steps[0];
        SetOpacity(next);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        App.UnregisterSink(_sink);
        App.AccentChanged -= OnAccentChanged;
        CloseTip();
        _window.Close();
    }
}