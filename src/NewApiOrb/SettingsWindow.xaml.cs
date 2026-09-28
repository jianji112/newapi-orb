using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using NewApiOrb.Models;
using NewApiOrb.Services;

namespace NewApiOrb;

public partial class SettingsWindow : Window
{
    private readonly ObservableCollection<FieldItem> _fields = new();

    public SettingsWindow()
    {
        InitializeComponent();

        CmbPrimary.ItemsSource = MetricFields.All.ToList();
        // ⚠️ 不要用 DisplayMemberPath —— SelectionBoxItemTemplate 只认 ItemTemplate，
        // 用 DisplayMemberPath 的话收起态会渲染成 "Meta { Key = ... }"（模板见 SettingsWindow.xaml）
        CmbPrimary.SelectedValuePath = nameof(MetricFields.Meta.Key);

        FieldItems.ItemsSource = _fields;

        SldSize.ValueChanged += (_, e) => LblSize.Text = $"{e.NewValue:0} px";
        SldOpacity.ValueChanged += (_, e) => LblOpacity.Text = $"{e.NewValue * 100:0}%";

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

        TxtBaseUrl.Text = cfg.BaseUrl;
        TxtToken.Text = cfg.AccessToken;
        TxtUserId.Text = cfg.UserId.ToString();
        TxtRefresh.Text = cfg.RefreshSeconds.ToString();
        TxtCardWidth.Text = cfg.CardWidth.ToString("0");

        CmbPrimary.SelectedValue = cfg.PrimaryField;
        ChkChart.IsChecked = cfg.ShowHourlyChart;
        ChkTopmost.IsChecked = cfg.Topmost;
        ChkStartup.IsChecked = StartupRegistry.IsEnabled();

        SldSize.Value = Math.Clamp(cfg.BallSize, SldSize.Minimum, SldSize.Maximum);
        SldOpacity.Value = Math.Clamp(cfg.Opacity, SldOpacity.Minimum, SldOpacity.Maximum);

        BuildFieldList(cfg);
    }

    private void BuildFieldList(AppConfig cfg)
    {
        _fields.Clear();

        // 已勾选的按配置顺序排在前面
        foreach (var key in cfg.VisibleFields)
        {
            var meta = MetricFields.Get(key);
            _fields.Add(new FieldItem
            {
                Key = meta.Key, Label = meta.Label, Hint = meta.Hint, Checked = true,
            });
        }

        // 其余按目录顺序排在后面
        foreach (var meta in MetricFields.All)
        {
            if (cfg.VisibleFields.Contains(meta.Key)) continue;
            _fields.Add(new FieldItem
            {
                Key = meta.Key, Label = meta.Label, Hint = meta.Hint, Checked = false,
            });
        }
    }

    // ------------------------------------------------------------ 字段排序

    private void OnMoveUp(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FieldItem item }) return;
        var i = _fields.IndexOf(item);
        if (i > 0) _fields.Move(i, i - 1);
    }

    private void OnMoveDown(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FieldItem item }) return;
        var i = _fields.IndexOf(item);
        if (i >= 0 && i < _fields.Count - 1) _fields.Move(i, i + 1);
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
        ChkChart.IsChecked = d.ShowHourlyChart;
        SldSize.Value = d.BallSize;
        SldOpacity.Value = d.Opacity;
        TxtCardWidth.Text = d.CardWidth.ToString("0");

        BuildFieldList(d);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
