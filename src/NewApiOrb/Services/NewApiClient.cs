using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using NewApiOrb.Models;
using NewApiOrb.Utils;

namespace NewApiOrb.Services;

/// <summary>New API 网关的只读客户端。</summary>
public sealed class NewApiClient
{
    // 网关直连，绕开系统代理（mihomo / Clash 等透明代理工具）。
    // 否则私有网段地址可能被代理规则劫走，导致请求失败。
    // ⚠️ 超时抛的是 TaskCanceledException（继承 OperationCanceledException）——
    // 调用方必须用 `ct.IsCancellationRequested` 区分"真取消"和"超时"，否则一次超时会杀掉刷新循环。
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>分页并发上限 —— 网关跑在 N100 上，一次砸十几个查询会整批超时。</summary>
    private const int MaxPageConcurrency = 4;

    public string BaseUrl { get; private set; } = "";
    public string AccessToken { get; private set; } = "";
    public int UserId { get; private set; } = 1;

    public NewApiClient(AppConfig cfg) => Apply(cfg);

    public void Apply(AppConfig cfg)
    {
        BaseUrl = cfg.BaseUrl.TrimEnd('/');
        AccessToken = cfg.AccessToken;
        UserId = cfg.UserId;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(AccessToken);

    // ---------------------------------------------------------------- 取数

    /// <summary>GET /api/data/self —— 按时间桶聚合的 token 用量。一次请求拿全今日总量。</summary>
    public async Task<List<DataBucket>> GetBucketsAsync(
        long startUnix, long endUnix, CancellationToken ct, string defaultTime = "hour")
    {
        var path = $"/api/data/self?start_timestamp={startUnix}&end_timestamp={endUnix}&default_time={defaultTime}";
        var data = await GetDataAsync(path, ct);

        var list = new List<DataBucket>();
        if (data.ValueKind != JsonValueKind.Array) return list;

        foreach (var row in data.EnumerateArray())
        {
            list.Add(new DataBucket
            {
                ModelName = Str(row, "model_name"),
                CreatedAt = Long(row, "created_at"),
                TokenUsed = Long(row, "token_used"),
                Count = (int)Long(row, "count"),
                Quota = Long(row, "quota"),
            });
        }
        return list;
    }

    /// <summary>GET /api/user/self —— 账户余额与累计用量。</summary>
    public async Task<UserInfo> GetUserAsync(CancellationToken ct)
    {
        var data = await GetDataAsync("/api/user/self", ct);
        return new UserInfo
        {
            Username = Str(data, "username"),
            Quota = Long(data, "quota"),
            UsedQuota = Long(data, "used_quota"),
            RequestCount = (int)Long(data, "request_count"),
        };
    }

    /// <summary>
    /// GET /api/log/self —— 逐条明细，本工具的唯一数据源（/api/data/self 是近似统计，会少算）。
    /// 服务端 page_size 上限 100；先探总数，再并发拉取其余页，最后一次性聚合。
    /// </summary>
    public async Task<LogSummary> GetLogSummaryAsync(
        long startUnix, long endUnix, CancellationToken ct, int maxPages = 24)
    {
        const int pageSize = 100;

        var first = await FetchLogPageAsync(1, pageSize, startUnix, endUnix, ct);
        var rows = new List<LogRow>(first.Rows);
        var sum = new LogSummary();

        var pages = (int)Math.Ceiling(first.Total / (double)pageSize);
        if (pages > maxPages)
        {
            pages = maxPages;
            sum.Truncated = true;
        }

        if (pages > 1)
        {
            // ⚠️ 必须限流。今日几百条日志 → 十几页，不限制就是十几个并发查询同时砸向
            // 只有 N100 的网关，实测会整批超时（而超时又会把刷新循环搞死，见 MetricsService）。
            using var gate = new SemaphoreSlim(MaxPageConcurrency);
            var tasks = Enumerable.Range(2, pages - 1)
                .Select(p => FetchLogPageGatedAsync(p, pageSize, startUnix, endUnix, ct, gate));
            foreach (var r in await Task.WhenAll(tasks)) rows.AddRange(r.Rows);
        }

        // 「最近 N 条」类指标必须按时间倒序取 —— 分页是 4 路并发拉的，rows 顺序已被打乱，
        // 不排序会取到随机三条（表现就是球上的速度每次刷新都乱跳）。
        foreach (var r in rows.Where(x => x.IsStream).OrderByDescending(x => x.CreatedAt).Take(3))
        {
            sum.RecentStreams.Add(new LogSample
            {
                CompletionTokens = r.Completion,
                UseTime = r.UseTime,
                CreatedAt = r.CreatedAt,
            });
        }

        foreach (var r in rows)
        {
            var tokens = r.Prompt + r.Completion;

            sum.PromptTokens += r.Prompt;
            sum.CompletionTokens += r.Completion;
            sum.CacheTokens += r.Cache;
            sum.Quota += r.Quota;
            sum.TotalUseTime += r.UseTime;
            sum.Requests++;

            var hour = Fmt.FromUnix(r.CreatedAt).Hour;
            if (hour is >= 0 and < 24) sum.HourlyTokens[hour] += tokens;

            if (!string.IsNullOrEmpty(r.ModelName))
            {
                if (!sum.ByModel.TryGetValue(r.ModelName, out var mu))
                {
                    mu = new ModelUsage { ModelName = r.ModelName };
                    sum.ByModel[r.ModelName] = mu;
                }
                mu.Tokens += tokens;
                mu.Count++;
            }

            if (!string.IsNullOrEmpty(r.TokenName))
            {
                if (!sum.ByToken.TryGetValue(r.TokenName, out var tu))
                {
                    tu = new TokenUsage { TokenName = r.TokenName, TokenId = r.TokenId };
                    sum.ByToken[r.TokenName] = tu;
                }
                tu.Tokens += tokens;
                tu.Requests++;
            }
        }

        return sum;
    }

    private sealed record LogRow(
        long Prompt, long Completion, long Cache, long Quota,
        int UseTime, bool IsStream, string TokenName, int TokenId, string ModelName, long CreatedAt);

    /// <summary>
    /// 限流取一页。重试统一放在 <see cref="GetDataAsync"/>（所有出站请求共用），此处只管并发闸门。
    /// </summary>
    private async Task<(int Total, List<LogRow> Rows)> FetchLogPageGatedAsync(
        int page, int pageSize, long startUnix, long endUnix,
        CancellationToken ct, SemaphoreSlim gate)
    {
        await gate.WaitAsync(ct);
        try
        {
            return await FetchLogPageAsync(page, pageSize, startUnix, endUnix, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<(int Total, List<LogRow> Rows)> FetchLogPageAsync(
        int page, int pageSize, long startUnix, long endUnix, CancellationToken ct)
    {
        var path = $"/api/log/self?p={page}&page_size={pageSize}&type=0" +
                   $"&start_timestamp={startUnix}&end_timestamp={endUnix}";
        var data = await GetDataAsync(path, ct);

        var total = (int)Long(data, "total");
        var rows = new List<LogRow>();

        if (data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in items.EnumerateArray())
            {
                rows.Add(new LogRow(
                    Prompt: Long(row, "prompt_tokens"),
                    Completion: Long(row, "completion_tokens"),
                    Cache: ParseCacheTokens(Str(row, "other")),
                    Quota: Long(row, "quota"),
                    UseTime: (int)Long(row, "use_time"),
                    IsStream: Bool(row, "is_stream"),
                    TokenName: Str(row, "token_name"),
                    TokenId: (int)Long(row, "token_id"),
                    ModelName: Str(row, "model_name"),
                    CreatedAt: Long(row, "created_at")));
            }
        }

        return (total, rows);
    }

    /// <summary>探测网关是否可达（不需要鉴权的 /api/status）。</summary>
    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync($"{BaseUrl}/api/status", ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- 内部

    private async Task<JsonElement> GetDataAsync(string path, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("尚未配置网关地址或访问令牌");

        // ⚠️ 实测首请求会偶发抖动（4 次启动中 2 次撞上 30s 超时，而网关侧其实只要 90ms）。
        // 所以每个请求都自带一次重试 —— 不然一个抖动就让整轮采集失败、界面闪"离线"。
        const int maxAttempts = 2;

        for (var attempt = 1; ; attempt++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Trace($"-> GET {path}" + (attempt > 1 ? $"   (重试 {attempt - 1})" : ""));

            try
            {
                return await SendOnceAsync(path, ct, sw);
            }
            catch (Exception ex) when (attempt < maxAttempts && !ct.IsCancellationRequested && IsTransient(ex))
            {
                Trace($"~~ {path}  {sw.ElapsedMilliseconds}ms  {ex.GetType().Name} → 重试");
                await Task.Delay(400, ct);
            }
            catch (Exception ex)
            {
                // 出站请求的耗时与失败原因必须能看见 —— 不然"离线"只能靠猜。
                Trace($"!! {path}  {sw.ElapsedMilliseconds}ms  {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }
    }

    private async Task<JsonElement> SendOnceAsync(string path, CancellationToken ct, System.Diagnostics.Stopwatch sw)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + AccessToken);
        req.Headers.TryAddWithoutValidation("New-Api-User", UserId.ToString());

        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}：{Truncate(body, 160)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("success", out var ok) &&
            ok.ValueKind == JsonValueKind.False)
        {
            var msg = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            throw new InvalidOperationException(string.IsNullOrEmpty(msg) ? "接口返回失败" : msg);
        }

        if (!root.TryGetProperty("data", out var data))
            throw new InvalidOperationException("响应缺少 data 字段");

        Trace($"<- {(int)resp.StatusCode} {path}  {sw.ElapsedMilliseconds}ms  {body.Length}B");
        return data.Clone();
    }

    /// <summary>可重试的失败：网络层抖动 / 连接被拒 / 响应超时。业务错误（401、缺字段）不重试。</summary>
    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException
        || ex is IOException
        || ex is SocketException
        || (ex is TaskCanceledException && ex.InnerException is TimeoutException);

    /// <summary>出站请求追踪（ORB_TRACE=1 时写 %APPDATA%\NewApiOrb\fetch.log）。</summary>
    private static void Trace(string msg)
    {
        if (Environment.GetEnvironmentVariable("ORB_TRACE") != "1") return;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NewApiOrb");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "fetch.log"),
                $"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}");
        }
        catch { /* 追踪写不进去不影响主流程 */ }
    }

    /// <summary>logs.other 是一个 JSON 字符串，里面才有 cache_tokens。</summary>
    private static long ParseCacheTokens(string other)
    {
        if (string.IsNullOrWhiteSpace(other)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(other);
            return Long(doc.RootElement, "cache_tokens");
        }
        catch
        {
            return 0;
        }
    }

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
