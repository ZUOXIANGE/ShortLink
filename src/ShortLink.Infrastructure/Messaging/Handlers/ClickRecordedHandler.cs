using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Messaging;
using ShortLink.Domain.Links;
using ShortLink.Infrastructure.Diagnostics;
using ShortLink.Infrastructure.Persistence;
using Wolverine;

namespace ShortLink.Infrastructure.Messaging.Handlers;

/// <summary>
/// 点击消息的批量落库。明细、短链计数与分支计数在同一事务内按稳定 ClickId 幂等提交；
/// 唯一冲突时重查已存在事件并重试一次，以覆盖并发消费者同时处理同一 Kafka 消息的情况。
/// </summary>
public static class ClickRecordedHandler
{
    public static async Task Handle(
        ClickRecorded[] batch,
        ShortLinkDbContext db,
        ILinkCache cache,
        IClickQuota clickQuota,
        IMessageBus bus,
        ILogger<ClickRecordedBatchMarker> logger,
        CancellationToken cancellationToken)
    {
        if (batch.Length == 0)
        {
            return;
        }

        // 同一次批处理也可能包含重复消息，先折叠，避免唯一索引尚未介入前就放大 delta。
        var distinct = batch.GroupBy(click => click.ClickId).Select(group => group.First()).ToArray();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var eventIds = distinct.Select(click => click.ClickId).ToArray();
            var existingIds = await db.ClickEvents
                .AsNoTracking()
                .Where(click => eventIds.Contains(click.EventId))
                .Select(click => click.EventId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var existing = existingIds.ToHashSet();
            var pending = distinct.Where(click => !existing.Contains(click.ClickId)).ToArray();
            if (pending.Length == 0)
            {
                return;
            }

            var capped = new List<LinkClickCapped>();
            var actualCounts = new Dictionary<long, long>();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var group in pending.GroupBy(click => click.LinkId))
                {
                    var delta = (long)group.Count();
                    var lastClickedAt = group.Max(click => click.ClickedAtUtc);

                    await db.Links
                        .Where(link => link.Id == group.Key)
                        .ExecuteUpdateAsync(
                            setters => setters
                                .SetProperty(link => link.ClickCount, link => link.ClickCount + delta)
                                .SetProperty(
                                    link => link.LastAccessedAtUtc,
                                    link => link.LastAccessedAtUtc == null || link.LastAccessedAtUtc < lastClickedAt
                                        ? lastClickedAt
                                        : link.LastAccessedAtUtc),
                            cancellationToken)
                        .ConfigureAwait(false);

                    var snapshot = await db.Links
                        .AsNoTracking()
                        .Where(link => link.Id == group.Key)
                        .Select(link => new { link.ClickCount, link.MaxClicks })
                        .FirstOrDefaultAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (snapshot is { MaxClicks: not null })
                    {
                        actualCounts[group.Key] = snapshot.ClickCount;
                    }

                    // 必须在累加之后判定，且只有仍 Enabled 的一个事务能发出封顶事件。
                    var disabled = await db.Links
                        .Where(link => link.Id == group.Key
                                       && link.MaxClicks != null
                                       && link.ClickCount >= link.MaxClicks
                                       && link.Status == LinkStatus.Enabled)
                        .ExecuteUpdateAsync(
                            setters => setters
                                .SetProperty(link => link.Status, LinkStatus.Disabled)
                                .SetProperty(link => link.UpdatedAtUtc, lastClickedAt),
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (disabled > 0)
                    {
                        var sample = group.OrderByDescending(click => click.ClickedAtUtc).First();
                        capped.Add(new LinkClickCapped(
                            sample.WorkspaceId,
                            sample.LinkId,
                            sample.DomainId,
                            sample.Code,
                            snapshot?.ClickCount ?? delta,
                            snapshot?.MaxClicks ?? 0,
                            sample.Ip,
                            sample.Country,
                            lastClickedAt));
                    }

                    foreach (var routeGroup in group
                                 .Where(click => click.RouteId is not null)
                                 .GroupBy(click => click.RouteId!.Value))
                    {
                        var routeDelta = (long)routeGroup.Count();
                        await db.Routes
                            .Where(route => route.Id == routeGroup.Key)
                            .ExecuteUpdateAsync(
                                setters => setters.SetProperty(route => route.ClickCount, route => route.ClickCount + routeDelta),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                db.ClickEvents.AddRange(pending.Select(click => click.ToEntity()));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException exception) when (attempt == 0 && exception.IsUniqueConstraintViolation())
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                logger.LogWarning("点击批出现并发重复，重查 EventId 后重试：count={Count}", pending.Length);
                continue;
            }
            catch (Exception exception)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                await ReleasePendingAsync(clickQuota, pending).ConfigureAwait(false);
                ShortLinkDiagnostics.ClickPersistFailures.Add(1);
                logger.LogError(exception, "点击批落库失败，丢弃 {Count} 条并补偿点击配额", pending.Length);

                // 点击是可丢数据：不向总线重抛，避免一批坏消息堵住后续点击。
                return;
            }

            ShortLinkDiagnostics.ClicksEnqueued.Add(pending.Length);

            // 先消费预留，再做校准；后续重投即使失败也不能重复释放已落库的额度。
            foreach (var reservation in pending
                         .Where(click => click.QuotaReservationId.HasValue)
                         .Select(click => new { Id = click.QuotaReservationId!.Value, click.LinkId })
                         .Distinct())
            {
                await clickQuota.CompleteAsync(reservation.Id, reservation.LinkId, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            // 提交后把 short_links.click_count 校准到配额后端；只升不降，保留其他实例尚未落库的预留。
            foreach (var (linkId, actualCount) in actualCounts)
            {
                await clickQuota.SynchronizeAsync(linkId, actualCount, CancellationToken.None).ConfigureAwait(false);
            }

            foreach (var linkCapped in capped)
            {
                ShortLinkDiagnostics.LinksCapped.Add(1);

                try
                {
                    await bus.PublishAsync(linkCapped).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "封顶事件发布失败：短链 {Code}", linkCapped.Code);
                }

                try
                {
                    await cache.RemoveLinkAsync(linkCapped.DomainId, linkCapped.Code, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "封顶后缓存失效失败：短链 {Code}", linkCapped.Code);
                }
            }

            return;
        }
    }

    private static async Task ReleasePendingAsync(IClickQuota clickQuota, ClickRecorded[] pending)
    {
        foreach (var reservation in pending
                     .Where(click => click.QuotaReservationId.HasValue)
                     .Select(click => new { Id = click.QuotaReservationId!.Value, click.LinkId })
                     .Distinct())
        {
            await clickQuota.ReleaseAsync(reservation.Id, reservation.LinkId, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }
}

/// <summary>仅用于给静态 handler 提供稳定日志分类名。</summary>
public sealed class ClickRecordedBatchMarker;
