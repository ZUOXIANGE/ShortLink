using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Contracts.Analytics;
using ShortLink.Domain.Analytics;

namespace ShortLink.Infrastructure.Persistence.Repositories;

/// <summary>
/// 统计查询。所有聚合下推到 SQL：按天聚合走 <c>click_date</c> 索引，
/// 维度聚合限制 TopN，避免把点击明细拉进内存。
/// </summary>
public sealed class AnalyticsRepository(ShortLinkDbContext db) : IAnalyticsRepository
{
    private const int DimensionTopN = 8;

    public async Task<LinkClickAggregate> GetLinkStatsAsync(
        long linkId,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken)
    {
        var all = db.ClickEvents.AsNoTracking().Where(c => c.LinkId == linkId);
        var ranged = all.Where(c => c.ClickDate >= fromDate && c.ClickDate <= toDate);

        var totalClicks = await db.Links
            .AsNoTracking()
            .Where(link => link.Id == linkId)
            .Select(link => (long?)link.ClickCount)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0;
        var uniqueVisitors = await ranged
            .Where(c => !c.IsBot)
            .Select(c => c.VisitorHash)
            .Distinct()
            .LongCountAsync(cancellationToken)
            .ConfigureAwait(false);
        var todayClicks = await all
            .LongCountAsync(c => c.ClickDate == toDate, cancellationToken)
            .ConfigureAwait(false);

        var trend = await BuildTrendAsync(ranged, fromDate, toDate, cancellationToken).ConfigureAwait(false);

        var referers = await TopAsync(ranged.Where(c => c.Referer != null), c => c.Referer, cancellationToken)
            .ConfigureAwait(false);
        var devices = await TopAsync(ranged, c => c.Device, cancellationToken).ConfigureAwait(false);
        var browsers = await TopAsync(ranged, c => c.Browser, cancellationToken).ConfigureAwait(false);
        var systems = await TopAsync(ranged, c => c.OperatingSystem, cancellationToken).ConfigureAwait(false);
        var countries = await TopAsync(ranged, c => c.Country, cancellationToken).ConfigureAwait(false);

        return new LinkClickAggregate(
            totalClicks,
            uniqueVisitors,
            todayClicks,
            trend,
            NormalizeReferers(referers),
            devices,
            browsers,
            systems,
            countries);
    }

    public async Task<DashboardClickAggregate> GetDashboardAsync(
        long workspaceId,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken)
    {
        var all = db.ClickEvents.AsNoTracking().Where(c => c.WorkspaceId == workspaceId);
        var ranged = all.Where(c => c.ClickDate >= fromDate && c.ClickDate <= toDate);
        var yesterday = toDate.AddDays(-1);

        var totalClicks = await db.Links
            .AsNoTracking()
            .Where(link => link.WorkspaceId == workspaceId)
            .Select(link => link.ClickCount)
            .SumAsync(cancellationToken)
            .ConfigureAwait(false);
        var todayClicks = await all.LongCountAsync(c => c.ClickDate == toDate, cancellationToken).ConfigureAwait(false);
        var yesterdayClicks = await all
            .LongCountAsync(c => c.ClickDate == yesterday, cancellationToken)
            .ConfigureAwait(false);
        var uniqueToday = await all
            .Where(c => c.ClickDate == toDate && !c.IsBot)
            .Select(c => c.VisitorHash)
            .Distinct()
            .LongCountAsync(cancellationToken)
            .ConfigureAwait(false);

        var trend = await BuildTrendAsync(ranged, fromDate, toDate, cancellationToken).ConfigureAwait(false);

        var topRows = await db.Links
            .AsNoTracking()
            .Where(l => l.WorkspaceId == workspaceId)
            .OrderByDescending(l => l.ClickCount)
            .ThenByDescending(l => l.Id)
            .Take(5)
            .Select(l => new { l.Id, l.Code, l.Title, l.TargetUrl, l.ClickCount, l.DomainId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var hosts = await db.Domains
            .AsNoTracking()
            .ToDictionaryAsync(d => d.Id, d => d.Host, cancellationToken)
            .ConfigureAwait(false);

        var topLinks = topRows
            .Select(r => new TopLinkDto(
                r.Id,
                r.Code,
                hosts.GetValueOrDefault(r.DomainId, string.Empty),
                r.Title,
                r.TargetUrl,
                r.ClickCount))
            .ToList();

        return new DashboardClickAggregate(
            totalClicks,
            todayClicks,
            yesterdayClicks,
            uniqueToday,
            trend,
            topLinks);
    }

    public async Task<IReadOnlyList<ClickLogDto>> GetRecentClicksAsync(
        long linkId,
        int take,
        CancellationToken cancellationToken)
    {
        var rows = await db.ClickEvents
            .AsNoTracking()
            .Where(c => c.LinkId == linkId)
            .OrderByDescending(c => c.Id)
            .Take(take)
            .Select(c => new ClickLogDto(
                c.Id,
                c.Code,
                c.ClickedAtUtc,
                c.Ip,
                c.Referer,
                c.Device,
                c.Browser,
                c.OperatingSystem,
                c.Country,
                c.IsBot,
                c.RouteId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows;
    }

    public async Task<IReadOnlyList<VariantClicks>> GetVariantClicksAsync(
        long linkId,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken)
    {
        var rows = await db.ClickEvents
            .AsNoTracking()
            .Where(c => c.LinkId == linkId && c.ClickDate >= fromDate && c.ClickDate <= toDate)
            .GroupBy(c => c.RouteId)
            .Select(g => new { RouteId = g.Key, Clicks = g.LongCount() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(r => new VariantClicks(r.RouteId, r.Clicks)).ToList();
    }

    public IAsyncEnumerable<ClickLogDto> StreamClicksAsync(
        long linkId,
        DateOnly fromDate,
        CancellationToken cancellationToken)
        => db.ClickEvents
            .AsNoTracking()
            .Where(c => c.LinkId == linkId && c.ClickDate >= fromDate)
            .OrderByDescending(c => c.Id)
            .Take(100_000)
            .Select(c => new ClickLogDto(
                c.Id,
                c.Code,
                c.ClickedAtUtc,
                c.Ip,
                c.Referer,
                c.Device,
                c.Browser,
                c.OperatingSystem,
                c.Country,
                c.IsBot,
                c.RouteId))
            .AsAsyncEnumerable();

    /// <summary>按天聚合点击数，并按 (日期, 访客指纹) 去重计算独立访客；缺失日期补 0，保证趋势轴连续。</summary>
    private static async Task<IReadOnlyList<DailyClickDto>> BuildTrendAsync(
        IQueryable<ClickEvent> ranged,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken)
    {
        var dailyRows = await ranged
            .GroupBy(c => c.ClickDate)
            .Select(g => new
            {
                Date = g.Key,
                Clicks = g.LongCount(),
                UniqueVisitors = g.Where(c => !c.IsBot).Select(c => c.VisitorHash).Distinct().LongCount(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // SQL 只返回 O(天数) 聚合行，禁止把 O(访客数) 的日期/指纹对拉到内存去重。
        var clicksByDate = dailyRows.ToDictionary(x => x.Date, x => x.Clicks);
        var uniqueByDate = dailyRows.ToDictionary(x => x.Date, x => x.UniqueVisitors);

        var trend = new List<DailyClickDto>();
        for (var date = fromDate; date <= toDate; date = date.AddDays(1))
        {
            trend.Add(new DailyClickDto(
                date,
                clicksByDate.GetValueOrDefault(date),
                uniqueByDate.GetValueOrDefault(date)));
        }

        return trend;
    }

    private static async Task<IReadOnlyList<DimensionItemDto>> TopAsync(
        IQueryable<ClickEvent> source,
        Expression<Func<ClickEvent, string?>> selector,
        CancellationToken cancellationToken)
    {
        var rows = await source
            .GroupBy(selector)
            .Select(g => new { Name = g.Key, Clicks = g.LongCount() })
            .OrderByDescending(x => x.Clicks)
            .Take(DimensionTopN)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(r => new DimensionItemDto(string.IsNullOrWhiteSpace(r.Name) ? "未知" : r.Name!, r.Clicks))
            .ToList();
    }

    /// <summary>Referer 是完整 URL，视图按来源主机聚合更直观。</summary>
    private static List<DimensionItemDto> NormalizeReferers(IReadOnlyList<DimensionItemDto> referers)
        => referers
            .Select(r => new DimensionItemDto(NormalizeRefererHost(r.Name), r.Clicks))
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DimensionItemDto(g.Key, g.Sum(x => x.Clicks)))
            .OrderByDescending(x => x.Clicks)
            .Take(DimensionTopN)
            .ToList();

    private static string NormalizeRefererHost(string referer)
    {
        if (string.Equals(referer, "未知", StringComparison.Ordinal))
        {
            return referer;
        }

        return Uri.TryCreate(referer, UriKind.Absolute, out var uri)
            ? uri.Host
            : referer;
    }
}
