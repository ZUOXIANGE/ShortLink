using ShortLink.Application.Abstractions;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Application.Workspaces;
using ShortLink.Contracts;
using ShortLink.Contracts.Analytics;
using ShortLink.Domain.Links;

namespace ShortLink.Application.Analytics;

/// <summary>访问统计用例。聚合全部下推到数据库，接口只做口径归一。</summary>
public sealed class AnalyticsService(
    IShortLinkRepository links,
    IDomainRepository domains,
    IRouteRepository routes,
    IAnalyticsRepository analytics,
    IConversionRepository conversions,
    IClock clock,
    IStatisticsCalendar calendar,
    WorkspaceContext workspace)
{
    public const int DefaultDays = 30;

    public const int MaxDays = 365;

    public async Task<LinkStatsDto> GetLinkStatsAsync(long linkId, int days, CancellationToken cancellationToken)
    {
        days = NormalizeDays(days);

        var link = await LoadVisibleAsync(linkId, cancellationToken).ConfigureAwait(false);
        var domain = await domains.FindByIdAsync(link.DomainId, cancellationToken).ConfigureAwait(false);

        var (from, today) = Range(days);
        var aggregate = await analytics.GetLinkStatsAsync(linkId, from, today, cancellationToken).ConfigureAwait(false);
        var variantClicks = await analytics
            .GetVariantClicksAsync(linkId, from, today, cancellationToken)
            .ConfigureAwait(false);
        var conversionAggregate = await conversions
            .GetLinkAggregateAsync(
                linkId,
                calendar.ToUtcStart(from),
                calendar.ToUtcStart(today.AddDays(1)),
                cancellationToken)
            .ConfigureAwait(false);

        var variants = await BuildVariantsAsync(linkId, variantClicks, conversionAggregate, cancellationToken)
            .ConfigureAwait(false);

        return new LinkStatsDto(
            link.Id,
            link.Code,
            domain?.Host ?? string.Empty,
            domain?.BuildShortUrl(link.Code) ?? $"/{link.Code}",
            link.TargetUrl,
            aggregate.TotalClicks,
            aggregate.UniqueVisitors,
            aggregate.TodayClicks,
            days,
            conversionAggregate.Total,
            conversionAggregate.Value,
            aggregate.Trend,
            aggregate.Referers,
            aggregate.Devices,
            aggregate.Browsers,
            aggregate.OperatingSystems,
            aggregate.Countries,
            variants);
    }

    public async Task<DashboardOverviewDto> GetDashboardAsync(int days, CancellationToken cancellationToken)
    {
        days = NormalizeDays(days);

        var workspaceId = await workspace.GetIdAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        var counters = await links.GetCountersAsync(workspaceId, now, cancellationToken).ConfigureAwait(false);
        var domainList = await domains.ListAsync(cancellationToken).ConfigureAwait(false);

        var (from, today) = Range(days);
        var aggregate = await analytics
            .GetDashboardAsync(workspaceId, from, today, cancellationToken)
            .ConfigureAwait(false);

        var (conversionCount, _) = await conversions
            .GetWorkspaceTotalsAsync(workspaceId, fromUtc: null, cancellationToken)
            .ConfigureAwait(false);

        return new DashboardOverviewDto(
            counters.Total,
            counters.Enabled,
            counters.Expired,
            domainList.Count,
            aggregate.TotalClicks,
            aggregate.TodayClicks,
            aggregate.YesterdayClicks,
            aggregate.UniqueVisitorsToday,
            conversionCount,
            aggregate.Trend,
            aggregate.TopLinks);
    }

    public async Task<IReadOnlyList<ClickLogDto>> GetRecentClicksAsync(
        long linkId,
        int take,
        CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 100);
        var link = await LoadVisibleAsync(linkId, cancellationToken).ConfigureAwait(false);

        return await analytics.GetRecentClicksAsync(link.Id, take, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 按当前工作区校验可见性。统计接口是按 ID 直接取数，如果不校验工作区，
    /// 换个 ID 就能读到别的工作区的访问数据。
    /// </summary>
    private async Task<Link> LoadVisibleAsync(long linkId, CancellationToken cancellationToken)
    {
        var workspaceId = await workspace.GetIdAsync(cancellationToken).ConfigureAwait(false);
        var link = await links.FindByIdAsync(linkId, cancellationToken).ConfigureAwait(false)
                   ?? throw new BusinessException(BizCodes.LinkNotFound);

        return link.WorkspaceId == workspaceId
            ? link
            : throw new BusinessException(BizCodes.LinkNotFound);
    }

    /// <summary>
    /// 把「分支点击」与「分支转化」对齐成一张表，未命中的分支补 0。
    /// <para>
    /// 兜底点击的 <c>RouteId</c> 为 null，因此不能直接用 <c>ToDictionary(v => v.RouteId)</c>：
    /// .NET 的字典拒绝 null 键（即使键类型是 <c>long?</c>），会抛 ArgumentNullException。
    /// 这里统一把 null 折叠成哨兵值 0（自增主键从 1 起，不会冲突）。
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<VariantStatDto>> BuildVariantsAsync(
        long linkId,
        IReadOnlyList<VariantClicks> variantClicks,
        ConversionAggregate conversionAggregate,
        CancellationToken cancellationToken)
    {
        var routeList = await routes.ListByLinkAsync(linkId, cancellationToken).ConfigureAwait(false);
        if (routeList.Count == 0 && variantClicks.Count == 0 && conversionAggregate.ByRoute.Count == 0)
        {
            return [];
        }

        const long fallbackKey = 0L;

        var names = routeList.ToDictionary(r => r.Id, r => r.Name);
        var clicks = variantClicks
            .GroupBy(v => v.RouteId ?? fallbackKey)
            .ToDictionary(g => g.Key, g => g.Sum(v => v.Clicks));
        var convertible = conversionAggregate.ByRoute
            .GroupBy(v => v.RouteId ?? fallbackKey)
            .ToDictionary(g => g.Key, g => (Count: g.Sum(v => v.Count), Value: g.Sum(v => v.Value)));

        var total = clicks.Values.Sum();

        var keys = names.Keys
            .Concat(clicks.Keys)
            .Concat(convertible.Keys)
            .Distinct()
            .ToList();

        return keys
            .Select(id =>
            {
                var clickCount = clicks.GetValueOrDefault(id);
                var conversion = convertible.TryGetValue(id, out var value) ? value : default;
                return new VariantStatDto(
                    id == fallbackKey ? null : id,
                    id == fallbackKey ? "兜底地址" : names.GetValueOrDefault(id, $"分支 #{id}"),
                    clickCount,
                    conversion.Count,
                    conversion.Value,
                    total == 0 ? 0 : Math.Round(clickCount * 100d / total, 2),
                    clickCount == 0 ? 0 : Math.Round(conversion.Count * 100d / clickCount, 2));
            })
            .OrderByDescending(v => v.Clicks)
            .ToList();
    }

    /// <summary>
    /// 统计区间。日界必须走 <see cref="IStatisticsCalendar"/> 而不是直接取 UTC 日期，
    /// 否则「今日」与写入侧的 <c>click_date</c> 会用两套口径，同一份数据被切成两段。
    /// </summary>
    private (DateOnly From, DateOnly Today) Range(int days)
    {
        var today = calendar.Today;
        return (today.AddDays(-(days - 1)), today);
    }

    private static int NormalizeDays(int days) => days switch
    {
        < 1 => DefaultDays,
        > MaxDays => MaxDays,
        _ => days,
    };
}
