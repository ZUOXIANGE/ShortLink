namespace ShortLink.Infrastructure.Options;

/// <summary>缓存配置。</summary>
public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    /// <summary>留空则只用进程内缓存；配置后启用 Redis 分布式缓存 + 背板。</summary>
    public string? RedisConnectionString { get; set; }

    public int LinkTtlSeconds { get; set; } = 60;

    public int DomainTtlSeconds { get; set; } = 60;
}
