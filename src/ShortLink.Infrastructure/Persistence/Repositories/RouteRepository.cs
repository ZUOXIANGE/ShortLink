using Microsoft.EntityFrameworkCore;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Domain.Links;

namespace ShortLink.Infrastructure.Persistence.Repositories;

public sealed class RouteRepository(ShortLinkDbContext db) : IRouteRepository
{
    public async Task<IReadOnlyList<LinkRoute>> ListByLinkAsync(
        long linkId,
        CancellationToken cancellationToken)
        => await db.Routes
            .Where(r => r.LinkId == linkId)
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<bool> ExistsForLinkAsync(long routeId, long linkId, CancellationToken cancellationToken)
        => db.Routes.AnyAsync(route => route.Id == routeId && route.LinkId == linkId, cancellationToken);

    public async Task<IReadOnlyDictionary<long, int>> CountByLinksAsync(
        IReadOnlyList<long> linkIds,
        CancellationToken cancellationToken)
    {
        if (linkIds.Count == 0)
        {
            return new Dictionary<long, int>();
        }

        var rows = await db.Routes
            .AsNoTracking()
            .Where(r => linkIds.Contains(r.LinkId))
            .GroupBy(r => r.LinkId)
            .Select(g => new { LinkId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.ToDictionary(r => r.LinkId, r => r.Count);
    }

    public void Add(LinkRoute route) => db.Routes.Add(route);

    public void RemoveRange(IEnumerable<LinkRoute> routes) => db.Routes.RemoveRange(routes);
}
