using System.IO;
using NewApiOrb.Models;
using NewApiOrb.Utils;

namespace NewApiOrb.Services;

/// <summary>
/// 负责定时采集、聚合与状态广播。UI 层只订阅 <see cref="Updated"/>。
/// </summary>
public sealed class MetricsService
{
    private readonly NewApiClient _client = new(new AppConfig());
    private AppConfig _cfg = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;

    // 昨日总量按天缓存，避免每轮都多打一次请求
    private long _yesterdayDayKey = -1;
    private long _yesterdayTokens;

    public Snapshot Latest { get; private set; } = new();

    /// <summary>注意：回调发生在后台线程，UI 需自行切回 Dispatcher。</summary>
    public event Action<Snapshot>? Updated;

    public void Start(AppConfig cfg)
    {
        Stop();
        UpdateConfig(cfg);
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* 已释放 */ }
        _cts = null;
        _loop = null;
    }

    public void UpdateConfig(AppConfig cfg)
    {
        _cfg = cfg;
        _client.Apply(cfg);
    }

    /// <summary>立即采集一次（设置保存后调用）。</summary>
    public async Task RefreshNowAsync()
    {
        var token = _cts?.Token ?? CancellationToken.None;
        var snap = await CollectAsync(token);
        Latest = snap;
        Updated?.Invoke(snap);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var snap = await CollectAsync(ct);
                Latest = snap;
                Updated?.Invoke(snap);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;      // 真的被取消（程序退出）
            }
            catch (Exception ex)
            {
                // ⚠️ 这里**绝不能 break** —— HttpClient 超时抛的是 TaskCanceledException，
                // 而它继承 OperationCanceledException。老写法 `catch (OperationCanceledException) { break; }`
                // 会把"一次超时"当成"程序已退出"，把整个刷新循环永久杀掉：
                // 界面永远停在"离线"，且不落任何日志（走的是取消分支，不是错误分支）。
                LogError("loop", ex);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _cfg.RefreshSeconds)), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<Snapshot> CollectAsync(CancellationToken ct)
    {
        var snap = new Snapshot { FetchedAt = DateTime.Now };

        try
        {
            var start = Fmt.DayStartUnix(DateTime.Today);
            var end = Fmt.NowUnix();

            // 单一数据源：逐条日志（/api/log/self）。
            // 不用 /api/data/self —— 实测它是近似统计，会少算请求数与 token。
            var log = await _client.GetLogSummaryAsync(start, end, ct);

            snap.TodayTotalTokens = log.TotalTokens;
            snap.TodayPromptTokens = log.PromptTokens;
            snap.TodayCompletionTokens = log.CompletionTokens;
            snap.TodayCacheTokens = log.CacheTokens;
            snap.TodayRequests = log.Requests;
            snap.TodayQuota = log.Quota;
            snap.AvgLatency = log.AvgLatency;
            snap.Truncated = log.Truncated;

            snap.Models = log.ByModel.Values
                .OrderByDescending(m => m.Tokens)
                .ToList();

            snap.Tokens = log.ByToken.Values
                .OrderByDescending(t => t.Tokens)
                .ToList();

            snap.Hours = Enumerable.Range(0, 24)
                .Select(h => new HourBucket { Hour = h, Tokens = log.HourlyTokens[h] })
                .ToList();

            // 账户级信息
            var user = await _client.GetUserAsync(ct);
            snap.Username = user.Username;
            snap.Balance = user.Quota;
            snap.UsedQuota = user.UsedQuota;
            snap.TotalRequests = user.RequestCount;

            // 昨日对比
            snap.YesterdayTotalTokens = await GetYesterdayTokensAsync(ct);

            snap.IsOnline = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;      // 真取消，交给上层收尾
        }
        catch (Exception ex)
        {
            // 超时 / 网络 / 接口报错 —— 标离线、落日志，下一轮自动重试
            snap.IsOnline = false;
            snap.Error = ex.Message;
            LogError("collect", ex);
        }

        return snap;
    }

    /// <summary>
    /// 把失败原因落盘 —— 球上只显示"离线"，不落日志等于无法排查。
    /// ⚠️ 必须覆盖"超时"这条路径：`HttpClient` 超时抛的是 TaskCanceledException。
    /// </summary>
    private static void LogError(string where, Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NewApiOrb");
            Directory.CreateDirectory(dir);

            var logPath = Path.Combine(dir, "last-error.log");
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 256 * 1024)
                File.Delete(logPath);       // 不设上限会一直长

            File.AppendAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ({where}) {ex.GetType().Name}: {ex.Message}"
                + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine);
        }
        catch
        {
            // 日志写不进去就算了
        }
    }

    private async Task<long?> GetYesterdayTokensAsync(CancellationToken ct)
    {
        var yesterday = DateTime.Today.AddDays(-1);
        var key = Fmt.DayStartUnix(yesterday);
        if (_yesterdayDayKey == key) return _yesterdayTokens;

        try
        {
            var start = key;
            var end = Fmt.DayStartUnix(DateTime.Today) - 1;
            var log = await _client.GetLogSummaryAsync(start, end, ct);
            _yesterdayTokens = log.TotalTokens;
            _yesterdayDayKey = key;
            return _yesterdayTokens;
        }
        catch
        {
            return null;
        }
    }

}
