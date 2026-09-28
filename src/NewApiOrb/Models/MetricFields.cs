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
        new(AvgLatency,          "平均耗时",        "今日", "单位：秒"),
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
}
