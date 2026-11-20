using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Options;

namespace ShortLink.Infrastructure.Analytics;

/// <summary>Quartz 月度归档任务：把超过热窗口的点击明细搬入归档表。</summary>
public sealed class ClickArchiveJob(
    ClickRetentionService service,
    IOptions<AnalyticsArchiveOptions> options,
    IClock clock,
    ILogger<ClickArchiveJob> logger) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogDebug("点击明细归档未启用，跳过月度任务");
            return;
        }

        if (settings.ArchiveAfterDays <= 0)
        {
            logger.LogWarning(
                "点击明细归档已启用但 ArchiveAfterDays={Days} 非法，归档任务不会执行",
                settings.ArchiveAfterDays);
            return;
        }

        await service.ArchiveAsync(clock.UtcNow, settings, cancellationToken).ConfigureAwait(false);
    }
}
