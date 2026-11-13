using System.ComponentModel;
using ModelContextProtocol.Server;
using ShortLink.Application.Analytics;
using ShortLink.Application.Links;
using ShortLink.Contracts;
using ShortLink.Contracts.Analytics;
using ShortLink.Domain.Users;
using ShortLink.Mcp.Models;

namespace ShortLink.Mcp.Tools;

/// <summary>
/// 统计与归因工具。全部只读。
/// <para>
/// 维度聚合只回 Top N（默认 5）：后台表格可以看全量长尾，
/// 但模型需要的是「主要来源是什么」，把 200 行 Referer 塞进上下文只会稀释判断。
/// </para>
/// </summary>
[McpServerToolType]
public sealed class AnalyticsTools(AnalyticsService analytics, LinkService links, McpToolRunner runner)
{
    private const int DefaultTop = 5;

    private const int MaxTop = 20;

    [McpServerTool(Name = "shortlink_stats", ReadOnly = true)]
    [Description(
        "查询单条短链的访问统计：总点击、独立访客、今日点击、转化数与转化价值，以及来源/设备/浏览器/国家的 Top N " +
        "和 A/B 分支效果。只读操作。需要 90 天以上趋势时改调 shortlink_dashboard 或用报表导出。")]
    public Task<LinkStats> GetStatsAsync(
        [Description("短链 ID；与 shortUrl 二选一")] long? linkId = null,
        [Description("完整短链地址，如 https://s.example.com/abc123；与 linkId 二选一")] string? shortUrl = null,
        [Description("统计窗口天数，1-90，默认 7")] int days = 7,
        [Description("是否返回逐日趋势点。默认 false（90 天趋势会占掉大量上下文）")] bool includeTrend = false,
        [Description("各维度返回前几名，1-20，默认 5")] int top = DefaultTop,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(
            "shortlink_stats",
            UserRole.Viewer,
            async ct =>
            {
                var id = await ResolveLinkIdAsync(linkId, shortUrl, ct).ConfigureAwait(false);
                var window = Math.Clamp(days, 1, 90);
                var take = Math.Clamp(top, 1, MaxTop);

                var stats = await analytics.GetLinkStatsAsync(id, window, ct).ConfigureAwait(false);

                return new LinkStats(
                    stats.LinkId,
                    stats.Code,
                    stats.ShortUrl,
                    stats.TargetUrl,
                    stats.Days,
                    stats.TotalClicks,
                    stats.UniqueVisitors,
                    stats.TodayClicks,
                    stats.Conversions,
                    stats.ConversionValue,
                    includeTrend ? stats.Trend.Select(DailyPoint.From).ToList() : null,
                    TopOf(stats.Referers, take),
                    TopOf(stats.Devices, take),
                    TopOf(stats.Browsers, take),
                    TopOf(stats.Countries, take),
                    stats.Variants.Select(VariantSlice.From).ToList());
            },
            cancellationToken);

    [McpServerTool(Name = "shortlink_dashboard", ReadOnly = true)]
    [Description(
        "查询当前工作区的整体概览：短链总数、启用/过期数、域名数、总点击、今日与昨日点击、" +
        "今日独立访客、总转化数与点击 Top 5 短链。只读操作。" +
        "用于回答「整体情况怎么样」，不含逐条明细。")]
    public Task<DashboardSummary> GetDashboardAsync(
        [Description("统计窗口天数，1-90，默认 7")] int days = 7,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(
            "shortlink_dashboard",
            UserRole.Viewer,
            async ct =>
            {
                var window = Math.Clamp(days, 1, 90);
                var overview = await analytics.GetDashboardAsync(window, ct).ConfigureAwait(false);

                return new DashboardSummary(
                    window,
                    overview.TotalLinks,
                    overview.EnabledLinks,
                    overview.ExpiredLinks,
                    overview.TotalDomains,
                    overview.TotalClicks,
                    overview.TodayClicks,
                    overview.YesterdayClicks,
                    overview.UniqueVisitorsToday,
                    overview.TotalConversions,
                    overview.TopLinks
                        .Select(link => new MetricSlice($"{link.Code} → {link.TargetUrl}", link.Clicks))
                        .ToList());
            },
            cancellationToken);

    [McpServerTool(Name = "shortlink_recent_clicks", ReadOnly = true)]
    [Description(
        "查询单条短链最近的点击明细（时间、国家、设备、浏览器、来源、是否机器人）。只读操作。" +
        "用于排查「这个链接是不是被爬了」这类问题；原始 IP 不返回，只有匿名访客指纹参与去重。")]
    public Task<IReadOnlyList<ClickRecord>> GetRecentClicksAsync(
        [Description("短链 ID；与 shortUrl 二选一")] long? linkId = null,
        [Description("完整短链地址；与 linkId 二选一")] string? shortUrl = null,
        [Description("返回条数，1-100，默认 20")] int limit = 20,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(
            "shortlink_recent_clicks",
            UserRole.Viewer,
            async ct =>
            {
                var id = await ResolveLinkIdAsync(linkId, shortUrl, ct).ConfigureAwait(false);
                var clicks = await analytics
                    .GetRecentClicksAsync(id, Math.Clamp(limit, 1, 100), ct)
                    .ConfigureAwait(false);

                return (IReadOnlyList<ClickRecord>)clicks.Select(ClickRecord.From).ToList();
            },
            cancellationToken);

    private static IReadOnlyList<MetricSlice> TopOf(IReadOnlyList<DimensionItemDto> items, int take)
        => items.Take(take).Select(MetricSlice.From).ToList();

    private async Task<long> ResolveLinkIdAsync(long? linkId, string? shortUrl, CancellationToken cancellationToken)
    {
        if (linkId is { } id)
        {
            return id;
        }

        if (!string.IsNullOrWhiteSpace(shortUrl))
        {
            var dto = await links.GetByShortUrlAsync(shortUrl, cancellationToken).ConfigureAwait(false);
            return dto.Id;
        }

        throw new BusinessException(BizCodes.InvalidArgument, "linkId 与 shortUrl 必须提供一个");
    }
}
