using ShortLink.Contracts.Analytics;
using ShortLink.Contracts.Links;

namespace ShortLink.Mcp.Models;

/// <summary>
/// 工具返回给模型的短链视图。
/// <para>
/// 刻意不复用 <see cref="LinkDto"/>：那是给管理后台的完整视图（30 个字段，含 IP 名单原文、
/// 转化密钥有无、创建人 ID 等）。工具返回值会进模型上下文，字段越多越占 token、越容易干扰判断，
/// 这里只保留「判断与下一步动作」真正需要的字段。
/// </para>
/// </summary>
public sealed record LinkSummary(
    long Id,
    string Code,
    string ShortUrl,
    string TargetUrl,
    string? Title,
    string Status,
    long ClickCount,
    bool HasPassword,
    int RouteCount,
    bool AntiBlockEnabled,
    long? MaxClicks,
    DateTime? ExpiresAtUtc,
    DateTime? StartsAtUtc,
    DateTime CreatedAtUtc)
{
    public static LinkSummary From(LinkDto dto) => new(
        dto.Id,
        dto.Code,
        dto.ShortUrl,
        dto.TargetUrl,
        dto.Title,
        dto.Status,
        dto.ClickCount,
        dto.HasPassword,
        dto.RouteCount,
        dto.AntiBlockEnabled,
        dto.MaxClicks,
        dto.ExpiresAtUtc,
        dto.StartsAtUtc,
        dto.CreatedAtUtc);
}

/// <summary>分页结果。<c>Total</c> 用于让模型判断「是否还有下一页」，不必再猜。</summary>
public sealed record LinkPage(
    long Total,
    int Page,
    int PageSize,
    IReadOnlyList<LinkSummary> Items);

/// <summary>维度聚合切片（来源 / 设备 / 浏览器 / 国家）。</summary>
public sealed record MetricSlice(string Name, long Clicks)
{
    public static MetricSlice From(DimensionItemDto dto) => new(dto.Name, dto.Clicks);
}

/// <summary>A/B 或条件路由分支的效果。</summary>
public sealed record VariantSlice(
    string Name,
    long Clicks,
    long Conversions,
    double ConversionRate,
    double Share)
{
    public static VariantSlice From(VariantStatDto dto)
        => new(dto.Name, dto.Clicks, dto.Conversions, Math.Round(dto.ConversionRate, 4), Math.Round(dto.Share, 4));
}

/// <summary>单日趋势点。</summary>
public sealed record DailyPoint(DateOnly Date, long Clicks, long UniqueVisitors)
{
    public static DailyPoint From(DailyClickDto dto) => new(dto.Date, dto.Clicks, dto.UniqueVisitors);
}

/// <summary>
/// 单条短链的统计。<see cref="Trend"/> 仅在调用方显式要求时填充——
/// 90 天的趋势点会占掉大量上下文，默认不给。
/// </summary>
public sealed record LinkStats(
    long LinkId,
    string Code,
    string ShortUrl,
    string TargetUrl,
    int Days,
    long TotalClicks,
    long UniqueVisitors,
    long TodayClicks,
    long Conversions,
    decimal ConversionValue,
    IReadOnlyList<DailyPoint>? Trend,
    IReadOnlyList<MetricSlice> TopReferers,
    IReadOnlyList<MetricSlice> TopDevices,
    IReadOnlyList<MetricSlice> TopBrowsers,
    IReadOnlyList<MetricSlice> TopCountries,
    IReadOnlyList<VariantSlice> Variants);

/// <summary>工作区总览。</summary>
public sealed record DashboardSummary(
    int Days,
    long TotalLinks,
    long EnabledLinks,
    long ExpiredLinks,
    long TotalDomains,
    long TotalClicks,
    long TodayClicks,
    long YesterdayClicks,
    long UniqueVisitorsToday,
    long TotalConversions,
    IReadOnlyList<MetricSlice> TopLinks);

/// <summary>一条点击明细。</summary>
public sealed record ClickRecord(
    string Code,
    DateTime ClickedAtUtc,
    string? Country,
    string? Province,
    string? Device,
    string? Browser,
    string? OperatingSystem,
    string? Referer,
    bool IsBot)
{
    public static ClickRecord From(ClickLogDto dto) => new(
        dto.Code,
        dto.ClickedAtUtc,
        dto.Country,
        null,
        dto.Device,
        dto.Browser,
        dto.OperatingSystem,
        // Referer 是外部可控文本，会进模型上下文；截断并交给模型按数据处理。
        Truncate(dto.Referer, 200),
        dto.IsBot);

    private static string? Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}

/// <summary>域名选项。创建短链时若需要指定域名，模型要先知道有哪些可选。</summary>
public sealed record DomainOption(long Id, string Host, string Scheme, bool IsDefault, bool IsEnabled, long LinkCount);
