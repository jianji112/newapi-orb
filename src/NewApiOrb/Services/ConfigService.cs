using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using NewApiOrb.Models;

namespace NewApiOrb.Services;

public static class ConfigService
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NewApiOrb");

    public static string FilePath => Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), JsonOpts);
                if (cfg is not null) return Normalize(cfg);
            }
        }
        catch
        {
            // 配置损坏时回落到默认值，不让程序起不来
        }

        var fresh = FromEnv();
        Save(fresh);
        return fresh;
    }

    public static void Save(AppConfig cfg)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(cfg, JsonOpts));
    }

    /// <summary>
    /// 首次运行：若用户主目录下存在 <c>.newapi-orb.env</c>，从中读取 NEWAPI_* 作为默认连接参数，
    /// 免去手填。文件不存在则用内置默认值（localhost:3000 + 空令牌，此时会弹出设置窗口）。
    /// </summary>
    private static AppConfig FromEnv()
    {
        var cfg = new AppConfig();
        try
        {
            var envPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".newapi-orb.env");

            if (!File.Exists(envPath)) return cfg;

            foreach (var raw in File.ReadAllLines(envPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;

                var eq = line.IndexOf('=');
                if (eq <= 0) continue;

                var key = line[..eq].Trim();
                var val = line[(eq + 1)..].Trim().Trim('"', '\'');

                switch (key)
                {
                    case "NEWAPI_BASE_URL": cfg.BaseUrl = val; break;
                    case "NEWAPI_ACCESS_TOKEN": cfg.AccessToken = val; break;
                    case "NEWAPI_USER_ID" when int.TryParse(val, out var uid): cfg.UserId = uid; break;
                }
            }
        }
        catch
        {
            // .env 读不到就用内置默认
        }

        return cfg;
    }

    private static AppConfig Normalize(AppConfig cfg)
    {
        // 兜底也用 new-api 官方默认端口，不要写死某台内网机器（见 AppConfig.BaseUrl 注释）
        if (string.IsNullOrWhiteSpace(cfg.BaseUrl)) cfg.BaseUrl = "http://localhost:3000";
        if (cfg.UserId <= 0) cfg.UserId = 1;
        if (cfg.RefreshSeconds < 5) cfg.RefreshSeconds = 5;
        if (cfg.RefreshSeconds > 3600) cfg.RefreshSeconds = 3600;
        if (cfg.BallSize < 60) cfg.BallSize = 60;
        if (cfg.BallSize > 260) cfg.BallSize = 260;
        if (cfg.Opacity < 0.2) cfg.Opacity = 0.2;
        if (cfg.Opacity > 1) cfg.Opacity = 1;

        cfg.VisibleFields ??= new List<string>();
        // 剔除已经不认识的字段键
        var known = MetricFields.All.Select(m => m.Key).ToHashSet();
        cfg.VisibleFields = cfg.VisibleFields.Where(known.Contains).Distinct().ToList();
        if (!known.Contains(cfg.PrimaryField)) cfg.PrimaryField = MetricFields.TodayTotalTokens;

        return cfg;
    }
}
