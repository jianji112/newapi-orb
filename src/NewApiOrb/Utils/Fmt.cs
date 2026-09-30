using System.Globalization;

namespace NewApiOrb.Utils;

public static class Fmt
{
    /// <summary>18369109 → "18.37M"；106247 → "106.2K"。</summary>
    public static string Compact(long n)
    {
        double v = n;
        return v switch
        {
            >= 1_000_000_000 => (v / 1_000_000_000).ToString("0.##", CultureInfo.InvariantCulture) + "B",
            >= 1_000_000 => (v / 1_000_000).ToString("0.##", CultureInfo.InvariantCulture) + "M",
            >= 1_000 => (v / 1_000).ToString("0.#", CultureInfo.InvariantCulture) + "K",
            _ => n.ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>18369109 → "18,369,109"。</summary>
    public static string Group(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>展示用：大数走 Compact，小数走原值。</summary>
    public static string Auto(long n) => n >= 100_000 ? Compact(n) : Group(n);

    public static string Seconds(double s) => s <= 0 ? "—" : s.ToString("0.#", CultureInfo.InvariantCulture) + "s";

    /// <summary>生成速度：49.4 → "49 tok/s"；不足 10 时保留一位小数 → "8.4 tok/s"（球上空间极窄）。</summary>
    public static string Speed(double tokPerSec) =>
        (tokPerSec >= 10
            ? tokPerSec.ToString("0", CultureInfo.InvariantCulture)
            : tokPerSec.ToString("0.0", CultureInfo.InvariantCulture)) + " tok/s";

    /// <summary>带符号的百分比：+12.3% / -4.1%。</summary>
    public static string Percent(double v) => (v >= 0 ? "+" : "") + v.ToString("0.#", CultureInfo.InvariantCulture) + "%";

    /// <summary>把 unix 秒转成本地 DateTime。</summary>
    public static DateTime FromUnix(long seconds) =>
        DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().DateTime;

    /// <summary>某天 00:00:00 的 unix 秒。</summary>
    public static long DayStartUnix(DateTime day) =>
        new DateTimeOffset(day.Date, TimeZoneInfo.Local.GetUtcOffset(day.Date)).ToUnixTimeSeconds();

    public static long NowUnix() => DateTimeOffset.Now.ToUnixTimeSeconds();
}
