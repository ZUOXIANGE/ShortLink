using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Options;
using ShortLink.Infrastructure.Diagnostics;
using ShortLink.Infrastructure.Persistence;

namespace ShortLink.Infrastructure.Analytics;

/// <summary>
/// 点击明细保留策略。所有副本通过数据库租约互斥，最后成功日期也持久化：
/// 只有完整成功才记录日期，失败按有界退避重试，进程重启不会把失败误当成功。
/// </summary>
public sealed class ClickRetentionService(
    IServiceScopeFactory scopeFactory,
    IOptions<AnalyticsRetentionOptions> options,
    IClock clock,
    ILogger<ClickRetentionService> logger) : BackgroundService
{
    private const string LeaseName = "click-retention";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("点击明细保留策略未启用（Analytics:Retention:Enabled=false），不执行清理");
            return;
        }

        if (settings.ClickRetentionDays <= 0)
        {
            logger.LogWarning(
                "点击明细保留策略已启用但 ClickRetentionDays={Days} 非法，清理任务不会执行",
                settings.ClickRetentionDays);
            return;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "点击明细保留策略已启用：保留 {Days} 天，每天 {Hour}:00 UTC 执行",
                settings.ClickRetentionDays,
                settings.RunAtHourUtc);
        }

        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var nextAttemptUtc = DateTime.MinValue;
        var consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = clock.UtcNow;
            var runAtHour = Math.Clamp(settings.RunAtHourUtc, 0, 23);
            if (now >= nextAttemptUtc && now.Hour >= runAtHour)
            {
                try
                {
                    var result = await PurgeCoreAsync(now, settings, stoppingToken).ConfigureAwait(false);
                    consecutiveFailures = 0;
                    nextAttemptUtc = result.Status == RetentionRunStatus.Busy
                        ? now.Add(RetryDelays[0])
                        : now.Date.AddDays(1).AddHours(runAtHour);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    var retryDelay = RetryDelays[Math.Min(consecutiveFailures, RetryDelays.Length - 1)];
                    consecutiveFailures++;
                    nextAttemptUtc = now.Add(retryDelay);
                    logger.LogError(exception, "点击明细清理失败，将在 {Delay} 后重试（连续失败 {Failures} 次）",
                        retryDelay, consecutiveFailures);
                }
            }

            try
            {
                var delay = nextAttemptUtc > now && nextAttemptUtc - now < CheckInterval
                    ? nextAttemptUtc - now
                    : CheckInterval;
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>执行一次清理并返回本次实际删除行数；其他副本持锁或当天已成功时返回 0。</summary>
    public async Task<int> PurgeAsync(
        DateTime nowUtc,
        AnalyticsRetentionOptions settings,
        CancellationToken cancellationToken)
        => (await PurgeCoreAsync(nowUtc, settings, cancellationToken).ConfigureAwait(false)).DeletedRows;

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
        if (!await TryAcquireLeaseAsync(db, ownerId, clock.UtcNow, today, cancellationToken).ConfigureAwait(false))
        {
            var alreadyCompleted = await db.ClickRetentionLeases.AnyAsync(
                    lease => lease.Name == LeaseName && lease.LastCompletedDate != null && lease.LastCompletedDate >= today,
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
                var batchNowUtc = clock.UtcNow;
                var renewed = await db.ClickRetentionLeases
                    .Where(lease => lease.Name == LeaseName
                                    && lease.OwnerId == ownerId
                                    && lease.LeaseUntilUtc > batchNowUtc)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(lease => lease.LeaseUntilUtc, batchNowUtc.Add(LeaseDuration)),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (renewed != 1)
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

            // 完成标记与所有者匹配原子更新，只有当前租约持有者能宣布当天成功。
            var completedAtUtc = clock.UtcNow;
            var marked = await db.ClickRetentionLeases
                .Where(lease => lease.Name == LeaseName
                                && lease.OwnerId == ownerId
                                && lease.LeaseUntilUtc > completedAtUtc)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(lease => lease.LastCompletedDate, today)
                        .SetProperty(lease => lease.OwnerId, (string?)null)
                        .SetProperty(lease => lease.LeaseUntilUtc, completedAtUtc),
                    cancellationToken)
                .ConfigureAwait(false);
            if (marked != 1)
            {
                throw new InvalidOperationException("点击明细清理完成时租约已丢失，不能记录成功日期");
            }

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
                try
                {
                    await db.ClickRetentionLeases
                        .Where(lease => lease.Name == LeaseName && lease.OwnerId == ownerId)
                        .ExecuteUpdateAsync(
                            setters => setters
                                .SetProperty(lease => lease.OwnerId, (string?)null)
                                .SetProperty(lease => lease.LeaseUntilUtc, nowUtc),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "点击明细清理失败后释放租约失败，租约将在到期后自动释放");
                }
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

    private static async Task<bool> TryAcquireLeaseAsync(
        ShortLinkDbContext db,
        string ownerId,
        DateTime nowUtc,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var acquired = await db.ClickRetentionLeases
            .Where(lease => lease.Name == LeaseName
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

        if (await db.ClickRetentionLeases.AnyAsync(lease => lease.Name == LeaseName, cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }

        var lease = ClickRetentionLease.Create(LeaseName, ownerId, nowUtc.Add(LeaseDuration));
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
