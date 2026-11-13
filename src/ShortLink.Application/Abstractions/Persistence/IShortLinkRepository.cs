using ShortLink.Contracts;
using ShortLink.Contracts.Links;
using ShortLink.Domain.Links;

namespace ShortLink.Application.Abstractions.Persistence;

/// <summary>短链统计计数。</summary>
public sealed record LinkCounters(long Total, long Enabled, long Expired);

/// <summary>短链仓储。</summary>
public interface IShortLinkRepository
{
    /// <summary>分页查询。工作区过滤在仓储层完成，应用层不需要重复拼条件。</summary>
    Task<PagedResult<Link>> QueryAsync(
        LinkQuery query,
        long workspaceId,
        long? currentUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    Task<Link?> FindByIdAsync(long id, CancellationToken cancellationToken);

    /// <summary>批量操作：一次取回全部实体（跟踪态），保证单事务内的修改可见。</summary>
    Task<IReadOnlyList<Link>> FindManyAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken);

    Task<Link?> FindByCodeAsync(long domainId, string code, CancellationToken cancellationToken);

    Task<bool> CodeExistsAsync(long domainId, string code, CancellationToken cancellationToken);

    Task<bool> CodeExistsExceptAsync(long domainId, string code, long exceptId, CancellationToken cancellationToken);

    Task<LinkCounters> GetCountersAsync(long workspaceId, DateTime nowUtc, CancellationToken cancellationToken);

    Task<int> CountByDomainAsync(long domainId, CancellationToken cancellationToken);

    /// <summary>仅在尚未设置密钥时原子写入，返回是否由本次调用写入。</summary>
    Task<bool> TrySetConversionSecretAsync(
        long id,
        string secret,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    /// <summary>直接读取最终持久化密钥，绕过当前 DbContext 的跟踪快照。</summary>
    Task<string?> GetConversionSecretAsync(long id, CancellationToken cancellationToken);

    /// <summary>仅在短链仍启用时原子停用，避免依赖未跟踪实体。</summary>
    Task<bool> DisableIfEnabledAsync(long id, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>流式导出：逐行读取，避免一次性把全表加载进内存。</summary>
    IAsyncEnumerable<Link> StreamAsync(
        LinkQuery query,
        long workspaceId,
        long? currentUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    void Add(Link link);

    void Remove(Link link);
}
