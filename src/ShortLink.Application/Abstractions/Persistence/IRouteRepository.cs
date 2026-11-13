using ShortLink.Domain.Links;

namespace ShortLink.Application.Abstractions.Persistence;

/// <summary>路由规则仓储。</summary>
public interface IRouteRepository
{
    Task<IReadOnlyList<LinkRoute>> ListByLinkAsync(long linkId, CancellationToken cancellationToken);

    Task<bool> ExistsForLinkAsync(long routeId, long linkId, CancellationToken cancellationToken);

    /// <summary>取多条短链的分支数量，列表页用于展示「条件路由」标记。</summary>
    Task<IReadOnlyDictionary<long, int>> CountByLinksAsync(
        IReadOnlyList<long> linkIds,
        CancellationToken cancellationToken);

    void Add(LinkRoute route);

    void RemoveRange(IEnumerable<LinkRoute> routes);
}
