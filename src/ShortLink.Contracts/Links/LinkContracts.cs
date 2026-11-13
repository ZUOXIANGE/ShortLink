namespace ShortLink.Contracts.Links;

/// <summary>短链。</summary>
public sealed record LinkDto(
    long Id,
    long WorkspaceId,
    long DomainId,
    string DomainHost,
    string Code,
    string ShortUrl,
    string TargetUrl,
    string? Title,
    string Status,
    int RedirectType,
    DateTime? ExpiresAtUtc,
    DateTime? StartsAtUtc,
    bool HasPassword,
    string? Remark,
    long ClickCount,
    long? MaxClicks,
    string BotPolicy,
    /// <summary>IP 白名单原文（逗号 / 换行分隔）。更新是全量覆盖，故详情必须回传原文。</summary>
    string? IpAllowList,
    /// <summary>IP 黑名单原文。</summary>
    string? IpBlockList,
    string? FallbackUrl,
    bool AntiBlockEnabled,
    bool HasConversionSecret,
    int RouteCount,
    long? CreatedByUserId,
    string? CreatedByName,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? LastAccessedAtUtc);

/// <summary>新建短链。Code 留空时由服务端生成。</summary>
public sealed record CreateLinkRequest
{
    public long? DomainId { get; set; }

    public string TargetUrl { get; set; } = string.Empty;

    public string? Code { get; set; }

    public string? Title { get; set; }

    public int RedirectType { get; set; } = 302;

    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>生效时间，留空立即生效。</summary>
    public DateTime? StartsAtUtc { get; set; }

    public string? Password { get; set; }

    public string? Remark { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>点击封顶，达到后自动停用；留空不限制。</summary>
    public long? MaxClicks { get; set; }

    /// <summary><c>allow</c>（默认）/ <c>block</c> / <c>ignore_count</c>。</summary>
    public string? BotPolicy { get; set; }

    public string? IpAllowList { get; set; }

    public string? IpBlockList { get; set; }

    /// <summary>条件路由全部未命中时的兜底地址。</summary>
    public string? FallbackUrl { get; set; }

    /// <summary>微信 / QQ 内访问时展示防红中间页。</summary>
    public bool AntiBlockEnabled { get; set; }
}

/// <summary>更新短链。DomainId 与 Code 不可变更，保证已分发的短链始终有效。</summary>
public sealed record UpdateLinkRequest
{
    public string TargetUrl { get; set; } = string.Empty;

    public string? Title { get; set; }

    public int RedirectType { get; set; } = 302;

    public DateTime? ExpiresAtUtc { get; set; }

    public DateTime? StartsAtUtc { get; set; }

    /// <summary>null 表示不修改密码；空字符串表示清除密码。</summary>
    public string? Password { get; set; }

    public string? Remark { get; set; }

    public bool Enabled { get; set; } = true;

    public long? MaxClicks { get; set; }

    public string? BotPolicy { get; set; }

    public string? IpAllowList { get; set; }

    public string? IpBlockList { get; set; }

    public string? FallbackUrl { get; set; }

    public bool AntiBlockEnabled { get; set; }
}

/// <summary>短链列表查询条件。</summary>
public sealed class LinkQuery : PageRequest
{
    public string? Keyword { get; set; }

    public long? DomainId { get; set; }

    /// <summary><c>enabled</c> / <c>disabled</c> / <c>expired</c> / <c>capped</c>，留空为全部。</summary>
    public string? Status { get; set; }

    /// <summary><c>created_desc</c>（默认）/ <c>created_asc</c> / <c>clicks_desc</c> / <c>last_access_desc</c>。</summary>
    public string? Sort { get; set; }

    /// <summary>只看自己创建的短链（团队成员视角）。可空以适配 <c>[AsParameters]</c> 的必填推断。</summary>
    public bool? Mine { get; set; }

    /// <summary>按创建人过滤（管理员视角）。</summary>
    public long? CreatedByUserId { get; set; }
}

/// <summary>机器人访问策略码值。</summary>
public static class BotPolicies
{
    public const string Allow = "allow";

    public const string Block = "block";

    public const string IgnoreCount = "ignore_count";

    public static readonly string[] All = [Allow, Block, IgnoreCount];

    public static bool IsValid(string? value)
        => string.IsNullOrWhiteSpace(value)
           || All.Contains(value.Trim().ToLowerInvariant(), StringComparer.Ordinal);
}

/// <summary>批量操作类型。</summary>
public static class BatchActions
{
    public const string Enable = "enable";

    public const string Disable = "disable";

    public const string Delete = "delete";

    public const string ChangeDomain = "change_domain";

    public const string ChangeExpiry = "change_expiry";

    public static readonly string[] All = [Enable, Disable, Delete, ChangeDomain, ChangeExpiry];

    public static bool IsValid(string? action)
        => !string.IsNullOrWhiteSpace(action)
           && All.Contains(action.Trim().ToLowerInvariant(), StringComparer.Ordinal);
}

/// <summary>批量操作请求。单次最多 500 条，逐条返回结果码。</summary>
public sealed class BatchLinkRequest
{
    public const int MaxItems = 500;

    /// <summary><c>enable</c> / <c>disable</c> / <c>delete</c> / <c>change_domain</c> / <c>change_expiry</c>。</summary>
    public string Action { get; set; } = string.Empty;

    public List<long> Ids { get; set; } = [];

    /// <summary><c>change_domain</c> 时的目标域名。</summary>
    public long? DomainId { get; set; }

    /// <summary><c>change_expiry</c> 时的过期时间；为空表示清除过期时间。</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary><c>change_expiry</c> 时的生效时间；为空表示不修改现有生效时间。</summary>
    public DateTime? StartsAtUtc { get; set; }
}

/// <summary>批量操作的单条结果。</summary>
public sealed record BatchItemResult(long Id, string? ShortCode, bool Success, int Code, string Message);

/// <summary>批量操作汇总结果。</summary>
public sealed record BatchResult(int Total, int Succeeded, int Failed, IReadOnlyList<BatchItemResult> Items);

/// <summary>转化回调签名参数。交由调用方在服务端完成签名，密钥不下发到浏览器。</summary>
public sealed record ConversionKeyDto(
    long LinkId,
    string LinkCode,
    string Secret,
    string Algorithm,
    string HeaderName,
    string SignatureFormat,
    string SignedPayloadFormat);

/// <summary>路由规则测试请求。用于上线前验证命中结果与分流比例。</summary>
public sealed class RouteTestRequest
{
    public string? Country { get; set; }

    public string? Province { get; set; }

    public string? Device { get; set; }

    public string? Browser { get; set; }

    public string? Os { get; set; }

    public string? Language { get; set; }

    public string? Referer { get; set; }

    public Dictionary<string, string> Query { get; set; } = [];

    public string? VisitorKey { get; set; }

    /// <summary>模拟访客数，用于估算分流比例；默认 1000，上限 10000。</summary>
    public int Repeat { get; set; } = 1000;
}
