using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Options;
using ShortLink.Infrastructure.Diagnostics;
using ShortLink.Infrastructure.Persistence;

namespace ShortLink.Infrastructure.Analytics;

/// <summary>
/// 点击明细的生命周期：按月把超过热窗口的明细搬入归档表，按天清理剩余过期明细。
/// 调度由 Quartz 负责，这里只暴露「执行一次」的可测方法；所有副本通过数据库租约互斥，
/// 最后成功日期也持久化，进程重启不会把失败误当成功。
/// </summary>
public sealed class ClickRetentionService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<ClickRetentionService> logger)
{
    private const string RetentionLeaseName = "click-retention";
    private const string ArchiveLeaseName = "click-archive";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);

    /// <summary>执行一次清理并返回本次实际删除行数；其他副本持锁或当天已成功时返回 0。</summary>
    public async Task<int> PurgeAsync(
        DateTime nowUtc,
        AnalyticsRetentionOptions settings,
        CancellationToken cancellationToken)
        => (await PurgeCoreAsync(nowUtc, settings, cancellationToken).ConfigureAwait(false)).DeletedRows;

    /// <summary>执行一次归档并返回本次搬运行数；未启用时直接跳过。</summary>
    public async Task<int> ArchiveAsync(
        DateTime nowUtc,
        AnalyticsArchiveOptions settings,
        CancellationToken cancellationToken)
    {
        if (!settings.Enabled)
        {
            logger.LogDebug("点击明细归档未启用，跳过本次调度");
            return 0;
        }

        return await ArchiveCoreAsync(nowUtc, settings, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RetentionPurgeResult> PurgeCoreAsync(
        DateTime nowUtc,
        AnalyticsRetentionOptions settings,
        CancellationToken cancellationToken)
    {
        var days = Math.Max(settings.ClickRetentionDays, 1);
        var cutoff = nowUtc.Date.AddDays(-days);
        var batchSize = Math.Clamp(settings.BatchSize, 50, 2000);
        var maxRows = Math.Max(settings.MaxRowsPerRun, batchSize);
        var today = DateOnly.FromDateTime(nowUtc);
        var ownerId = Guid.NewGuid().ToString("N");

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShortLinkDbContext>();
        // nowUtc 仅决定清理水位和完成日期；租约必须使用真实当前时钟，不能把历史回放时间当租约起点。
        if (!await TryAcquireLeaseAsync(db, RetentionLeaseName, ownerId, clock.UtcNow, today, cancellationToken)
                .ConfigureAwait(false))
        {
            var alreadyCompleted = await db.ClickRetentionLeases.AnyAsync(
                    lease => lease.Name == RetentionLeaseName
                             && lease.LastCompletedDate != null
                             && lease.LastCompletedDate >= today,
                    cancellationToken)
                .ConfigureAwait(false);
            logger.LogDebug("点击明细清理跳过：当天已成功或其他副本持有有效租约");
            return new RetentionPurgeResult(
                0,
                alreadyCompleted ? RetentionRunStatus.AlreadyCompleted : RetentionRunStatus.Busy);
        }

        var completed = false;
        try
        {
            var total = 0;
            while (total < maxRows && !cancellationToken.IsCancellationRequested)
            {
                // 每批之前续约；失去租约就停止，避免过期持有者继续执行后续批次。
                if (!await RenewLeaseAsync(db, RetentionLeaseName, ownerId, clock.UtcNow, cancellationToken)
                        .ConfigureAwait(false))
                {
                    throw new InvalidOperationException("点击明细清理租约已丢失");
                }

                var ids = await db.ClickEvents
                    .Where(c => c.ClickedAtUtc < cutoff)
                    .OrderBy(c => c.ClickedAtUtc)
                    .ThenBy(c => c.Id)
                    .Select(c => c.Id)
                    .Take(batchSize)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (ids.Count == 0)
                {
                    break;
                }

                var deleted = await db.ClickEvents
                    .Where(c => ids.Contains(c.Id))
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);

                total += deleted;
                ShortLinkDiagnostics.ClicksPurged.Add(deleted);

                if (ids.Count < batchSize)
                {
                    break;
                }

                if (settings.BatchDelayMilliseconds > 0)
                {
                    await Task.Delay(settings.BatchDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                }
            }

            // 停机时不能把「执行到一半」记成当天成功，否则本日剩余数据不会再被处理。
            cancellationToken.ThrowIfCancellationRequested();

            // 完成标记与所有者匹配原子更新，只有当前租约持有者能宣布当天成功。
            await MarkCompletedAsync(db, RetentionLeaseName, ownerId, today, clock.UtcNow, cancellationToken)
                .ConfigureAwait(false);

            completed = true;
            if (total > 0)
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("点击明细清理完成：删除 {Rows} 行（早于 {Cutoff:yyyy-MM-dd}）", total, cutoff);
                }
            }
            else if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("点击明细清理完成：没有需要清理的数据（早于 {Cutoff:yyyy-MM-dd}）", cutoff);
            }

            return new RetentionPurgeResult(total, RetentionRunStatus.Completed);
        }
        finally
        {
            if (!completed)
            {
                await ReleaseLeaseAsync(db, RetentionLeaseName, ownerId, nowUtc).ConfigureAwait(false);
            }
        }
    }

    private async Task<int> ArchiveCoreAsync(
        DateTime nowUtc,
        AnalyticsArchiveOptions settings,
        CancellationToken cancellationToken)
    {
        var days = Math.Max(settings.ArchiveAfterDays, 1);
        var cutoff = nowUtc.Date.AddDays(-days);
        var batchSize = Math.Clamp(settings.BatchSize, 50, 2000);
        var maxRows = Math.Max(settings.MaxRowsPerRun, batchSize);
        var today = DateOnly.FromDateTime(nowUtc);
        var ownerId = Guid.NewGuid().ToString("N");

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShortLinkDbContext>();
        if (!await TryAcquireLeaseAsync(db, ArchiveLeaseName, ownerId, clock.UtcNow, today, cancellationToken)
                .ConfigureAwait(false))
        {
            logger.LogDebug("点击明细归档跳过：当天已成功或其他副本持有有效租约");
            return 0;
        }

        var completed = false;
        try
        {
            var total = 0;
            while (total < maxRows && !cancellationToken.IsCancellationRequested)
            {
                if (!await RenewLeaseAsync(db, ArchiveLeaseName, ownerId, clock.UtcNow, cancellationToken)
                        .ConfigureAwait(false))
                {
                    throw new InvalidOperationException("点击明细归档租约已丢失");
                }

                var clicks = await db.ClickEvents
                    .AsNoTracking()
                    .Where(c => c.ClickedAtUtc < cutoff)
                    .OrderBy(c => c.ClickedAtUtc)
                    .ThenBy(c => c.Id)
                    .Take(batchSize)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (clicks.Count == 0)
                {
                    break;
                }

                var ids = clicks.Select(c => c.Id).ToList();
                var archivedAtUtc = clock.UtcNow;

                int deleted;
                // 归档插入与主表删除必须同批提交：否则「已归档未删除」会让下个月重复搬运。
                await using (var transaction = await db.Database
                                 .BeginTransactionAsync(cancellationToken)
                                 .ConfigureAwait(false))
                {
                    db.ClickEventArchives.AddRange(clicks.Select(c => ClickEventArchive.From(c, archivedAtUtc)));
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    deleted = await db.ClickEvents
                        .Where(c => ids.Contains(c.Id))
                        .ExecuteDeleteAsync(cancellationToken)
                        .ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }

                // 归档实体不再需要跟踪，清掉以免单次运行搬得多时内存线性增长。
                db.ChangeTracker.Clear();

                total += deleted;
                ShortLinkDiagnostics.ClicksArchived.Add(deleted);

                if (clicks.Count < batchSize)
                {
                    break;
                }

                if (settings.BatchDelayMilliseconds > 0)
                {
                    await Task.Delay(settings.BatchDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                }
            }

            // 停机时释放租约并让下次调度继续；不能把半次搬运记成当月成功。
            cancellationToken.ThrowIfCancellationRequested();

            await MarkCompletedAsync(db, ArchiveLeaseName, ownerId, today, clock.UtcNow, cancellationToken)
                .ConfigureAwait(false);

            completed = true;
            if (total >= maxRows)
            {
                logger.LogWarning(
                    "点击明细归档达到单次上限 {MaxRows} 行，剩余数据将在下一个归档周期继续搬运",
                    maxRows);
            }

            if (total > 0)
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("点击明细归档完成：搬运 {Rows} 行（早于 {Cutoff:yyyy-MM-dd}）", total, cutoff);
                }
            }
            else if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("点击明细归档完成：没有需要归档的数据（早于 {Cutoff:yyyy-MM-dd}）", cutoff);
            }

            return total;
        }
        finally
        {
            if (!completed)
            {
                await ReleaseLeaseAsync(db, ArchiveLeaseName, ownerId, nowUtc).ConfigureAwait(false);
            }
        }
    }

    private enum RetentionRunStatus
    {
        Completed,
        AlreadyCompleted,
        Busy,
    }

    private sealed record RetentionPurgeResult(int DeletedRows, RetentionRunStatus Status);

    private static async Task<bool> RenewLeaseAsync(
        ShortLinkDbContext db,
        string leaseName,
        string ownerId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var renewed = await db.ClickRetentionLeases
            .Where(lease => lease.Name == leaseName
                            && lease.OwnerId == ownerId
                            && lease.LeaseUntilUtc > nowUtc)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(lease => lease.LeaseUntilUtc, nowUtc.Add(LeaseDuration)),
                cancellationToken)
            .ConfigureAwait(false);
        return renewed == 1;
    }

    private static async Task MarkCompletedAsync(
        ShortLinkDbContext db,
        string leaseName,
        string ownerId,
        DateOnly today,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var marked = await db.ClickRetentionLeases
            .Where(lease => lease.Name == leaseName
                            && lease.OwnerId == ownerId
                            && lease.LeaseUntilUtc > nowUtc)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(lease => lease.LastCompletedDate, today)
                    .SetProperty(lease => lease.OwnerId, (string?)null)
                    .SetProperty(lease => lease.LeaseUntilUtc, nowUtc),
                cancellationToken)
            .ConfigureAwait(false);
        if (marked != 1)
        {
            throw new InvalidOperationException($"{leaseName} 租约已丢失，不能记录成功日期");
        }
    }

    private async Task ReleaseLeaseAsync(
        ShortLinkDbContext db,
        string leaseName,
        string ownerId,
        DateTime nowUtc)
    {
        try
        {
            await db.ClickRetentionLeases
                .Where(lease => lease.Name == leaseName && lease.OwnerId == ownerId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(lease => lease.OwnerId, (string?)null)
                        .SetProperty(lease => lease.LeaseUntilUtc, nowUtc),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "{Lease} 失败后释放租约失败，租约将在到期后自动释放", leaseName);
        }
    }

    private static async Task<bool> TryAcquireLeaseAsync(
        ShortLinkDbContext db,
        string leaseName,
        string ownerId,
        DateTime nowUtc,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var acquired = await db.ClickRetentionLeases
            .Where(lease => lease.Name == leaseName
                            && (lease.LastCompletedDate == null || lease.LastCompletedDate < today)
                            && (lease.LeaseUntilUtc <= nowUtc || lease.OwnerId == ownerId))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(lease => lease.OwnerId, ownerId)
                    .SetProperty(lease => lease.LeaseUntilUtc, nowUtc.Add(LeaseDuration)),
                cancellationToken)
            .ConfigureAwait(false);
        if (acquired == 1)
        {
            return true;
        }

        if (await db.ClickRetentionLeases.AnyAsync(lease => lease.Name == leaseName, cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }

        var lease = ClickRetentionLease.Create(leaseName, ownerId, nowUtc.Add(LeaseDuration));
        db.ClickRetentionLeases.Add(lease);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException exception) when (exception.IsUniqueConstraintViolation())
        {
            // 多副本同时首次启动时只有一个能插入单行租约，其余副本下一周期再竞争。
            db.Entry(lease).State = EntityState.Detached;
            return false;
        }
    }
}
