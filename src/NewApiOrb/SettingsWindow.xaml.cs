using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NewApiOrb.Models;
using NewApiOrb.Services;

namespace NewApiOrb;

public partial class SettingsWindow : Window
{
    private readonly ObservableCollection<FieldItem> _fields = new();

    /// <summary>
    /// 载入控件初值时置位。否则在 <see cref="LoadFromConfig"/> 里给 ComboBox / Slider 赋值
    /// 会逐个触发变更事件，把"还没初始化完"的中间状态刷到球上
    /// （典型的：SldSize 赋值时 _fields 还是空的 → 卡片瞬间只剩一个字段）。
    /// </summary>
    private bool _loading;

    public SettingsWindow()
    {
        InitializeComponent();

        CmbPrimary.ItemsSource = MetricFields.All.ToList();
        // ⚠️ 不要用 DisplayMemberPath —— SelectionBoxItemTemplate 只认 ItemTemplate，
        // 用 DisplayMemberPath 的话收起态会渲染成 "Meta { Key = ... }"（模板见 SettingsWindow.xaml）
        CmbPrimary.SelectedValuePath = nameof(MetricFields.Meta.Key);

        CmbSub.ItemsSource = MetricFields.SubFields.ToList();
        CmbSub.SelectedValuePath = nameof(MetricFields.Meta.Key);

        FieldItems.ItemsSource = _fields;

        // ---- 实时预览：显示类控件一改就刷到球上（连接类输入框不参与，见 Preview）----
        CmbPrimary.SelectionChanged += (_, _) => Preview();
        CmbSub.SelectionChanged += (_, _) => Preview();
        ChkChart.Click += (_, _) => Preview();
        TxtCardWidth.TextChanged += (_, _) => Preview();

        SldSize.ValueChanged += (_, e) =>
        {
            LblSize.Text = $"{e.NewValue:0} px";
            Preview();
        };
        SldOpacity.ValueChanged += (_, e) =>
        {
            LblOpacity.Text = $"{e.NewValue * 100:0}%";
            Preview();
        };

        // 关闭面板时若没保存过，把球回滚到打开面板前的样子（保存路径已清空快照，这里是空操作）
        Closed += (_, _) => ((App)Application.Current).RevertSettingsPreview();

        LblConfigPath.Text = "配置文件：" + ConfigService.FilePath;

        // 手动居中到主显示器工作区：CenterScreen 在多显示器下会跑到屏幕外
        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + Math.Max(0, (wa.Width - ActualWidth) / 2);
            Top = wa.Top + Math.Max(0, (wa.Height - ActualHeight) / 2);

            // 打开必须停在顶部。实测某个控件会触发 BringIntoView，把窗口滚到中间，
            // 正好把"网关地址 / 访问令牌"这两个必填项藏出视野 ——
            // 首启用户看到的只有"用户 ID / 刷新间隔"，会以为没地方填令牌。
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => Scroller.ScrollToTop()));
        };

        LoadFromConfig();
    }

    private static AppConfig Config => ((App)Application.Current).Config;

    // ------------------------------------------------------------ 载入

    private void LoadFromConfig()
    {
        var cfg = Config;

        _loading = true;
        try
        {
            TxtBaseUrl.Text = cfg.BaseUrl;
            TxtToken.Text = cfg.AccessToken;
            TxtUserId.Text = cfg.UserId.ToString();
            TxtRefresh.Text = cfg.RefreshSeconds.ToString();
            TxtCardWidth.Text = cfg.CardWidth.ToString("0");

            CmbPrimary.SelectedValue = cfg.PrimaryField;
            CmbSub.SelectedValue = cfg.BallSubField;
            ChkChart.IsChecked = cfg.ShowHourlyChart;
            ChkTopmost.IsChecked = cfg.Topmost;
            ChkStartup.IsChecked = StartupRegistry.IsEnabled();

            SldSize.Value = Math.Clamp(cfg.BallSize, SldSize.Minimum, SldSize.Maximum);
            SldOpacity.Value = Math.Clamp(cfg.Opacity, SldOpacity.Minimum, SldOpacity.Maximum);

            BuildFieldList(cfg);
        }
        finally
        {
            _loading = false;
        }
    }

    // ------------------------------------------------------------ 实时预览

    /// <summary>
    /// 把「显示相关」的当前控件值立刻刷到悬浮球上（不落盘）。
    /// 连接类输入框（地址 / 令牌 / 用户 ID / 刷新间隔）刻意不参与 —— 边输入边打网关毫无必要，
    /// 而且会把敲到一半的令牌写进内存里的配置。基准取当前生效配置，连接信息因此不受影响。
    /// </summary>
    private void Preview()
    {
        if (_loading) return;
        if (Application.Current is not App app) return;

        var cfg = app.Config.Clone();

        cfg.PrimaryField = CmbPrimary.SelectedValue as string ?? cfg.PrimaryField;
        cfg.BallSubField = CmbSub.SelectedValue as string ?? cfg.BallSubField;

        cfg.VisibleFields = _fields.Where(f => f.Checked).Select(f => f.Key).ToList();
        if (cfg.VisibleFields.Count == 0)
            cfg.VisibleFields.Add(MetricFields.TodayTotalTokens);

        cfg.ShowHourlyChart = ChkChart.IsChecked == true;
        cfg.BallSize = SldSize.Value;
        cfg.Opacity = SldOpacity.Value;

        if (double.TryParse(TxtCardWidth.Text.Trim(), out var cardWidth))
            cfg.CardWidth = Math.Clamp(cardWidth, 240, 560);

        app.PreviewConfig(cfg);
    }

    private void BuildFieldList(AppConfig cfg)
    {
        _fields.Clear();

        // 已勾选的按配置顺序排在前面
        foreach (var key in cfg.VisibleFields)
        {
            var meta = MetricFields.Get(key);
            AddField(new FieldItem
            {
                Key = meta.Key, Label = meta.Label, Hint = meta.Hint, Checked = true,
            });
        }

        // 其余按目录顺序排在后面
        foreach (var meta in MetricFields.All)
        {
            if (cfg.VisibleFields.Contains(meta.Key)) continue;
            AddField(new FieldItem
            {
                Key = meta.Key, Label = meta.Label, Hint = meta.Hint, Checked = false,
            });
        }
    }

    /// <summary>加入字段行并挂上实时预览 —— 勾选框是 TwoWay 绑定，Click 时 Checked 已写回。</summary>
    private void AddField(FieldItem item)
    {
        item.PropertyChanged += (_, _) => Preview();
        _fields.Add(item);
    }

    // ------------------------------------------------------------ 拖拽排序

    private FieldItem? _dragItem;

    /// <summary>重排动画时长。要短促 —— 拖动时会连续触发，太长会"追不上"手指。</summary>
    private const double ReflowMs = 170;

    private void OnDragHandleDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FieldItem item } handle) return;

        _dragItem = item;
        handle.CaptureMouse();
        e.Handled = true;   // 别让外层 ScrollViewer 顺手把这一下当成滚动
    }

    private void OnDragHandleMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null) return;

        // 拖到一半在别处松手（手柄之外），事件仍会送到捕获元素，这里兜底收尾
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag(sender as UIElement);
            return;
        }

        var target = ItemAt(e.GetPosition(FieldItems));
        if (target is null || ReferenceEquals(target, _dragItem)) return;

        var from = _fields.IndexOf(_dragItem);
        var to = _fields.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;

        MoveWithReflow(from, to);
        Preview();          // 顺序即时反映到卡片上
    }

    /// <summary>
    /// 重排并让被挤开的项平滑滑到新位置。
    /// 直接 Move 会让每一项瞬移，观感很硬；这里先记下重排前每项的视觉位置，
    /// 重排后把它"瞬移回旧位置"再动画归零 —— 全程走 RenderTransform，不碰布局。
    /// </summary>
    private void MoveWithReflow(int from, int to)
    {
        var before = CaptureVisualTops();

        _fields.Move(from, to);
        FieldItems.UpdateLayout();      // 新位置要同步拿到，不能等下一帧

        foreach (var (item, oldTop) in before)
        {
            var index = _fields.IndexOf(item);
            if (index < 0) continue;
            if (FieldItems.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement c) continue;

            var dy = oldTop - LayoutInformation.GetLayoutSlot(c).Y;
            if (Math.Abs(dy) < 0.5) continue;       // 没动的项别白跑一条动画

            var tt = EnsureTranslate(c);
            tt.BeginAnimation(TranslateTransform.YProperty, null);
            tt.Y = dy;                              // 先瞬移回旧位置……
            tt.BeginAnimation(TranslateTransform.YProperty,   // ……再平滑滑向新位置
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(ReflowMs))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }
    }

    /// <summary>
    /// 重排前的"视觉"纵向位置 = 布局槽位 + 正在进行的位移动画值。
    /// 取槽位必须用 LayoutInformation：TranslatePoint 会把 RenderTransform 算进去，
    /// 连续拖动时会拿到动画中间值，动画起点就飘了。
    /// </summary>
    private Dictionary<FieldItem, double> CaptureVisualTops()
    {
        var map = new Dictionary<FieldItem, double>();
        for (var i = 0; i < _fields.Count; i++)
        {
            if (FieldItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;
            var offset = c.RenderTransform is TranslateTransform t ? t.Y : 0;
            map[_fields[i]] = LayoutInformation.GetLayoutSlot(c).Y + offset;
        }
        return map;
    }

    private static TranslateTransform EnsureTranslate(FrameworkElement c)
    {
        if (c.RenderTransform is TranslateTransform t) return t;
        var fresh = new TranslateTransform();
        c.RenderTransform = fresh;
        return fresh;
    }

    private void OnDragHandleUp(object sender, MouseButtonEventArgs e) => EndDrag(sender as UIElement);

    private void EndDrag(UIElement? handle)
    {
        if (handle?.IsMouseCaptured == true) handle.ReleaseMouseCapture();
        _dragItem = null;
    }

    /// <summary>鼠标落在哪一行字段上。按容器实际边界判断，比命中测试稳（拖动中被拖项自己也在动）。</summary>
    private FieldItem? ItemAt(Point p)
    {
        for (var i = 0; i < _fields.Count; i++)
        {
            if (FieldItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;

            var top = c.TranslatePoint(new Point(0, 0), FieldItems).Y;
            if (p.Y >= top && p.Y <= top + c.ActualHeight) return _fields[i];
        }
        return null;
    }

    // ------------------------------------------------------------ 动作

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var cfg = Config.Clone();

        cfg.BaseUrl = TxtBaseUrl.Text.Trim();
        cfg.AccessToken = TxtToken.Text.Trim();
        cfg.UserId = int.TryParse(TxtUserId.Text.Trim(), out var uid) && uid > 0 ? uid : 1;
        cfg.RefreshSeconds = int.TryParse(TxtRefresh.Text.Trim(), out var rs)
            ? Math.Clamp(rs, 5, 3600) : 30;
        cfg.CardWidth = double.TryParse(TxtCardWidth.Text.Trim(), out var cw)
            ? Math.Clamp(cw, 240, 560) : 320;

        cfg.PrimaryField = CmbPrimary.SelectedValue as string ?? MetricFields.TodayTotalTokens;
        cfg.BallSubField = CmbSub.SelectedValue as string ?? MetricFields.YesterdayCompare;

        cfg.VisibleFields = _fields.Where(f => f.Checked).Select(f => f.Key).ToList();
        if (cfg.VisibleFields.Count == 0)
            cfg.VisibleFields.Add(MetricFields.TodayTotalTokens);

        cfg.ShowHourlyChart = ChkChart.IsChecked == true;
        cfg.Topmost = ChkTopmost.IsChecked == true;
        cfg.StartWithWindows = ChkStartup.IsChecked == true;
        cfg.BallSize = SldSize.Value;
        cfg.Opacity = SldOpacity.Value;

        StartupRegistry.Set(cfg.StartWithWindows);

        if (Application.Current is App app)
            await app.ApplyNewConfigAsync(cfg);

        Close();
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        // 只重置显示相关，不动已填好的连接信息
        var d = new AppConfig();

        CmbPrimary.SelectedValue = d.PrimaryField;
        CmbSub.SelectedValue = d.BallSubField;
        ChkChart.IsChecked = d.ShowHourlyChart;
        SldSize.Value = d.BallSize;
        SldOpacity.Value = d.Opacity;
        TxtCardWidth.Text = d.CardWidth.ToString("0");

        BuildFieldList(d);
        Preview();      // 恢复默认也要立刻看到效果
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
