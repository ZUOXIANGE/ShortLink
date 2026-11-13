using Microsoft.EntityFrameworkCore;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Contracts;
using ShortLink.Contracts.Links;
using ShortLink.Domain.Links;

namespace ShortLink.Infrastructure.Persistence.Repositories;

public sealed class LinkRepository(ShortLinkDbContext db) : IShortLinkRepository
{
    public async Task<PagedResult<Link>> QueryAsync(
        LinkQuery query,
        long workspaceId,
        long? currentUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var source = Filtered(query, workspaceId, currentUserId, nowUtc);
        var total = await source.LongCountAsync(cancellationToken).ConfigureAwait(false);

        source = ApplySort(source, query.Sort);

        var items = await source
            .Skip(query.Skip)
            .Take(query.EffectivePageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<Link>(items, total, query.EffectivePage, query.EffectivePageSize);
    }

    public Task<Link?> FindByIdAsync(long id, CancellationToken cancellationToken)
        => db.Links.FirstOrDefaultAsync(l => l.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Link>> FindManyAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken)
        => await db.Links
            .Where(l => ids.Contains(l.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<Link?> FindByCodeAsync(long domainId, string code, CancellationToken cancellationToken)
        => db.Links
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.DomainId == domainId && l.Code == code, cancellationToken);

    public Task<bool> CodeExistsAsync(long domainId, string code, CancellationToken cancellationToken)
        => db.Links.AnyAsync(l => l.DomainId == domainId && l.Code == code, cancellationToken);

    public Task<bool> CodeExistsExceptAsync(
        long domainId,
        string code,
        long exceptId,
        CancellationToken cancellationToken)
        => db.Links.AnyAsync(
            l => l.DomainId == domainId && l.Code == code && l.Id != exceptId,
            cancellationToken);

    public async Task<LinkCounters> GetCountersAsync(
        long workspaceId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var scoped = db.Links.Where(l => l.WorkspaceId == workspaceId);

        var total = await scoped.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var enabled = await scoped
            .LongCountAsync(
                l => l.Status == LinkStatus.Enabled
                     && (l.StartsAtUtc == null || l.StartsAtUtc <= nowUtc)
                     && (l.ExpiresAtUtc == null || l.ExpiresAtUtc > nowUtc)
                     && (l.MaxClicks == null || l.ClickCount < l.MaxClicks),
                cancellationToken)
            .ConfigureAwait(false);
        var expired = await scoped
            .LongCountAsync(l => l.ExpiresAtUtc != null && l.ExpiresAtUtc <= nowUtc, cancellationToken)
            .ConfigureAwait(false);

        return new LinkCounters(total, enabled, expired);
    }

    public Task<int> CountByDomainAsync(long domainId, CancellationToken cancellationToken)
        => db.Links.CountAsync(l => l.DomainId == domainId, cancellationToken);

    public async Task<bool> TrySetConversionSecretAsync(
        long id,
        string secret,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var affected = await db.Links
            .Where(l => l.Id == id && (l.ConversionSecret == null || l.ConversionSecret == string.Empty))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(l => l.ConversionSecret, secret)
                    .SetProperty(l => l.UpdatedAtUtc, nowUtc)
                    .SetProperty(l => l.ConcurrencyToken, Guid.NewGuid().ToString("N")),
                cancellationToken)
            .ConfigureAwait(false);

        return affected == 1;
    }

    public Task<string?> GetConversionSecretAsync(long id, CancellationToken cancellationToken)
        => db.Links
            .AsNoTracking()
            .Where(l => l.Id == id)
            .Select(l => l.ConversionSecret)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<bool> DisableIfEnabledAsync(long id, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var affected = await db.Links
            .Where(l => l.Id == id && l.Status == LinkStatus.Enabled)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(l => l.Status, LinkStatus.Disabled)
                    .SetProperty(l => l.UpdatedAtUtc, nowUtc)
                    .SetProperty(l => l.ConcurrencyToken, Guid.NewGuid().ToString("N")),
                cancellationToken)
            .ConfigureAwait(false);

        return affected == 1;
    }

    public IAsyncEnumerable<Link> StreamAsync(
        LinkQuery query,
        long workspaceId,
        long? currentUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
        => ApplySort(Filtered(query, workspaceId, currentUserId, nowUtc), query.Sort)
            // 导出上限：10 万行足够覆盖单次人工分析，超过则应改用离线归档。
            .Take(100_000)
            .AsAsyncEnumerable();

    public void Add(Link link) => db.Links.Add(link);

    public void Remove(Link link) => db.Links.Remove(link);

    private IQueryable<Link> Filtered(
        LinkQuery query,
        long workspaceId,
        long? currentUserId,
        DateTime nowUtc)
    {
        var source = db.Links
            .AsNoTracking()
            .Where(l => l.WorkspaceId == workspaceId);

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            source = source.Where(l =>
                l.Code.Contains(keyword)
                || l.TargetUrl.Contains(keyword)
                || (l.Title != null && l.Title.Contains(keyword)));
        }

        if (query.DomainId is { } domainId)
        {
            source = source.Where(l => l.DomainId == domainId);
        }

        if (query.Mine == true && currentUserId is { } me)
        {
            source = source.Where(l => l.CreatedByUserId == me);
        }
        else if (query.CreatedByUserId is { } creator)
        {
            source = source.Where(l => l.CreatedByUserId == creator);
        }

        source = query.Status?.Trim().ToLowerInvariant() switch
        {
            "enabled" => source.Where(l =>
                l.Status == LinkStatus.Enabled
                && (l.StartsAtUtc == null || l.StartsAtUtc <= nowUtc)
                && (l.ExpiresAtUtc == null || l.ExpiresAtUtc > nowUtc)
                && (l.MaxClicks == null || l.ClickCount < l.MaxClicks)),
            "disabled" => source.Where(l =>
                l.Status == LinkStatus.Disabled
                && (l.StartsAtUtc == null || l.StartsAtUtc <= nowUtc)
                && (l.ExpiresAtUtc == null || l.ExpiresAtUtc > nowUtc)
                && (l.MaxClicks == null || l.ClickCount < l.MaxClicks)),
            "expired" => source.Where(l =>
                l.ExpiresAtUtc != null
                && l.ExpiresAtUtc <= nowUtc
                && (l.MaxClicks == null || l.ClickCount < l.MaxClicks)),
            "capped" => source.Where(l => l.MaxClicks != null && l.ClickCount >= l.MaxClicks),
            _ => source,
        };

        return source;
    }

    private static IQueryable<Link> ApplySort(IQueryable<Link> source, string? sort)
        => sort?.Trim().ToLowerInvariant() switch
        {
            "created_asc" => source.OrderBy(l => l.Id),
            "clicks_desc" => source.OrderByDescending(l => l.ClickCount).ThenByDescending(l => l.Id),
            "last_access_desc" => source.OrderByDescending(l => l.LastAccessedAtUtc).ThenByDescending(l => l.Id),
            _ => source.OrderByDescending(l => l.Id),
        };
}
