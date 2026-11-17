using Riok.Mapperly.Abstractions;
using ShortLink.Contracts.Analytics;
using ShortLink.Contracts.Domains;
using ShortLink.Contracts.Links;
using ShortLink.Mcp.Models;

namespace ShortLink.Mcp.Mapping;

/// <summary>
/// 工具返回模型的投影。与 HTTP 后台契约分离：只保留模型做判断与下一步动作需要的字段，
/// 字段对应关系由 Mapperly 在编译期生成。
/// </summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public static partial class McpMapper
{
    public static partial LinkSummary ToSummary(LinkDto dto);

    public static partial MetricSlice ToMetricSlice(DimensionItemDto dto);

    public static MetricSlice ToMetricSlice(TopLinkDto dto)
        => new($"{dto.Code} → {dto.TargetUrl}", dto.Clicks);

    public static partial DailyPoint ToDailyPoint(DailyClickDto dto);

    [MapProperty(nameof(VariantStatDto.ConversionRate), nameof(VariantSlice.ConversionRate), Use = nameof(RoundRate))]
    [MapProperty(nameof(VariantStatDto.Share), nameof(VariantSlice.Share), Use = nameof(RoundRate))]
    public static partial VariantSlice ToVariantSlice(VariantStatDto dto);

    [MapProperty(nameof(ClickLogDto.Province), nameof(ClickRecord.Province), Use = nameof(HideProvince))]
    [MapProperty(nameof(ClickLogDto.Referer), nameof(ClickRecord.Referer), Use = nameof(TruncateReferer))]
    public static partial ClickRecord ToClickRecord(ClickLogDto dto);

    public static partial DomainOption ToDomainOption(DomainDto dto);

    public static partial LinkStats ToStats(
        LinkStatsDto stats,
        IReadOnlyList<DailyPoint>? trend,
        IReadOnlyList<MetricSlice> topReferers,
        IReadOnlyList<MetricSlice> topDevices,
        IReadOnlyList<MetricSlice> topBrowsers,
        IReadOnlyList<MetricSlice> topCountries);

    public static partial DashboardSummary ToDashboard(
        DashboardOverviewDto overview,
        int days,
        IReadOnlyList<MetricSlice> topLinks);

    [UserMapping(Default = false)]
    private static double RoundRate(double value) => Math.Round(value, 4);

    [UserMapping(Default = false)]
    private static string? HideProvince(string? _) => null;

    [UserMapping(Default = false)]
    private static string? TruncateReferer(string? value)
        => string.IsNullOrEmpty(value) || value.Length <= 200 ? value : value[..200];
}
