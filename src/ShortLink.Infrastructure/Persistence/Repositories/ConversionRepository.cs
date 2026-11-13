using Microsoft.EntityFrameworkCore;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Domain.Analytics;

namespace ShortLink.Infrastructure.Persistence.Repositories;

public sealed class ConversionRepository(ShortLinkDbContext db) : IConversionRepository
{
    public Task<bool> ExistsAsync(long linkId, string eventId, CancellationToken cancellationToken)
        => db.Conversions.AnyAsync(c => c.LinkId == linkId && c.EventId == eventId, cancellationToken);

    public async Task<ConversionAggregate> GetLinkAggregateAsync(
        long linkId,
        DateTime fromUtc,
        DateTime toUtcExclusive,
        CancellationToken cancellationToken)
    {
        var rows = await db.Conversions
            .AsNoTracking()
            .Where(c => c.LinkId == linkId && c.CreatedAtUtc >= fromUtc && c.CreatedAtUtc < toUtcExclusive)
            .GroupBy(c => c.RouteId)
            .Select(g => new { RouteId = g.Key, Count = g.LongCount(), Value = g.Sum(x => x.Value) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byRoute = rows
            .Select(r => new VariantConversion(r.RouteId, r.Count, r.Value))
            .ToList();

        return new ConversionAggregate(
            byRoute.Sum(r => r.Count),
            byRoute.Sum(r => r.Value),
            byRoute);
    }

    public Task<long> GetLinkTotalAsync(long linkId, CancellationToken cancellationToken)
        => db.Conversions.AsNoTracking().LongCountAsync(c => c.LinkId == linkId, cancellationToken);

    public async Task<(long Count, decimal Value)> GetWorkspaceTotalsAsync(
        long workspaceId,
        DateTime? fromUtc,
        CancellationToken cancellationToken)
    {
        // 常量分组让 COUNT/SUM 都在 SQL 完成；空集返回默认元组而不是拉回全部 Value 行。
        var totals = await db.Conversions
            .AsNoTracking()
            .Where(c => c.WorkspaceId == workspaceId && (!fromUtc.HasValue || c.CreatedAtUtc >= fromUtc.Value))
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.LongCount(), Value = g.Sum(c => c.Value) })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return (totals?.Count ?? 0, totals?.Value ?? 0m);
    }

    public async Task<bool> TryAddAsync(ConversionEvent conversion, CancellationToken cancellationToken)
    {
        db.Conversions.Add(conversion);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException exception) when (exception.IsUniqueConstraintViolation())
        {
            // 唯一错误仍需确认就是当前 (link_id, event_id)，不能把其他唯一键错误吞成重复回调。
            db.Entry(conversion).State = EntityState.Detached;
            if (await ExistsAsync(conversion.LinkId, conversion.EventId, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            throw;
        }
    }
}
