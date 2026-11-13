namespace ShortLink.Application.Abstractions;

/// <summary>
/// 分布式缓存可用性探测。就绪探针用它判断「本实例能不能正常工作」：
/// 未配置 Redis 时缓存是进程内的，永远健康；配置了 Redis 就必须能 ping 通。
/// </summary>
public interface ICacheHealthProbe
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 缓存拓扑：本实例的缓存失效能不能传到别的实例。
/// <para>
/// 存在的理由是「静默降级」：没有 Redis 背板时，更新一条短链只失效本进程的缓存，
/// 其他实例要靠 TTL 自然过期（默认 60 秒）。这个行为不会报错、不会告警，
/// 只会表现为「停用了一条恶意短链，别的节点还在继续跳转」——
/// 把它显式暴露出来，才能让部署方知道自己站在哪一侧。
/// </para>
/// </summary>
public interface ICacheTopology
{
    /// <summary>是否存在跨实例的失效通道（即是否配置了 Redis 背板）。</summary>
    bool HasDistributedBackplane { get; }

    /// <summary>拓扑标识：<c>redis</c>（有背板）或 <c>in-process</c>（仅本实例）。</summary>
    string Backplane { get; }

    /// <summary>短链缓存 TTL（秒）。无背板时它决定了「其他地方多久能看到变更」的上限。</summary>
    int LinkTtlSeconds { get; }
}
