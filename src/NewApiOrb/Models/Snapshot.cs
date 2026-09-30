namespace NewApiOrb.Models;

/// <summary>按时间桶 / 按模型聚合的一行原始数据（来自 /api/data/self）。</summary>
public sealed class DataBucket
{
    public string ModelName { get; set; } = "";
    public long CreatedAt { get; set; }
    public long TokenUsed { get; set; }
    public int Count { get; set; }
    public long Quota { get; set; }
}

/// <summary>单条调用日志的样本，供「最近 N 条」类指标使用。</summary>
public sealed class LogSample
{
    public long CompletionTokens { get; set; }
    public int UseTime { get; set; }
    public long CreatedAt { get; set; }

    /// <summary>生成速度（tokens/s）。⚠️ use_time 只有整数秒，单条样本误差较大。</summary>
    public double? Speed => UseTime > 0 ? CompletionTokens / (double)UseTime : null;
}

/// <summary>日志明细聚合结果（来自 /api/log/self）。</summary>
public sealed class LogSummary
{
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long CacheTokens { get; set; }
    public long Quota { get; set; }
    public int Requests { get; set; }
    public double TotalUseTime { get; set; }
    public bool Truncated { get; set; }

    /// <summary>按令牌聚合的用量（来自逐条日志的 token_name）。</summary>
    public Dictionary<string, TokenUsage> ByToken { get; } = new();

    /// <summary>按模型聚合的用量。</summary>
    public Dictionary<string, ModelUsage> ByModel { get; } = new();

    /// <summary>按小时聚合的 token 数（索引 0..23）。</summary>
    public long[] HourlyTokens { get; } = new long[24];

    /// <summary>最近的流式请求样本，按时间倒序，最多 3 条。</summary>
    public List<LogSample> RecentStreams { get; } = new();

    public long TotalTokens => PromptTokens + CompletionTokens;

    public double AvgLatency => Requests > 0 ? TotalUseTime / Requests : 0;

    /// <summary>最近一条流式请求的生成速度（tok/s）。</summary>
    public double? StreamSpeedLast1 => RecentStreams.Count > 0 ? RecentStreams[0].Speed : null;

    /// <summary>最近三条流式请求的平均生成速度（tok/s）= Σ输出 ÷ Σ耗时（比逐条平均更稳）。</summary>
    public double? StreamSpeedLast3
    {
        get
        {
            var totalTime = RecentStreams.Sum(x => x.UseTime);
            if (totalTime <= 0) return null;
            return RecentStreams.Sum(x => x.CompletionTokens) / (double)totalTime;
        }
    }

    /// <summary>最近一条流式请求的耗时（秒）。</summary>
    public int? LatencyLast1 => RecentStreams.Count > 0 ? RecentStreams[0].UseTime : null;

    /// <summary>最近三条流式请求的平均耗时（秒）。不足 3 条时按现有条数算。</summary>
    public double? LatencyLast3 =>
        RecentStreams.Count > 0 ? RecentStreams.Average(x => x.UseTime) : null;
}

/// <summary>账户级信息（来自 /api/user/self）。</summary>
public sealed class UserInfo
{
    public string Username { get; set; } = "";
    public long Quota { get; set; }
    public long UsedQuota { get; set; }
    public int RequestCount { get; set; }
}

/// <summary>某一小时桶，用于柱状图。</summary>
public sealed class HourBucket
{
    public int Hour { get; set; }
    public long Tokens { get; set; }
}

/// <summary>模型用量，用于 Top 模型。</summary>
public sealed class ModelUsage
{
    public string ModelName { get; set; } = "";
    public long Tokens { get; set; }
    public int Count { get; set; }
}

/// <summary>按 API 令牌（token / key）维度的用量。</summary>
public sealed class TokenUsage
{
    public string TokenName { get; set; } = "";
    public int TokenId { get; set; }
    public long Tokens { get; set; }
    public int Requests { get; set; }
}

/// <summary>一次采集后的完整快照，UI 只读这个对象。</summary>
public sealed class Snapshot
{
    public DateTime FetchedAt { get; set; } = DateTime.Now;
    public bool IsOnline { get; set; }
    public bool Truncated { get; set; }
    public string? Error { get; set; }

    // 今日
    public long TodayTotalTokens { get; set; }
    public long TodayPromptTokens { get; set; }
    public long TodayCompletionTokens { get; set; }
    public long TodayCacheTokens { get; set; }
    public int TodayRequests { get; set; }
    public long TodayQuota { get; set; }
    public double AvgLatency { get; set; }
    public List<HourBucket> Hours { get; set; } = new();
    public List<ModelUsage> Models { get; set; } = new();
    public List<TokenUsage> Tokens { get; set; } = new();

    // 账户
    public string Username { get; set; } = "";
    public long Balance { get; set; }
    public long UsedQuota { get; set; }
    public int TotalRequests { get; set; }

    // 对比
    public long? YesterdayTotalTokens { get; set; }

    // 最近 N 条流式请求（收起态副指标用）
    public double? StreamSpeedLast1 { get; set; }
    public double? StreamSpeedLast3 { get; set; }
    public int? LatencyLast1 { get; set; }
    public double? LatencyLast3 { get; set; }

    public string TopModel => Models.Count > 0 ? Models[0].ModelName : "—";

    public string TopToken => Tokens.Count > 0 ? Tokens[0].TokenName : "—";

    public int ActiveTokenCount => Tokens.Count;

    /// <summary>环比昨日，返回百分比变化；昨日无数据时返回 null。</summary>
    public double? DayOverDay
    {
        get
        {
            if (YesterdayTotalTokens is null or <= 0) return null;
            return (TodayTotalTokens - YesterdayTotalTokens.Value) * 100.0 / YesterdayTotalTokens.Value;
        }
    }
}
