using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
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

    /// <summary>打开设置面板前的配置快照 —— 面板里所有改动都是"实时预览"，点取消要靠它回滚。</summary>
    private AppConfig? _settingsSnapshot;

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

        // 启动引导是最难查的一段：一旦它没弹出来，用户只看到一个"离线"的球，界面上毫无线索。
        // 所以关键节点必须落痕（startup.log）。
        // 调试开关：ORB_OPEN_SETTINGS=1 时启动即打开设置窗口
        var wantSettings = notConfigured
                        || Environment.GetEnvironmentVariable("ORB_OPEN_SETTINGS") == "1";

        LogStartup($"启动完成 notConfigured={notConfigured} " +
                   $"env={Environment.GetEnvironmentVariable("ORB_OPEN_SETTINGS")} wantSettings={wantSettings}");

        if (!wantSettings) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            LogStartup("BeginInvoke 回调执行，准备 OpenSettings");
            try
            {
                OpenSettings();
                LogStartup("OpenSettings 正常返回");
            }
            catch (Exception ex)
            {
                LogStartup("OpenSettings 抛异常：" + ex);
                MessageBox.Show("设置窗口打开失败：\n" + ex.Message, "New API Orb",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }));
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

    /// <summary>
    /// 启动/引导路径的故障留痕。这段一旦出问题，用户只会看到一个写着"离线"的球，
    /// 界面上没有任何线索 —— 所以关键节点都要往 startup.log 落一行。
    /// </summary>
    private static void LogStartup(string msg)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NewApiOrb");
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, "startup.log");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                File.Delete(path);      // 不设上限会一直长

            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        }
        catch { /* 日志写不进去不影响主流程 */ }
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

        // 面板里的改动都是实时预览（不落盘），先留一份快照供「取消」回滚
        _settingsSnapshot = Config.Clone();

        LogStartup("OpenSettings: 开始构造 SettingsWindow");
        _settings = new SettingsWindow { Topmost = Config.Topmost };
        LogStartup("OpenSettings: 构造完成");
        _settings.Closed += (_, _) =>
        {
            LogStartup("OpenSettings: 设置窗口被 Closed");
            _settings = null;
            if (_orb is not null) _orb.Topmost = restoreTopmost;
        };

        LogStartup("OpenSettings: 调用 Show()");
        _settings.Show();
        LogStartup($"OpenSettings: Show() 返回 IsVisible={_settings.IsVisible} " +
                   $"IsLoaded={_settings.IsLoaded} " +
                   $"rect=({_settings.Left},{_settings.Top},{_settings.ActualWidth}x{_settings.ActualHeight})");

        _settings.Activate();
        LogStartup($"OpenSettings: Activate() 完成 IsVisible={_settings.IsVisible}");
    }

    /// <summary>
    /// 设置面板的实时预览：把配置应用到内存与界面，**不落盘**。
    /// 用户在面板里勾选 / 拖滑块时立刻能看到球上的变化，不必保存之后才知道效果。
    /// </summary>
    public void PreviewConfig(AppConfig cfg)
    {
        Config = cfg;
        _orb?.ApplyConfig(cfg);
    }

    /// <summary>
    /// 关闭设置面板时调用：没保存过就回滚到打开面板前的样子。
    /// 保存路径会先清空快照（见 <see cref="ApplyNewConfigAsync"/>），所以此处天然是空操作。
    /// </summary>
    public void RevertSettingsPreview()
    {
        if (_settingsSnapshot is null) return;
        PreviewConfig(_settingsSnapshot);
        _settingsSnapshot = null;
    }

    /// <summary>设置保存后调用：落盘 + 立刻按新配置刷新一次。</summary>
    public async Task ApplyNewConfigAsync(AppConfig cfg)
    {
        Config = cfg;
        ConfigService.Save(cfg);
        Metrics.UpdateConfig(cfg);
        _orb?.ApplyConfig(cfg);
        _settingsSnapshot = null;   // 已提交，关闭面板时不再回滚
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
