using Microsoft.Extensions.Options;
using ShortLink.Application.Abstractions;
using ShortLink.Infrastructure.Options;

namespace ShortLink.Infrastructure.Caching;

/// <summary>
/// 缓存拓扑的只读快照。取值在启动时确定（背板只在注册阶段决定），因此注册为单例，
/// 不必每次请求都读配置。
/// </summary>
public sealed class CacheTopology(IOptions<CacheOptions> options) : ICacheTopology
{
    private readonly CacheOptions _cache = options.Value;

    public bool HasDistributedBackplane => !string.IsNullOrWhiteSpace(_cache.RedisConnectionString);

    public string Backplane => HasDistributedBackplane ? "redis" : "in-process";

    public int LinkTtlSeconds => Math.Max(5, _cache.LinkTtlSeconds);
}
