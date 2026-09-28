using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using NewApiOrb.Models;
using NewApiOrb.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace NewApiOrb;

public partial class App : Application
{
    private Mutex? _mutex;
    private NotifyIcon? _tray;
    private MainWindow? _orb;
    private SettingsWindow? _settings;

    public AppConfig Config { get; private set; } = new();
    public MetricsService Metrics { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, @"Global\NewApiOrb.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("New API Orb 已经在运行了。", "New API Orb",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            MessageBox.Show("出现未预期的错误：\n" + args.Exception.Message, "New API Orb",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        Config = ConfigService.Load();
        Metrics.Start(Config);

        _orb = new MainWindow();
        _orb.Show();

        BuildTray();

        // 首次运行（没配令牌 / 没填地址）→ 直接把设置窗口推到前台。
        // 分发给别人时这一条是必须的：对方机器上没有预置配置，令牌是空的，
        // 不弹设置窗口的话他只会看到球上写着"离线"，完全不知道该干什么。
        var notConfigured = string.IsNullOrWhiteSpace(Config.AccessToken)
                         || string.IsNullOrWhiteSpace(Config.BaseUrl);

        // 调试开关：ORB_OPEN_SETTINGS=1 时启动即打开设置窗口
        if (notConfigured || Environment.GetEnvironmentVariable("ORB_OPEN_SETTINGS") == "1")
            Dispatcher.BeginInvoke(new Action(OpenSettings));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Metrics.Stop();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        try { _mutex?.ReleaseMutex(); } catch { /* 未持有 */ }
        _mutex?.Dispose();
        base.OnExit(e);
    }

    // ------------------------------------------------------------ 托盘

    private void BuildTray()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };

        var miToggle = new ToolStripMenuItem("显示 / 隐藏悬浮球");
        miToggle.Click += (_, _) => ToggleOrb();

        var miRefresh = new ToolStripMenuItem("立即刷新");
        miRefresh.Click += async (_, _) => await Metrics.RefreshNowAsync();

        var miSettings = new ToolStripMenuItem("设置…");
        miSettings.Click += (_, _) => OpenSettings();

        var miTop = new ToolStripMenuItem("窗口置顶") { CheckOnClick = true, Checked = Config.Topmost };
        miTop.CheckedChanged += (_, _) =>
        {
            Config.Topmost = miTop.Checked;
            ConfigService.Save(Config);
            _orb?.ApplyConfig(Config);
        };

        var miStartup = new ToolStripMenuItem("开机自启")
        {
            CheckOnClick = true,
            Checked = StartupRegistry.IsEnabled(),
        };
        miStartup.CheckedChanged += (_, _) =>
        {
            Config.StartWithWindows = miStartup.Checked;
            ConfigService.Save(Config);
            StartupRegistry.Set(miStartup.Checked);
        };

        var miExit = new ToolStripMenuItem("退出");
        miExit.Click += (_, _) => Shutdown();

        menu.Items.AddRange(new ToolStripItem[]
        {
            miToggle, miRefresh, miSettings,
            new ToolStripSeparator(),
            miTop, miStartup,
            new ToolStripSeparator(),
            miExit,
        });

        _tray = new NotifyIcon
        {
            Icon = BuildTrayIcon(),
            Text = "New API Orb · 今日 Token 用量",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ToggleOrb();
        };
    }

    private void ToggleOrb()
    {
        if (_orb is null) return;
        if (_orb.IsVisible) _orb.Hide();
        else _orb.Show();
    }

    public void OpenSettings()
    {
        if (_settings is { IsVisible: true })
        {
            _settings.Activate();
            return;
        }

        var restoreTopmost = Config.Topmost;
        if (_orb is not null) _orb.Topmost = false;

        _settings = new SettingsWindow { Topmost = Config.Topmost };
        _settings.Closed += (_, _) =>
        {
            _settings = null;
            if (_orb is not null) _orb.Topmost = restoreTopmost;
        };
        _settings.Show();
        _settings.Activate();
    }

    /// <summary>设置保存后调用：落盘 + 立刻按新配置刷新一次。</summary>
    public async Task ApplyNewConfigAsync(AppConfig cfg)
    {
        Config = cfg;
        ConfigService.Save(cfg);
        Metrics.UpdateConfig(cfg);
        _orb?.ApplyConfig(cfg);
        await Metrics.RefreshNowAsync();
    }

    // ------------------------------------------------------------ 托盘图标（动态绘制，免外部资源）

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static Icon BuildTrayIcon()
    {
        const int size = 64;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var bg = new SolidBrush(Color.FromArgb(255, 31, 34, 39));
            g.FillEllipse(bg, 1, 1, size - 2, size - 2);

            using var ring = new Pen(Color.FromArgb(255, 76, 154, 255), 5);
            g.DrawEllipse(ring, 5, 5, size - 10, size - 10);

            using var dot = new SolidBrush(Color.FromArgb(255, 76, 154, 255));
            g.FillEllipse(dot, 25, 25, 14, 14);
        }

        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
