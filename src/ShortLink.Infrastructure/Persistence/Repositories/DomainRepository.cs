using Microsoft.EntityFrameworkCore;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Domain.Domains;

namespace ShortLink.Infrastructure.Persistence.Repositories;

public sealed class DomainRepository(ShortLinkDbContext db) : IDomainRepository
{
    public async Task<IReadOnlyList<ShortDomain>> ListAsync(CancellationToken cancellationToken)
        => await db.Domains
            .AsNoTracking()
            .OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.Host)
            .ToListAsync(cancellationToken);

    public Task<ShortDomain?> FindByIdAsync(long id, CancellationToken cancellationToken)
        => db.Domains.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

    public Task<ShortDomain?> FindByHostAsync(string host, CancellationToken cancellationToken)
        => db.Domains.AsNoTracking().FirstOrDefaultAsync(d => d.Host == host && d.IsEnabled, cancellationToken);

    public Task<ShortDomain?> FindDefaultAsync(CancellationToken cancellationToken)
        => db.Domains
            .AsNoTracking()
            .Where(d => d.IsDefault && d.IsEnabled)
            .OrderBy(d => d.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<bool> HostExistsAsync(string host, CancellationToken cancellationToken)
        => db.Domains.AnyAsync(d => d.Host == host, cancellationToken);

    public async Task<IReadOnlyDictionary<long, long>> CountLinksByDomainAsync(
        long workspaceId,
        CancellationToken cancellationToken)
    {
        var rows = await db.Links
            .AsNoTracking()
            .Where(l => l.WorkspaceId == workspaceId)
            .GroupBy(l => l.DomainId)
            .Select(g => new { DomainId = g.Key, Count = g.LongCount() })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(r => r.DomainId, r => r.Count);
    }

    public Task ClearDefaultAsync(CancellationToken cancellationToken)
        => db.Domains
            .Where(d => d.IsDefault)
            .ExecuteUpdateAsync(
                s => s.SetProperty(d => d.IsDefault, false).SetProperty(d => d.DefaultSlot, (int?)null),
                cancellationToken);

    public void Add(ShortDomain domain) => db.Domains.Add(domain);

    public void Remove(ShortDomain domain) => db.Domains.Remove(domain);
}
