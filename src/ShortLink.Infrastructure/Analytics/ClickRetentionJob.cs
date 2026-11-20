using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Options;

namespace ShortLink.Infrastructure.Analytics;

/// <summary>
/// Quartz 每日保留清理任务。归档开启时跳过：过期明细由月度归档负责搬运，
/// 这里继续删除会让尚未归档的数据直接消失。
/// </summary>
public sealed class ClickRetentionJob(
    ClickRetentionService service,
    IOptions<AnalyticsRetentionOptions> options,
    IOptions<AnalyticsArchiveOptions> archiveOptions,
    IClock clock,
    ILogger<ClickRetentionJob> logger) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (archiveOptions.Value.Enabled)
        {
            logger.LogDebug("归档已启用，跳过每日清理以免删除尚未归档的明细");
            return;
        }

        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogDebug("点击明细清理未启用，跳过每日任务");
            return;
        }

        if (settings.ClickRetentionDays <= 0)
        {
            logger.LogWarning(
                "点击明细清理已启用但 ClickRetentionDays={Days} 非法，清理任务不会执行",
                settings.ClickRetentionDays);
            return;
        }

        await service.PurgeAsync(clock.UtcNow, settings, cancellationToken).ConfigureAwait(false);
    }
}
