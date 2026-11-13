using ShortLink.Contracts.Analytics;

namespace ShortLink.Application.Abstractions.Persistence;

/// <summary>按分支聚合的点击量。<c>RouteId</c> 为空表示走兜底地址的点击。</summary>
public sealed record VariantClicks(long? RouteId, long Clicks);

/// <summary>单链统计聚合结果。</summary>
public sealed record LinkClickAggregate(
    long TotalClicks,
    long UniqueVisitors,
    long TodayClicks,
    IReadOnlyList<DailyClickDto> Trend,
    IReadOnlyList<DimensionItemDto> Referers,
    IReadOnlyList<DimensionItemDto> Devices,
    IReadOnlyList<DimensionItemDto> Browsers,
    IReadOnlyList<DimensionItemDto> OperatingSystems,
    IReadOnlyList<DimensionItemDto> Countries);

/// <summary>控制台聚合结果。</summary>
public sealed record DashboardClickAggregate(
    long TotalClicks,
    long TodayClicks,
    long YesterdayClicks,
    long UniqueVisitorsToday,
    IReadOnlyList<DailyClickDto> Trend,
    IReadOnlyList<TopLinkDto> TopLinks);

/// <summary>统计查询端口。聚合下推到数据库，避免把明细拉到内存。</summary>
public interface IAnalyticsRepository
{
    Task<LinkClickAggregate> GetLinkStatsAsync(
        long linkId,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken);

    Task<DashboardClickAggregate> GetDashboardAsync(
        long workspaceId,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ClickLogDto>> GetRecentClicksAsync(
        long linkId,
        int take,
        CancellationToken cancellationToken);

    /// <summary>按分支（A/B 变体）统计点击量。</summary>
    Task<IReadOnlyList<VariantClicks>> GetVariantClicksAsync(
        long linkId,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken);

    /// <summary>点击明细流式导出。</summary>
    IAsyncEnumerable<ClickLogDto> StreamClicksAsync(
        long linkId,
        DateOnly fromDate,
        CancellationToken cancellationToken);
}
