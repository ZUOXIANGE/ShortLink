namespace ShortLink.Domain.Security;

/// <summary>安全事件类型。</summary>
public enum SecurityEventType
{
    /// <summary>来源 IP 命中黑名单。</summary>
    IpBlocked = 0,

    /// <summary>来源 IP 不在白名单内。</summary>
    IpNotAllowed = 1,

    /// <summary>命中机器人拦截策略。</summary>
    BotBlocked = 2,

    /// <summary>目标地址被规则扫描拒绝。</summary>
    UrlRejected = 3,

    /// <summary>外部滥用举报。</summary>
    AbuseReported = 4,

    /// <summary>点击量达到封顶阈值并自动停用。</summary>
    ClickCapReached = 5,
}

/// <summary>
/// 安全事件流水。写入路径为「跳转热路径上的轻量插入」与「后台回填」，
/// 不参与短链聚合事务，避免影响跳转时延。
/// </summary>
public sealed class SecurityEvent
{
    private SecurityEvent()
    {
        Message = string.Empty;
    }

    public long Id { get; private set; }

    public long WorkspaceId { get; private set; }

    public long? LinkId { get; private set; }

    public string? Code { get; private set; }

    public SecurityEventType Type { get; private set; }

    public string Message { get; private set; }

    public string? Ip { get; private set; }

    public string? UserAgent { get; private set; }

    public string? Country { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public static SecurityEvent Create(
        SecurityEventType type,
        string message,
        long workspaceId,
        long? linkId,
        string? code,
        string? ip,
        string? userAgent,
        string? country,
        DateTime nowUtc)
        => new()
        {
            Type = type,
            Message = Truncate(message, 500) ?? string.Empty,
            WorkspaceId = workspaceId,
            LinkId = linkId,
            Code = Truncate(code, 32),
            Ip = Truncate(ip, 64),
            UserAgent = Truncate(userAgent, 512),
            Country = Truncate(country, 64),
            CreatedAtUtc = nowUtc,
        };

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
