namespace NewApiOrb.Models;

/// <summary>应用配置，持久化为 %APPDATA%\NewApiOrb\config.json。</summary>
public sealed class AppConfig
{
    // ---- 连接 ----
    /// <summary>
    /// 默认指向 new-api 的官方默认端口。**不要**在这里写具体某台机器的内网地址 ——
    /// 这个程序是要分发给别人用的，硬编码内网 IP 会让对方拿到就是"离线"，还泄露内网拓扑。
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:3000";
    public string AccessToken { get; set; } = "";
    public int UserId { get; set; } = 1;

    // ---- 刷新 ----
    public int RefreshSeconds { get; set; } = 30;

    // ---- 显示内容（可自定义）----
    /// <summary>收起态圆球上显示的主指标。</summary>
    public string PrimaryField { get; set; } = MetricFields.TodayTotalTokens;

    /// <summary>
    /// 收起态球体下方那一行的副指标（二级内容）：环比 / 近 N 条流式速度 / 近 N 条耗时 / 不显示。
    /// 可选值见 <see cref="MetricFields.SubFields"/>；默认保持历史上的「环比昨日」。
    /// </summary>
    public string BallSubField { get; set; } = MetricFields.YesterdayCompare;

    /// <summary>展开态卡片中显示哪些字段，顺序即展示顺序。</summary>
    public List<string> VisibleFields { get; set; } = new()
    {
        MetricFields.TodayTotalTokens,
        MetricFields.TodayPromptTokens,
        MetricFields.TodayCompletionTokens,
        MetricFields.TodayRequests,
        MetricFields.TopModel,
        MetricFields.TopToken,
        MetricFields.ActiveTokens,
    };

    public bool ShowHourlyChart { get; set; } = true;

    // ---- 外观 ----
    public double BallSize { get; set; } = 112;
    public double Opacity { get; set; } = 0.94;
    public bool Topmost { get; set; } = true;
    public bool DockEnabled { get; set; } = true;
    public double CardWidth { get; set; } = 320;

    // ---- 位置记忆 ----
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    // ---- 系统 ----
    public bool StartWithWindows { get; set; }

    public AppConfig Clone()
    {
        var copy = (AppConfig)MemberwiseClone();
        copy.VisibleFields = new List<string>(VisibleFields);
        return copy;
    }
}
