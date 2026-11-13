using ShortLink.Domain.Links;

namespace ShortLink.Application.Abstractions;

/// <summary>
/// 跳转时需要的短链快照，避免每次请求回源数据库。
/// 除基础字段外还包含安全策略与路由分支，保证一次缓存读取即可完成全部跳转判定。
/// </summary>
public sealed class CachedLink
{
    public long Id { get; init; }

    public long WorkspaceId { get; init; }

    public long DomainId { get; init; }

    public string Code { get; init; } = string.Empty;

    public string TargetUrl { get; init; } = string.Empty;

    /// <summary>条件路由全部未命中时的兜底地址。</summary>
    public string? FallbackUrl { get; init; }

    public int RedirectType { get; init; } = 302;

    public int Status { get; init; }

    public DateTime? StartsAtUtc { get; init; }

    public DateTime? ExpiresAtUtc { get; init; }

    public long ClickCount { get; init; }

    public long? MaxClicks { get; init; }

    public string? PasswordHash { get; init; }

    public int BotPolicy { get; init; }

    /// <summary>已解析的 IP 白名单，避免每次跳转重复解析字符串。</summary>
    public IpAccessList AllowList { get; init; } = IpAccessList.Empty;

    public IpAccessList BlockList { get; init; } = IpAccessList.Empty;

    public bool AntiBlockEnabled { get; init; }

    /// <summary>路由分支快照，按优先级从高到低由选择器排序。</summary>
    public IReadOnlyList<RouteSnapshot> Routes { get; init; } = [];

    /// <summary>负缓存标记：该短码在域名下不存在，用于抵御随机短码扫描造成的缓存穿透。</summary>
    public static CachedLink Missing { get; } = new();

    public bool IsMissing => Id == 0;

    /// <summary>实际生效的兜底地址（未配置兜底时回落到主目标地址）。</summary>
    public string EffectiveFallbackUrl
        => string.IsNullOrWhiteSpace(FallbackUrl) ? TargetUrl : FallbackUrl;
}

/// <summary>域名快照。<see cref="Id"/> 为 0 表示该 Host 未配置（负缓存，防穿透）。</summary>
public sealed record CachedDomain(long Id, string Host, string Scheme)
{
    public static CachedDomain Missing { get; } = new(0, string.Empty, string.Empty);

    public bool IsMissing => Id == 0;
}

/// <summary>
/// 跳转链路缓存。以 GetOrSet 形式暴露，让实现层（FusionCache）统一提供
/// 防击穿（stampede protection）与故障兜底（fail-safe）。
/// </summary>
public interface ILinkCache
{
    Task<CachedLink> GetOrSetLinkAsync(
        long domainId,
        string code,
        Func<CancellationToken, Task<CachedLink>> factory,
        CancellationToken cancellationToken);

    Task RemoveLinkAsync(long domainId, string code, CancellationToken cancellationToken);

    Task<CachedDomain> GetOrSetDomainAsync(
        string host,
        Func<CancellationToken, Task<CachedDomain>> factory,
        CancellationToken cancellationToken);

    Task RemoveDomainAsync(string host, CancellationToken cancellationToken);

    /// <summary>清空 Host 与未知 Host 的默认回退快照，在全局域名写操作提交后调用。</summary>
    Task ClearDomainsAsync(CancellationToken cancellationToken);
}
