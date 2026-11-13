using System.Security.Cryptography;
using System.Text;

namespace ShortLink.Infrastructure.Analytics;

/// <summary>User-Agent 解析结果。</summary>
public sealed record UserAgentInfo(string Device, string Browser, string OperatingSystem, bool IsBot);

/// <summary>
/// 轻量 UA 解析：不引入第三方 UA 库，覆盖主流浏览器/系统/爬虫，未知值统一归入 "other"。
/// 需要更细的设备型号时可替换为专门的 UA 解析组件，接口保持不变。
/// </summary>
public static class UserAgentParser
{
    private static readonly string[] BotMarkers =
    [
        "bot", "spider", "crawler", "slurp", "preview", "curl/", "wget/",
        "python-requests", "httpclient", "okhttp", "headlesschrome", "phantomjs",
        "facebookexternalhit", "whatsapp", "telegrambot", "google-inspectiontool",
    ];

    public static UserAgentInfo Parse(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return new UserAgentInfo("unknown", "other", "other", false);
        }

        var ua = userAgent;
        var isBot = BotMarkers.Any(marker => ua.Contains(marker, StringComparison.OrdinalIgnoreCase));

        return new UserAgentInfo(DetectDevice(ua), DetectBrowser(ua), DetectOs(ua), isBot);
    }

    private static string DetectDevice(string ua)
    {
        if (ua.Contains("iPad", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("Tablet", StringComparison.OrdinalIgnoreCase)
            || (ua.Contains("Android", StringComparison.OrdinalIgnoreCase)
                && !ua.Contains("Mobile", StringComparison.OrdinalIgnoreCase)))
        {
            return "tablet";
        }

        if (ua.Contains("Mobile", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("Android", StringComparison.OrdinalIgnoreCase))
        {
            return "mobile";
        }

        return "desktop";
    }

    private static string DetectBrowser(string ua)
    {
        if (ua.Contains("MicroMessenger", StringComparison.OrdinalIgnoreCase))
        {
            return "WeChat";
        }

        if (ua.Contains("DingTalk", StringComparison.OrdinalIgnoreCase))
        {
            return "DingTalk";
        }

        if (ua.Contains("QQ/", StringComparison.OrdinalIgnoreCase))
        {
            return "QQ";
        }

        if (ua.Contains("Edg/", StringComparison.OrdinalIgnoreCase))
        {
            return "Edge";
        }

        if (ua.Contains("OPR/", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("Opera", StringComparison.OrdinalIgnoreCase))
        {
            return "Opera";
        }

        if (ua.Contains("Firefox/", StringComparison.OrdinalIgnoreCase))
        {
            return "Firefox";
        }

        if (ua.Contains("Chrome/", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("CriOS", StringComparison.OrdinalIgnoreCase))
        {
            return "Chrome";
        }

        if (ua.Contains("Safari/", StringComparison.OrdinalIgnoreCase))
        {
            return "Safari";
        }

        return "other";
    }

    private static string DetectOs(string ua)
    {
        if (ua.Contains("Windows NT", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows";
        }

        if (ua.Contains("Android", StringComparison.OrdinalIgnoreCase))
        {
            return "Android";
        }

        if (ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("iPad", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("iOS", StringComparison.OrdinalIgnoreCase))
        {
            return "iOS";
        }

        if (ua.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("Macintosh", StringComparison.OrdinalIgnoreCase))
        {
            return "macOS";
        }

        if (ua.Contains("Linux", StringComparison.OrdinalIgnoreCase))
        {
            return "Linux";
        }

        return "other";
    }
}

/// <summary>
/// 匿名访客指纹：IP + UA + 统计日期 的 SHA-256 截断，仅用于独立访客去重，不可反推原始 IP。
/// 日期取 <see cref="ShortLink.Application.Abstractions.IStatisticsCalendar"/> 算出的统计日界，
/// 而不是 UTC 日期——否则 <c>Analytics:TimeZone</c> 非 UTC 时，去重窗口与 <c>click_date</c> 分桶错位。
/// </summary>
public static class VisitorFingerprint
{
    public static string Compute(string? ip, string? userAgent, DateOnly clickDate)
    {
        var raw = $"{ip}|{userAgent}|{clickDate:yyyyMMdd}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..32];
    }
}
