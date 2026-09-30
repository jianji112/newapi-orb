namespace NewApiOrb.Models;

/// <summary>
/// 可选显示字段的键。配置里以字符串保存，便于向前兼容。
/// </summary>
public static class MetricFields
{
    public const string TodayTotalTokens = "TodayTotalTokens";
    public const string TodayPromptTokens = "TodayPromptTokens";
    public const string TodayCompletionTokens = "TodayCompletionTokens";
    public const string TodayCacheTokens = "TodayCacheTokens";
    public const string TodayRequests = "TodayRequests";
    public const string TodayQuota = "TodayQuota";
    public const string TopModel = "TopModel";
    public const string TopToken = "TopToken";
    public const string ActiveTokens = "ActiveTokens";
    public const string AvgLatency = "AvgLatency";
    public const string Balance = "Balance";
    public const string UsedQuota = "UsedQuota";
    public const string TotalRequests = "TotalRequests";
    public const string YesterdayCompare = "YesterdayCompare";

    public sealed record Meta(string Key, string Label, string Group, string Hint);

    /// <summary>字段目录：顺序即设置面板中的展示顺序。</summary>
    public static readonly IReadOnlyList<Meta> All = new List<Meta>
    {
        new(TodayTotalTokens,    "今日 Token 总量", "今日", "输入 + 输出 + 缓存"),
        new(TodayPromptTokens,   "今日输入 Tokens", "今日", "prompt_tokens 合计"),
        new(TodayCompletionTokens,"今日输出 Tokens","今日", "completion_tokens 合计"),
        new(TodayCacheTokens,    "今日缓存 Tokens", "今日", "cache_tokens 合计"),
        new(TodayRequests,       "今日请求数",      "今日", "当日调用次数"),
        new(TodayQuota,          "今日消耗额度",    "今日", "原始 quota 值"),
        new(TopModel,            "Top 模型",        "今日", "当日用量最高的模型"),
        new(TopToken,            "Top 令牌",        "令牌", "当日用量最高的 API 令牌"),
        new(ActiveTokens,        "活跃令牌数",      "令牌", "当日有调用的令牌个数"),
        new(AvgLatency,          "平均耗时",        "今日", "单位：秒（今日全部请求）"),
        // 「最近 N 条」类指标：只统计流式请求，与上面的"平均耗时"口径不同，Label 里带「流式」以示区别
        new(StreamSpeedLast1,    "近 1 条流式速度", "流式", "最近一条流式请求的生成速度（输出 tokens ÷ 耗时）"),
        new(StreamSpeedLast3,    "近 3 条流式速度", "流式", "最近三条流式请求的平均生成速度（Σ输出 ÷ Σ耗时）"),
        new(LatencyLast1,        "近 1 条流式耗时", "流式", "最近一条流式请求的耗时"),
        new(LatencyLast3,        "近 3 条流式耗时", "流式", "最近三条流式请求的平均耗时"),
        new(Balance,             "账户余额",        "账户", "剩余额度"),
        new(UsedQuota,           "累计已用",        "账户", "历史消耗总额度"),
        new(TotalRequests,       "总请求数",        "账户", "历史累计调用次数"),
        new(YesterdayCompare,    "环比昨日",        "对比", "与昨日同口径对比"),
    };

    private static readonly Dictionary<string, Meta> Index =
        All.ToDictionary(m => m.Key, m => m);

    public static Meta Get(string key) =>
        Index.TryGetValue(key, out var m) ? m : new Meta(key, key, "其他", "");

    public static string Label(string key) => Get(key).Label;

    // ---------------------------------------------------------------- 收起态副指标

    /// <summary>副指标：不显示（球上只留主指标）。</summary>
    public const string SubNone = "None";
    public const string StreamSpeedLast1 = "StreamSpeedLast1";
    public const string StreamSpeedLast3 = "StreamSpeedLast3";
    public const string LatencyLast1 = "LatencyLast1";
    public const string LatencyLast3 = "LatencyLast3";

    /// <summary>
    /// 收起态球体下方那一行（副指标）的可选项 —— 即 <see cref="All"/> 里那 5 个「最近 N 条」/环比指标，
    /// 外加一个「不显示」（非数据项，所以不适合混进 All 的字段勾选列表）。
    /// 数据项直接复用 All 里的 Meta：同一个指标不该存在两份 Label。
    /// </summary>
    public static readonly IReadOnlyList<Meta> SubFields = new List<Meta>
    {
        new(SubNone, "不显示", "副指标", "球上只保留主指标"),
        Get(YesterdayCompare),
        Get(StreamSpeedLast1),
        Get(StreamSpeedLast3),
        Get(LatencyLast1),
        Get(LatencyLast3),
    };
}
