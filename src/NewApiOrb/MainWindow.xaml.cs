using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NewApiOrb.Models;
using NewApiOrb.Utils;

namespace NewApiOrb;

public partial class MainWindow : Window
{
    // ------------------------------------------------------------ 视图模型

    private sealed class FieldVm
    {
        public string Label { get; init; } = "";
        public string Value { get; init; } = "";
        public Brush ValueBrush { get; init; } = Brushes.White;
    }

    private sealed class BarVm
    {
        public double BarHeight { get; init; }
        public Brush BarBrush { get; init; } = Brushes.Gray;
    }

    // ------------------------------------------------------------ 科技风配色

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly Brush CyanBrush = Frozen(0x00, 0xE5, 0xFF);   // 主色：青
    private static readonly Brush VioletBrush = Frozen(0x7C, 0x4D, 0xFF); // 辅色：蓝紫
    private static readonly Brush MutedBrush = Frozen(0x5A, 0x6B, 0x7A);  // 下降/次要
    private static readonly Brush TextBrush = Frozen(0xFF, 0xFF, 0xFF);
    private static readonly Brush BarHistoryBrush =
        new SolidColorBrush(Color.FromArgb(0x73, 0x00, 0xE5, 0xFF));
    private static readonly Brush BarEmptyBrush =
        new SolidColorBrush(Color.FromArgb(0x24, 0x00, 0xE5, 0xFF));

    // ------------------------------------------------------------ 常量

    private const double DockGap = 30;      // 距屏幕边缘多少像素内算"贴边"
    private const double DockScale = 0.36;  // 贴边后缩到原尺寸的多少
    private const double AnimMs = 420;      // 贴边 / 恢复动画时长（苹果风 350-450ms）
    private const double ExpandMs = 340;    // 卡片弹出时长（带回弹）
    private const double CollapseMs = 170;  // 卡片缩回时长
    private const double RootMargin = 20;   // 与 XAML 中 Root 的 Margin 保持一致

    // 点击切换的"吸入 / 吐出"参数：球与卡片共用同一个锚点 —— 球心。
    private const double BallSuckScale = 0.35;  // 球被"吸进去"时缩到原尺寸的多少
    private const double CardGrowFrom = 0.45;   // 卡片自球心展开 / 缩回时的起始比例
    private const double SuckMs = 180;          // 球被吸入的时长（EaseIn = 越吸越快）

    // ------------------------------------------------------------ 苹果风缓动

    /// <summary>强 ease-out，接近 iOS cubic-bezier(0.25, 1, 0.5, 1)：起步快、收尾极缓。</summary>
    private static IEasingFunction EaseOut() =>
        new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>带轻微回弹的 ease-out —— 手机 UI 那种"吸附"手感。</summary>
    private static IEasingFunction Spring(double amplitude = 0.1) =>
        new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = amplitude };

    private static IEasingFunction EaseIn() =>
        new CubicEase { EasingMode = EasingMode.EaseIn };

    // ------------------------------------------------------------ 状态

    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(420) };
    private readonly DispatcherTimer _savePosTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };

    /// <summary>
    /// 悬停轮询 —— 这是"鼠标在不在身上"的**唯一可靠判据**。
    /// ⚠️ 不能依赖 MouseLeave：分层窗口的 alpha=0 区域会误报 MouseLeave，
    /// 而 WPF 一旦误判"鼠标已离开"就解除 WM_MOUSELEAVE 跟踪；此后光标真的移出窗口，
    /// 系统**再也不发离开通知** → 卡片永久卡在展开态（实测卡死 2 分钟以上）。
    /// </summary>
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    /// <summary>
    /// 贴边悬停的滞回开关：贴边动画刚结束时鼠标本来就在球上，
    /// 没有这道闸门会立刻反向 Undock，出现"贴边→弹出→贴边"的抖动。
    /// 必须先观察到"鼠标已离开球"才解锁。
    /// </summary>
    private bool _hoverArmed;

    private AppConfig _cfg = new();
    private Snapshot? _snap;
    private bool _expanded;
    private bool _positionReady;

    private Point _mouseScreenStart;
    private Point _windowStart;
    private bool _dragging;

    private bool _docked;          // 是否处于贴边隐藏态
    private int _dockSide;         // 0 未贴边 / -1 贴左 / 1 贴右
    private double _preDockLeft;
    private double _preDockTop;
    private bool _posAnimating;    // 窗口位置动画进行中（此时不持久化坐标）
    private int _posToken;         // 位移动画令牌：摘掉动画后其 Completed 仍会触发，靠它作废
    private int _animGen;          // 切换代际：每次展开/收起/贴边递增，用来作废上一轮遗留的 DispatcherTimer

    // ---- 动画帧率诊断（ORB_TRACE=1 时启用，用于验证"丝滑"是实测而非感觉）----
    private static readonly bool TraceEnabled =
        Environment.GetEnvironmentVariable("ORB_TRACE") == "1";

    // 性能对照：ORB_NOPOS=1 时跳过窗口位移动画（只留缩放），用于定位掉帧来源
    private static readonly bool NoPosAnim =
        Environment.GetEnvironmentVariable("ORB_NOPOS") == "1";

    private readonly List<double> _frameGaps = new();
    private long _lastFrameTicks;

    public MainWindow()
    {
        InitializeComponent();

        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (TraceEnabled) Diag($"COLLAPSE_TICK expanded={_expanded} dockSide={_dockSide} docked={_docked}");

            // 展开态：只收起卡片。别顺手把球也贴边 —— 用户只是把指针移开了而已。
            if (_expanded) { SetExpanded(false); return; }

            if (_dockSide != 0 && !_docked) Dock();
        };

        _savePosTimer.Tick += (_, _) =>
        {
            _savePosTimer.Stop();
            PersistPosition();
        };

        Loaded += OnLoaded;
        Closed += OnClosed;
        LocationChanged += (_, _) =>
        {
            if (TraceEnabled && !_posAnimating) Diag($"LOCCHG Left={Left:F1}");
            if (!_positionReady || _docked || _posAnimating) return;
            _savePosTimer.Stop();
            _savePosTimer.Start();
        };

        BallHost.MouseLeftButtonDown += OnBallMouseDown;
        BallHost.MouseMove += OnBallMouseMove;
        BallHost.MouseLeftButtonUp += OnBallMouseUp;

        // MouseEnter/MouseLeave 只作快路径（能拿到就立刻响应），真正的判据是 _hoverTimer 轮询。
        MouseEnter += (_, _) => ReevaluateHover();
        MouseLeave += (_, _) =>
        {
            if (TraceEnabled) Diag($"MLEAVE pos={Mouse.GetPosition(this)} AW={ActualWidth:F0} AH={ActualHeight:F0}");
            ReevaluateHover();
        };
        _hoverTimer.Tick += (_, _) => ReevaluateHover();
    }

    /// <summary>
    /// 鼠标是否仍在窗口矩形内。用来兜住分层窗口透明区的假 MouseLeave。
    /// ⚠️ 不能用 <c>Mouse.GetPosition</c> —— WPF 缓存了鼠标位置，MouseLeave 触发时它返回的
    /// 还是"离开前"的旧坐标（实测指针已移到 (60,60)，它却报 pos=284,76），判断必然为真。
    /// 必须走 Win32 实时坐标。
    /// </summary>
    private bool IsCursorInsideWindow()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return false;

        var pos = System.Windows.Forms.Cursor.Position;   // 屏幕物理像素，实时
        var tl = PointToScreen(new Point(0, 0));          // 窗口左上角，物理像素
        var dpi = VisualTreeHelper.GetDpi(this);

        var w = ActualWidth * dpi.DpiScaleX;
        var h = ActualHeight * dpi.DpiScaleY;

        return pos.X >= tl.X && pos.X <= tl.X + w
            && pos.Y >= tl.Y && pos.Y <= tl.Y + h;
    }

    /// <summary>
    /// 鼠标是否落在球体**可见**矩形内（物理像素）。
    /// 贴边态下球被缩到 36%、窗口大部分是空透明区 —— 此时窗口矩形完全不代表球的可见范围，
    /// 必须单独量。PointToScreen 会带上 RenderTransform，拿到的就是缩放后的真实矩形。
    /// </summary>
    private bool IsCursorInsideBall()
    {
        if (BallHost.ActualWidth <= 0 || BallHost.ActualHeight <= 0) return false;

        double x1, x2, y1, y2;
        try
        {
            var a = BallHost.PointToScreen(new Point(0, 0));
            var b = BallHost.PointToScreen(new Point(BallHost.ActualWidth, BallHost.ActualHeight));
            x1 = Math.Min(a.X, b.X); x2 = Math.Max(a.X, b.X);
            y1 = Math.Min(a.Y, b.Y); y2 = Math.Max(a.Y, b.Y);
        }
        catch (InvalidOperationException) { return false; }   // 尚未接入呈现源

        var dpi = VisualTreeHelper.GetDpi(this);
        var pad = 6 * dpi.DpiScaleX;    // 略微外扩，边缘抖动不至于闪断

        var pos = System.Windows.Forms.Cursor.Position;
        return pos.X >= x1 - pad && pos.X <= x2 + pad
            && pos.Y >= y1 - pad && pos.Y <= y2 + pad;
    }

    /// <summary>
    /// 悬停状态机 —— 由 100ms 轮询驱动，是"鼠标在不在身上"的唯一权威判据。
    /// 用事件驱动的老写法有个致命洞：分层窗口透明区误报 MouseLeave → WPF 解除
    /// WM_MOUSELEAVE 跟踪 → 光标真移出时通知不再送达 → 卡片卡死。轮询没有这个问题。
    /// </summary>
    private void ReevaluateHover()
    {
        if (!_positionReady || !IsLoaded || ActualWidth <= 0) return;

        // 拖拽中（含鼠标捕获）一律冻结状态机，免得把拖拽当成"移开"
        if (_dragging || BallHost.IsMouseCaptured) return;

        // ① 贴边态：只有悬停到球体上才恢复。滞回闸门兜住"刚贴完边鼠标还在球上"的情况。
        if (_docked)
        {
            if (!IsCursorInsideBall()) { _hoverArmed = true; return; }
            if (_hoverArmed) { _collapseTimer.Stop(); Undock(); }
            return;
        }

        // ② 位移动画进行中不动手 —— 否则 Undock 的窗口还在飞，倒计时就把 Dock 拽回来
        if (_posAnimating) { _collapseTimer.Stop(); return; }

        // ③ 光标在窗口内 → 取消收起倒计时
        if (IsCursorInsideWindow()) { _collapseTimer.Stop(); return; }

        // ④ 光标在窗口外 → 起倒计时（只在"展开态"或"贴边待命"时需要）
        if (!_expanded && _dockSide == 0) return;
        if (_collapseTimer.IsEnabled) return;

        if (TraceEnabled) Diag($"HOVEROUT expanded={_expanded} dockSide={_dockSide}");
        _collapseTimer.Start();
    }

    // ------------------------------------------------------------ 生命周期

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        _cfg = app.Config;
        app.Metrics.Updated += OnMetricsUpdated;

        // 性能对照开关：ORB_NOFX=1 时剥掉所有模糊效果，用于定位掉帧来源
        if (Environment.GetEnvironmentVariable("ORB_NOFX") == "1")
        {
            Core.Effect = null;
            BallValue.Effect = null;
            CardHost.Effect = null;
        }

        ApplyConfig(_cfg);

        Dispatcher.BeginInvoke(new Action(() =>
        {
            RestorePosition();
            _positionReady = true;
            _hoverArmed = false;        // 启动时鼠标在不在球上都先上闸，避免一上来就误恢复
            _hoverTimer.Start();
        }), DispatcherPriority.Loaded);

        OnMetricsUpdated(app.Metrics.Latest);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (Application.Current is App app)
            app.Metrics.Updated -= OnMetricsUpdated;

        PersistPosition();
    }

    private void PersistPosition()
    {
        if (!_positionReady || _docked) return;
        _cfg.WindowLeft = Left;
        _cfg.WindowTop = Top;
        Services.ConfigService.Save(_cfg);
    }

    // ------------------------------------------------------------ 配置

    public void ApplyConfig(AppConfig cfg)
    {
        _cfg = cfg;

        Topmost = cfg.Topmost;
        Opacity = Math.Clamp(cfg.Opacity, 0.2, 1.0);
        MiTopmost.IsChecked = cfg.Topmost;
        MiDock.IsChecked = cfg.DockEnabled;

        if (_docked) Undock();

        ApplyBallSize(cfg.BallSize);
        CardHost.Width = cfg.CardWidth;

        Render();
    }

    private void ApplyBallSize(double size)
    {
        BallHost.Width = size;
        BallHost.Height = size;

        // 各层同心环按比例内缩
        Arc1.Margin = new Thickness(Math.Max(1, size * 0.012));
        Arc2.Margin = new Thickness(size * 0.062);
        TickRing.Margin = new Thickness(size * 0.115);
        Core.Margin = new Thickness(size * 0.175);

        BallLabel.FontSize = Math.Max(9, size * 0.098);
        BallValue.FontSize = Math.Max(12, size * 0.225);
        BallDelta.FontSize = Math.Max(9, size * 0.092);
    }

    // ------------------------------------------------------------ 渲染

    private void OnMetricsUpdated(Snapshot snap)
    {
        if (Dispatcher.HasShutdownStarted) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _snap = snap;
            Render();
        }));
    }

    private void Render()
    {
        if (_snap is null) return;
        RenderBall(_snap);
        RenderCard(_snap);
    }

    private void RenderBall(Snapshot s)
    {
        var meta = MetricFields.Get(_cfg.PrimaryField);
        BallLabel.Text = meta.Label;
        BallValue.Text = ValueOf(s, _cfg.PrimaryField);

        if (!s.IsOnline)
        {
            BallValue.Text = "离线";
            BallValue.Foreground = MutedBrush;
            BallDelta.Visibility = Visibility.Collapsed;
            ToolTip = s.Error;
            return;
        }

        BallValue.Foreground = TextBrush;
        ToolTip = $"{s.Username} · 更新于 {s.FetchedAt:HH:mm:ss}";

        RenderBallSub(s);
    }

    /// <summary>
    /// 球体副指标（主指标下方那一行）。可选值见 <see cref="MetricFields.SubFields"/>。
    /// 取不到数据时整行隐藏，不留一个只写着单位的占位。
    /// </summary>
    private void RenderBallSub(Snapshot s)
    {
        switch (_cfg.BallSubField)
        {
            case MetricFields.YesterdayCompare when s.DayOverDay is { } dod:
                SetBallDelta(Fmt.Percent(dod) + " 环比", dod >= 0 ? VioletBrush : MutedBrush);
                break;

            case MetricFields.StreamSpeedLast1 when s.StreamSpeedLast1 is { } v:
                SetBallDelta(Fmt.Speed(v), CyanBrush);
                break;

            case MetricFields.StreamSpeedLast3 when s.StreamSpeedLast3 is { } v:
                SetBallDelta(Fmt.Speed(v), CyanBrush);
                break;

            case MetricFields.LatencyLast1 when s.LatencyLast1 is { } t:
                SetBallDelta(Fmt.Seconds(t), CyanBrush);
                break;

            case MetricFields.LatencyLast3 when s.LatencyLast3 is { } t:
                SetBallDelta(Fmt.Seconds(t), CyanBrush);
                break;

            default:
                SetBallDelta(null, CyanBrush);   // 不显示，或数据还没到位
                break;
        }
    }

    private void SetBallDelta(string? text, Brush brush)
    {
        if (string.IsNullOrEmpty(text))
        {
            BallDelta.Visibility = Visibility.Collapsed;
            return;
        }

        BallDelta.Text = text;
        BallDelta.Foreground = brush;
        BallDelta.Visibility = Visibility.Visible;
    }

    private void RenderCard(Snapshot s)
    {
        CardUpdated.Text = s.IsOnline ? $"{s.FetchedAt:HH:mm:ss} 刷新" : "离线";

        var rows = new List<FieldVm>();
        foreach (var key in _cfg.VisibleFields)
        {
            var meta = MetricFields.Get(key);
            var brush = TextBrush;

            if (key == MetricFields.YesterdayCompare && s.DayOverDay is { } dod)
                brush = dod >= 0 ? VioletBrush : MutedBrush;

            rows.Add(new FieldVm
            {
                Label = meta.Label,
                Value = ValueOf(s, key),
                ValueBrush = brush,
            });
        }
        FieldList.ItemsSource = rows;

        RenderChart(s);
        RenderTokens(s);
    }

    private void RenderTokens(Snapshot s)
    {
        if (s.Tokens.Count == 0)
        {
            TokenHost.Visibility = Visibility.Collapsed;
            return;
        }

        TokenHost.Visibility = Visibility.Visible;

        TokenList.ItemsSource = s.Tokens
            .Take(6)
            .Select(t => new FieldVm
            {
                Label = t.TokenName,
                Value = Fmt.Auto(t.Tokens),
                ValueBrush = CyanBrush,
            })
            .ToList();
    }

    private void RenderChart(Snapshot s)
    {
        if (!_cfg.ShowHourlyChart || s.Hours.Count == 0)
        {
            ChartHost.Visibility = Visibility.Collapsed;
            return;
        }

        ChartHost.Visibility = Visibility.Visible;

        const double maxHeight = 52;
        var peak = s.Hours.Max(h => h.Tokens);
        var nowHour = DateTime.Now.Hour;

        var bars = new List<BarVm>();
        foreach (var h in s.Hours)
        {
            var ratio = peak > 0 ? (double)h.Tokens / peak : 0;
            var height = h.Tokens > 0 ? Math.Max(3, ratio * maxHeight) : 2;

            bars.Add(new BarVm
            {
                BarHeight = height,
                BarBrush = h.Tokens <= 0
                    ? BarEmptyBrush
                    : (h.Hour == nowHour ? CyanBrush : BarHistoryBrush),
            });
        }
        HourChart.ItemsSource = bars;
    }

    private static string ValueOf(Snapshot s, string key) => key switch
    {
        MetricFields.TodayTotalTokens => Fmt.Auto(s.TodayTotalTokens),
        MetricFields.TodayPromptTokens => Fmt.Auto(s.TodayPromptTokens),
        MetricFields.TodayCompletionTokens => Fmt.Auto(s.TodayCompletionTokens),
        MetricFields.TodayCacheTokens => Fmt.Auto(s.TodayCacheTokens),
        MetricFields.TodayRequests => Fmt.Group(s.TodayRequests),
        MetricFields.TodayQuota => Fmt.Compact(s.TodayQuota),
        MetricFields.TopModel => s.TopModel,
        MetricFields.TopToken => s.TopToken,
        MetricFields.ActiveTokens => s.ActiveTokenCount.ToString(),
        MetricFields.AvgLatency => Fmt.Seconds(s.AvgLatency),
        MetricFields.StreamSpeedLast1 => s.StreamSpeedLast1 is { } v1 ? Fmt.Speed(v1) : "—",
        MetricFields.StreamSpeedLast3 => s.StreamSpeedLast3 is { } v3 ? Fmt.Speed(v3) : "—",
        MetricFields.LatencyLast1 => s.LatencyLast1 is { } t1 ? Fmt.Seconds(t1) : "—",
        MetricFields.LatencyLast3 => s.LatencyLast3 is { } t3 ? Fmt.Seconds(t3) : "—",
        MetricFields.Balance => Fmt.Compact(s.Balance),
        MetricFields.UsedQuota => Fmt.Compact(s.UsedQuota),
        MetricFields.TotalRequests => Fmt.Group(s.TotalRequests),
        MetricFields.YesterdayCompare => s.DayOverDay is { } d ? Fmt.Percent(d) : "—",
        _ => "—",
    };

    // ------------------------------------------------------------ 动效基建

    /// <summary>
    /// 缩放动画。只动 RenderTransform，不碰布局 / 窗口尺寸 —— 走 GPU 合成，这是丝滑的关键。
    /// 以"当前有效值"为起点，连续触发也不会跳。
    /// </summary>
    private void AnimateScale(ScaleTransform t, double to, double ms,
                              IEasingFunction ease, Action? onDone = null)
    {
        var from = t.ScaleX;   // 含动画中的中间值

        t.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        t.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        var dur = TimeSpan.FromMilliseconds(ms);
        var ax = new DoubleAnimation(from, to, dur) { EasingFunction = ease };
        var ay = new DoubleAnimation(from, to, dur) { EasingFunction = ease };

        ax.Completed += (_, _) =>
        {
            if (TraceEnabled) DumpTrace();
            onDone?.Invoke();
        };

        if (TraceEnabled) StartTrace();
        t.BeginAnimation(ScaleTransform.ScaleXProperty, ax);
        t.BeginAnimation(ScaleTransform.ScaleYProperty, ay);
    }

    /// <summary>
    /// 窗口位移动画。窗口尺寸全程不变，只改 Left/Top —— 避免 SizeToContent 重排。
    /// </summary>
    private void AnimateWindowPos(double toLeft, double toTop, double ms,
                                  IEasingFunction ease, Action? onDone = null)
    {
        if (NoPosAnim)          // 对照实验：跳过位移，只留缩放
        {
            Left = toLeft;
            Top = toTop;
            onDone?.Invoke();
            return;
        }

        var fromLeft = Left;
        var fromTop = Top;

        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);

        var dur = TimeSpan.FromMilliseconds(ms);
        var ax = new DoubleAnimation(fromLeft, toLeft, dur) { EasingFunction = ease };
        var ay = new DoubleAnimation(fromTop, toTop, dur) { EasingFunction = ease };

        // ⚠️ BeginAnimation(dp, null) 并**不能**阻止已经排上队的 Completed 回调 ——
        //    实测把动画摘掉之后它到点照样触发，把 Left 写回旧终值（卡片直接飞出屏幕）。
        //    所以这里发一个令牌，摘动画时令牌失效，回调自己认领不到就退出。
        var token = ++_posToken;

        ax.Completed += (_, _) =>
        {
            if (token != _posToken) return;     // 已被 SettleWindowPos / 新动画作废
            if (TraceEnabled) Diag($"POSDONE toLeft={toLeft:F1} curLeft={Left:F1}");
            // 先落定基础值，再摘掉动画，避免中间帧闪回
            Left = toLeft;
            Top = toTop;
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            _posAnimating = false;
            onDone?.Invoke();
        };

        _posAnimating = true;
        BeginAnimation(LeftProperty, ax);
        BeginAnimation(TopProperty, ay);
    }

    /// <summary>
    /// 把进行中的窗口位移动画"就地冻结"。
    /// ⚠️ 只摘动画不写回本地值，属性会回落到**动画起点** —— 窗口瞬移回去。
    /// 所以必须先读当前（动画）值，再摘动画，最后写成本地值。
    /// </summary>
    private void SettleWindowPos()
    {
        if (!_posAnimating) return;
        if (TraceEnabled) Diag($"SETTLE Left={Left:F1}");

        _posToken++;        // 作废正在跑的位移动画 —— 它的 Completed 仍会触发

        var l = Left;      // DP getter 返回的是动画计算后的当前值，不是本地值
        var t = Top;

        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);

        Left = l;
        Top = t;
        _posAnimating = false;
    }

    // ---- 帧率诊断 -------------------------------------------------

    private void StartTrace()
    {
        _frameGaps.Clear();
        _lastFrameTicks = 0;
        CompositionTarget.Rendering += OnTraceFrame;
    }

    private void OnTraceFrame(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastFrameTicks != 0)
            _frameGaps.Add((now - _lastFrameTicks) * 1000.0 / Stopwatch.Frequency);
        _lastFrameTicks = now;
    }

    private void DumpTrace()
    {        CompositionTarget.Rendering -= OnTraceFrame;
        if (_frameGaps.Count == 0) return;

        var sorted = _frameGaps.OrderBy(x => x).ToList();
        var p95 = sorted[(int)(sorted.Count * 0.95)];
        var jitter = _frameGaps.Count(g => g > 25);   // 掉到 40fps 以下的帧数

        var line = $"{DateTime.Now:HH:mm:ss}  frames={_frameGaps.Count,3}  " +
                   $"avg={_frameGaps.Average(),5:F1}ms  p95={p95,5:F1}ms  " +
                   $"max={_frameGaps.Max(),5:F1}ms  >25ms={jitter}\n";

        try
        {
            var dir = Path.GetDirectoryName(Services.ConfigService.FilePath)!;
            File.AppendAllText(Path.Combine(dir, "anim-trace.log"), line);
        }
        catch { /* 诊断失败不影响主流程 */ }
    }

    /// <summary>临时诊断：把几何量写盘（ORB_TRACE=1 时启用）。</summary>
    private static void Diag(string msg)
    {
        try
        {
            var dir = Path.GetDirectoryName(Services.ConfigService.FilePath)!;
            File.AppendAllText(Path.Combine(dir, "diag.log"), $"{DateTime.Now:HH:mm:ss.fff}  {msg}\n");
        }
        catch { }
    }

    private static void Fade(UIElement el, double to, double ms,
                             IEasingFunction? ease = null, double delayMs = 0)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease ?? EaseOut(),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
        };
        el.BeginAnimation(OpacityProperty, a);
    }

    /// <summary>停掉元素上的 Opacity 动画并归位。</summary>
    private static void ResetFade(UIElement el, double opacity = 1)
    {
        el.BeginAnimation(OpacityProperty, null);
        el.Opacity = opacity;
    }

    // ------------------------------------------------------------ 展开 / 收起

    /// <summary>
    /// 切换球 / 卡片可见性（不动画），并锁住窗口右边缘 ——
    /// SizeToContent 变尺寸时左上角会固定，不修正的话内容会整块往右跑。
    /// </summary>
    private void SwitchVisual(bool expanded, double rightEdge)
    {
        _expanded = expanded;

        BallHost.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        CardHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;

        UpdateLayout();

        SettleWindowPos();                // 有未跑完的位移动画先冻结，否则下面这行会被动画值压掉
        Left = rightEdge - ActualWidth;   // 右边缘钉死
        KeepOnScreen();
    }

    /// <summary>清掉球体上的过渡状态（透明度 / 缩放 / 原点）；保留当前缩放值，避免跳变。</summary>
    private void ResetBallVisual()
    {
        ResetFade(BallHost);
        ResetFade(BallContent);

        var cur = BallScale.ScaleX;
        BallScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        BallScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        BallScale.ScaleX = BallScale.ScaleY = cur;

        BallHost.RenderTransformOrigin = new Point(0.5, 0.5);
    }

    private void ResetCardVisual()
    {
        ResetFade(CardHost);

        var cur = CardScale.ScaleX;
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CardScale.ScaleX = CardScale.ScaleY = cur;

        CardHost.RenderTransformOrigin = new Point(0.5, 0.5);
    }

    /// <summary>
    /// 把卡片的缩放原点设到「球心」在卡片坐标系中的位置 —— 这是"被吸进去"的锚点。
    /// 几何：球心距窗口右边缘 = RootMargin + BallSize/2，距上边缘同理；
    /// 卡片在 Root 内再内缩 RootMargin，所以球心距卡片右边 = BallSize/2、距卡片上边 = BallSize/2。
    /// </summary>
    private void ApplyCardOriginAtBall()
    {
        var w = CardHost.ActualWidth > 0 ? CardHost.ActualWidth : _cfg.CardWidth;
        var h = CardHost.ActualHeight;

        if (w <= 0 || h <= 0)
        {
            CardHost.RenderTransformOrigin = new Point(1, 0);   // 量不到就退回右上角
            return;
        }

        var half = _cfg.BallSize / 2.0;
        CardHost.RenderTransformOrigin = new Point(
            Math.Clamp((w - half) / w, 0, 1),
            Math.Clamp(half / h, 0, 1));
    }

    /// <summary>
    /// 无动画地结束"贴边恢复中"的状态：球直接落定到满尺寸、缩放原点复位。
    /// 贴边时用户点得太快（Undock 的放大动画还没跑完），球会停在中间尺寸、
    /// 缩放原点还挂在贴边那一侧 —— 此时直接展开会跳，必须先归位。
    /// 窗口位置不在这里改，交给 SettleWindowPos 冻结当前值就行。
    /// </summary>
    private void UndockHard()
    {
        _docked = false;
        _dockSide = 0;      // 球已离开贴边位置：后续 MouseLeave 只该收起卡片，不该再触发 Dock

        BallScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        BallScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        BallScale.ScaleX = BallScale.ScaleY = 1.0;

        BallHost.RenderTransformOrigin = new Point(0.5, 0.5);
        ResetFade(BallContent);

        // 位置一并复位 —— Undock 的位移动画已被 SettleWindowPos 冻结在中间态，
        // 不补这一步的话球会停在"半贴边"的位置上。
        Left = _preDockLeft;
        Top = _preDockTop;
        KeepOnScreen();
    }

    /// <summary>
    /// 点击切换。球与卡片共用同一个锚点 —— 球心，而且两者是**重叠**进行的：
    /// 展开时卡片自球心长大、球同时缩向球心被卡片吞掉；收起反向，球从球心"吐"出来。
    ///
    /// 窗口尺寸切换之所以不闪：球是 Right/Top 对齐的，窗口变宽只是向左扩，
    /// 球的**屏幕坐标纹丝不动**（实测球态 152 与卡片态 360 两种窗口下球心落在同一像素）。
    /// </summary>
    private void SetExpanded(bool on)
    {
        if (_expanded == on) return;
        _expanded = on;

        // 作废上一轮遗留的 timer —— 否则它会按"上一轮的状态"去动球 / 卡片。
        var gen = ++_animGen;

        // 贴边恢复的位移动画若还在跑，先就地冻结：不然下面的 Left 赋值会被动画值压掉，
        // 等动画收尾时又把窗口拽回贴边前的位置，卡片直接错位。
        // 判据用"缩放原点"而不是 _posAnimating —— 后者可能已被 OnBallMouseDown 提前清掉。
        var wasDocking = _docked || Math.Abs(BallHost.RenderTransformOrigin.X - 0.5) > 0.001;
        SettleWindowPos();
        if (wasDocking) UndockHard();

        var rightEdge = Left + ActualWidth;

        if (on)
        {
            if (TraceEnabled) Diag($"E1 begin rightEdge={rightEdge:F1} Left={Left:F1} AW={ActualWidth:F1} wa={SystemParameters.WorkArea}");

            // ① 窗口先切到卡片尺寸（球位置不变，视觉无感）。
            //    卡片先以 Hidden 参与布局 —— 拿到真实高度才能算准球心锚点。
            CardHost.Visibility = Visibility.Hidden;
            UpdateLayout();
            if (TraceEnabled) Diag($"E2 afterHidden AW={ActualWidth:F1} AH={ActualHeight:F1}");

            ApplyCardOriginAtBall();
            CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CardScale.ScaleX = CardScale.ScaleY = CardGrowFrom;
            // ⚠️ 不能从 0 起：会和球的淡出叠出一段"两边都看不见"的空窗（实测 80ms）
            ResetFade(CardHost, 0.25);

            CardHost.Visibility = Visibility.Visible;
            UpdateLayout();
            if (TraceEnabled) Diag($"E3 afterVisible AW={ActualWidth:F1} AH={ActualHeight:F1} Left={Left:F1}");

            Left = rightEdge - ActualWidth;
            if (TraceEnabled) Diag($"E4 afterLeftSet Left={Left:F1}");

            KeepOnScreen();
            if (TraceEnabled)
            {
                Diag($"E5 afterKeep Left={Left:F1} AW={ActualWidth:F1}");
                Dispatcher.BeginInvoke(new Action(() =>
                    Diag($"E6 next-dispatch Left={Left:F1} AW={ActualWidth:F1}")),
                    DispatcherPriority.Background);
                var t600 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                t600.Tick += (_, _) => { t600.Stop(); Diag($"E7 +600ms Left={Left:F1} AW={ActualWidth:F1}"); };
                t600.Start();
            }

            // ② 卡片自球心长大
            AnimateScale(CardScale, 1.0, ExpandMs, Spring(0.18));
            Fade(CardHost, 1, 220);

            // ③ 球同时缩向球心 + 淡出 —— 被卡片吞掉。
            //    EaseIn = 越吸越快，像被吸力拽走；EaseOut 会显得"飘"。
            BallHost.RenderTransformOrigin = new Point(0.5, 0.5);
            AnimateScale(BallScale, BallSuckScale, SuckMs, EaseIn());
            Fade(BallHost, 0, SuckMs, EaseIn());

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SuckMs + 20) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (gen != _animGen) return;                  // 已被后续切换作废
                BallHost.Visibility = Visibility.Collapsed;   // 已淡到 0，藏起来无感
                ResetBallVisual();
            };
            timer.Start();
        }
        else
        {
            // ① 球先在"被吸进去"的尺寸待命（此刻它还是 Collapsed，改属性不渲染）
            BallHost.Visibility = Visibility.Hidden;
            BallHost.RenderTransformOrigin = new Point(0.5, 0.5);
            BallScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            BallScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            BallScale.ScaleX = BallScale.ScaleY = BallSuckScale;
            ResetFade(BallHost, 0.25);
            BallHost.Visibility = Visibility.Visible;

            // ② 球从球心"吐"出来（带回弹）+ 卡片缩回球心淡出，同时进行
            AnimateScale(BallScale, 1.0, ExpandMs + 80, Spring(0.14));
            Fade(BallHost, 1, 240);

            ApplyCardOriginAtBall();
            AnimateScale(CardScale, CardGrowFrom, CollapseMs, EaseIn());
            Fade(CardHost, 0, CollapseMs, EaseIn());

            // ③ 卡片缩完就藏起来，窗口切回球尺寸 —— 球已接近满尺寸，屏幕位置不变
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CollapseMs + 20) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (gen != _animGen) return;                  // 已被后续切换作废

                CardHost.Visibility = Visibility.Collapsed;
                ResetCardVisual();

                UpdateLayout();
                Left = rightEdge - ActualWidth;
                KeepOnScreen();
            };
            timer.Start();
        }
    }

    // ------------------------------------------------------------ 拖拽

    private void OnBallMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (TraceEnabled) Diag($"MDOWN at={e.GetPosition(this)} docked={_docked}");

        if (_docked) Undock();

        // 位移动画还没跑完就开拖，先就地落定，免得被动画拽回去
        SettleWindowPos();

        _mouseScreenStart = PointToScreen(e.GetPosition(this));
        _windowStart = new Point(Left, Top);
        _dragging = false;
        BallHost.CaptureMouse();
    }

    private void OnBallMouseMove(object sender, MouseEventArgs e)
    {
        if (!BallHost.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;

        var cur = PointToScreen(e.GetPosition(this));
        var dx = cur.X - _mouseScreenStart.X;
        var dy = cur.Y - _mouseScreenStart.Y;

        if (!_dragging && (Math.Abs(dx) > 4 || Math.Abs(dy) > 4))
        {
            _dragging = true;
            if (TraceEnabled) Diag($"MMOVE drag-start dx={dx:F0} dy={dy:F0}");
        }

        if (!_dragging) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        Left = _windowStart.X + dx / dpi.DpiScaleX;
        Top = _windowStart.Y + dy / dpi.DpiScaleY;
    }

    private void OnBallMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (TraceEnabled) Diag($"MUP dragging={_dragging}");

        BallHost.ReleaseMouseCapture();

        if (!_dragging)
        {
            SetExpanded(!_expanded);
        }
        else
        {
            _savePosTimer.Stop();
            // ⚠️ 拖拽全程不夹边界（用户中途可以自由摆放），但松手时必须收回来 ——
            // 否则球会被留在屏幕外一截，而且贴边基准坐标 _preDockLeft 也跟着落在屏外，
            // 之后悬停恢复会把窗口"恢复"到屏外去。
            KeepOnScreen();
            PersistPosition();
            EvaluateDock();
        }

        _dragging = false;
    }

    // ------------------------------------------------------------ 贴边隐藏

    /// <summary>拖拽结束后判断是否贴边。</summary>
    private void EvaluateDock()
    {
        if (!_cfg.DockEnabled)
        {
            _dockSide = 0;
            return;
        }

        var wa = SystemParameters.WorkArea;
        var w = ActualWidth;

        var gapLeft = Left - wa.Left;
        var gapRight = wa.Right - (Left + w);

        if (gapRight <= DockGap) _dockSide = 1;
        else if (gapLeft <= DockGap) _dockSide = -1;
        else _dockSide = 0;

        // 贴边后立刻收起（鼠标移入时再恢复）
        if (_dockSide != 0) Dock();
    }

    /// <summary>
    /// 贴边隐藏：窗口尺寸不变，只做「位移 + RenderTransform 缩放」两条并行动画。
    /// 缩放原点切到贴边那一侧，球缩小时始终咬住屏幕边缘。
    /// </summary>
    private void Dock()
    {
        if (_docked || _dockSide == 0) return;
        _docked = true;
        _hoverArmed = false;        // 刚贴边时鼠标还压在球上，等它离开再解锁悬停恢复

        _animGen++;                 // 作废进行中的切换 timer（展开的"把球藏起来"那一步尤其危险）

        // 正展开着就先无动画切回球体，再记基准坐标
        if (_expanded)
        {
            CardHost.Visibility = Visibility.Collapsed;   // 先藏再复位，避免可见状态下原点跳变
            ResetCardVisual();
            SwitchVisual(false, Left + ActualWidth);
        }
        ResetBallVisual();          // 球可能还停在"吸入淡出"的中间态

        _preDockLeft = Left;
        _preDockTop = Top;

        var wa = SystemParameters.WorkArea;
        var w = ActualWidth;
        var h = ActualHeight;

        // 缩放原点：贴右 → 右中；贴左 → 左中。scale=1 时改原点视觉无变化。
        BallHost.RenderTransformOrigin = _dockSide > 0
            ? new Point(1, 0.5)
            : new Point(0, 0.5);

        // 窗口边缘略微探出屏幕，让球（RootMargin 内缩）正好贴住边缘
        var targetLeft = _dockSide > 0
            ? wa.Right - w + RootMargin
            : wa.Left - RootMargin;
        var targetTop = Math.Clamp(Top, wa.Top, Math.Max(wa.Top, wa.Bottom - h));

        // 缩到 36% 后文字糊成一团，淡出只留发光环
        Fade(BallContent, 0, 150, EaseOut());

        AnimateWindowPos(targetLeft, targetTop, AnimMs, EaseOut());
        AnimateScale(BallScale, DockScale, AnimMs, EaseOut());
    }

    private void Undock()
    {
        if (!_docked) return;
        _docked = false;

        Fade(BallContent, 1, 200, EaseOut(), delayMs: 150);

        // 恢复时带一点回弹，比单纯放大更有"手机感"
        AnimateScale(BallScale, 1.0, AnimMs + 60, Spring(0.1), onDone: () =>
        {
            // 放大结束后原点复位（此时 scale=1，视觉无变化）
            if (!_docked) BallHost.RenderTransformOrigin = new Point(0.5, 0.5);
        });

        AnimateWindowPos(_preDockLeft, _preDockTop, AnimMs, EaseOut());
    }

    // ------------------------------------------------------------ 位置

    private void RestorePosition()
    {
        if (_cfg.WindowLeft is { } l && _cfg.WindowTop is { } t)
        {
            Left = l;
            Top = t;
            KeepOnScreen();
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth - 6;
            Top = wa.Bottom - ActualHeight - 6;
        }

        // 启动时就判定一次贴边状态（不立即隐藏，等鼠标移开）
        if (_cfg.DockEnabled)
        {
            var wa2 = SystemParameters.WorkArea;
            var gapRight = wa2.Right - (Left + ActualWidth);
            var gapLeft = Left - wa2.Left;
            _dockSide = gapRight <= DockGap ? 1 : gapLeft <= DockGap ? -1 : 0;
        }
    }

    private void KeepOnScreen()
    {
        var wa = SystemParameters.WorkArea;
        var w = ActualWidth > 0 ? ActualWidth : Width;
        var h = ActualHeight > 0 ? ActualHeight : Height;

        if (double.IsNaN(w) || double.IsNaN(h)) return;

        if (Left + w > wa.Right) Left = wa.Right - w;
        if (Top + h > wa.Bottom) Top = wa.Bottom - h;
        if (Left < wa.Left) Left = wa.Left;
        if (Top < wa.Top) Top = wa.Top;
    }

    // ------------------------------------------------------------ 菜单

    private void OnToggleClick(object sender, RoutedEventArgs e) => SetExpanded(!_expanded);

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
            await app.Metrics.RefreshNowAsync();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
            app.OpenSettings();
    }

    private void OnTopmostClick(object sender, RoutedEventArgs e)
    {
        _cfg.Topmost = MiTopmost.IsChecked;
        Topmost = _cfg.Topmost;
        Services.ConfigService.Save(_cfg);
    }

    private void OnDockToggleClick(object sender, RoutedEventArgs e)
    {
        _cfg.DockEnabled = MiDock.IsChecked;
        Services.ConfigService.Save(_cfg);

        if (!_cfg.DockEnabled)
        {
            _dockSide = 0;
            Undock();
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e) =>
        Application.Current.Shutdown();
}
