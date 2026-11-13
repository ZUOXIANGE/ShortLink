using Microsoft.Extensions.Options;
using ShortLink.Application.Abstractions;
using ShortLink.Infrastructure.Options;
using ZiggyCreatures.Caching.Fusion;

namespace ShortLink.Infrastructure.Caching;

/// <summary>
/// FusionCache 实现。启用防击穿（同一 key 只有一个回源），负缓存抵御随机短码扫描。
/// 回源异常保持原异常语义；安全状态不使用旧启用快照 fail-safe。
/// </summary>
public sealed class FusionLinkCache(IFusionCache cache, IOptions<CacheOptions> options) : ILinkCache
{
    private const string DomainTag = "shortlink:domains";
    public async Task<CachedLink> GetOrSetLinkAsync(
        long domainId,
        string code,
        Func<CancellationToken, Task<CachedLink>> factory,
        CancellationToken cancellationToken)
    {
        var entryOptions = new FusionCacheEntryOptions
        {
            Duration = TimeSpan.FromSeconds(Math.Max(5, options.Value.LinkTtlSeconds)),
            // 旧启用快照在回源故障时继续跳转会绕过停用/删除，安全路径禁用 fail-safe。
            IsFailSafeEnabled = false,
        };

        return await cache.GetOrSetAsync<CachedLink>(
            LinkKey(domainId, code),
            (context, token) => factory(token),
            options: entryOptions,
            tags: null,
            token: cancellationToken);
    }

    public async Task RemoveLinkAsync(long domainId, string code, CancellationToken cancellationToken)
        => await cache.RemoveAsync(LinkKey(domainId, code), token: cancellationToken);

    public async Task<CachedDomain> GetOrSetDomainAsync(
        string host,
        Func<CancellationToken, Task<CachedDomain>> factory,
        CancellationToken cancellationToken)
    {
        var entryOptions = new FusionCacheEntryOptions
        {
            Duration = TimeSpan.FromSeconds(Math.Max(5, options.Value.DomainTtlSeconds)),
            IsFailSafeEnabled = false,
        };

        return await cache.GetOrSetAsync<CachedDomain>(
            DomainKey(host),
            (context, token) => factory(token),
            options: entryOptions,
            tags: [DomainTag],
            token: cancellationToken);
    }

    public async Task RemoveDomainAsync(string host, CancellationToken cancellationToken)
        => await cache.RemoveAsync(DomainKey(host), token: cancellationToken);

    public async Task ClearDomainsAsync(CancellationToken cancellationToken)
        => await cache.RemoveByTagAsync(DomainTag, token: cancellationToken);

    private static string LinkKey(long domainId, string code) => $"shortlink:link:{domainId}:{code}";

    private static string DomainKey(string host) => $"shortlink:domain:{host}";
}
