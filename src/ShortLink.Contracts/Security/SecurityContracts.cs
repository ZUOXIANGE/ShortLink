namespace ShortLink.Contracts.Security;

/// <summary>安全事件列表项。</summary>
public sealed record SecurityEventDto(
    long Id,
    string Type,
    string TypeName,
    string Message,
    long? LinkId,
    string? Code,
    string? Ip,
    string? Country,
    DateTime CreatedAtUtc);

/// <summary>安全事件查询。</summary>
public sealed class SecurityEventQuery : PageRequest
{
    /// <summary><c>ip_blocked</c> / <c>ip_not_allowed</c> / <c>bot_blocked</c> / <c>url_rejected</c> / <c>abuse_reported</c> / <c>click_cap_reached</c>。</summary>
    public string? Type { get; set; }

    public long? LinkId { get; set; }

    public string? Code { get; set; }

    public int? Days { get; set; }
}

/// <summary>滥用举报（匿名可用，按 IP 限流）。</summary>
public sealed class AbuseReportRequest
{
    public string Code { get; set; } = string.Empty;

    /// <summary>举报原因，10~500 字。</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>联系方式，便于回访，可选。</summary>
    public string? Contact { get; set; }
}

/// <summary>安全事件类型的中文名与字符串码映射，供前后端共用。</summary>
public static class SecurityEventTypes
{
    public const string IpBlocked = "ip_blocked";

    public const string IpNotAllowed = "ip_not_allowed";

    public const string BotBlocked = "bot_blocked";

    public const string UrlRejected = "url_rejected";

    public const string AbuseReported = "abuse_reported";

    public const string ClickCapReached = "click_cap_reached";

    public static string ToCode(int type) => type switch
    {
        0 => IpBlocked,
        1 => IpNotAllowed,
        2 => BotBlocked,
        3 => UrlRejected,
        4 => AbuseReported,
        5 => ClickCapReached,
        _ => "unknown",
    };

    public static string ToName(string code) => code switch
    {
        IpBlocked => "IP 黑名单拦截",
        IpNotAllowed => "IP 不在白名单",
        BotBlocked => "机器人拦截",
        UrlRejected => "目标地址被拒",
        AbuseReported => "滥用举报",
        ClickCapReached => "点击封顶停用",
        _ => code,
    };
}
