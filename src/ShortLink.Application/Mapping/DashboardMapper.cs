using Riok.Mapperly.Abstractions;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Contracts.Analytics;

namespace ShortLink.Application.Mapping;

/// <summary>仪表盘总览的映射：聚合计数、趋势与热门短链。</summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public static partial class DashboardMapper
{
    public static DashboardOverviewDto ToDashboardDto(
        LinkCounters counters,
        int totalDomains,
        DashboardClickAggregate aggregate,
        long totalConversions)
        => MapDashboard(
            counters,
            totalDomains,
            aggregate.TotalClicks,
            aggregate.TodayClicks,
            aggregate.YesterdayClicks,
            aggregate.UniqueVisitorsToday,
            totalConversions,
            aggregate.Trend,
            aggregate.TopLinks);

    [MapProperty(nameof(LinkCounters.Total), nameof(DashboardOverviewDto.TotalLinks))]
    [MapProperty(nameof(LinkCounters.Enabled), nameof(DashboardOverviewDto.EnabledLinks))]
    [MapProperty(nameof(LinkCounters.Expired), nameof(DashboardOverviewDto.ExpiredLinks))]
    private static partial DashboardOverviewDto MapDashboard(
        LinkCounters counters,
        long totalDomains,
        long totalClicks,
        long todayClicks,
        long yesterdayClicks,
        long uniqueVisitorsToday,
        long totalConversions,
        IReadOnlyList<DailyClickDto> trend,
        IReadOnlyList<TopLinkDto> topLinks);
}
