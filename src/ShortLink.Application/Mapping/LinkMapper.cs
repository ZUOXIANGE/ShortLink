using System.Diagnostics.CodeAnalysis;
using Riok.Mapperly.Abstractions;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Contracts.Analytics;
using ShortLink.Contracts.Links;
using ShortLink.Domain.Domains;
using ShortLink.Domain.Links;

namespace ShortLink.Application.Mapping;

/// <summary>
/// 短链及其附属对象到契约 DTO 的映射：短链本体、分支、转化密钥与单链统计。
/// 所有时间字段以 UTC 输出，客户端负责本地化展示。
/// </summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public static partial class LinkMapper
{
    public static LinkDto ToDto(
        this Link link,
        ShortDomain? domain,
        int routeCount = 0,
        string? createdByName = null)
        => ToDtoCore(
            link,
            domain?.Host ?? string.Empty,
            domain is null ? $"/{link.Code}" : domain.BuildShortUrl(link.Code),
            routeCount,
            createdByName);

    [MapProperty(nameof(Link.Status), nameof(LinkDto.Status), Use = nameof(StatusText))]
    [MapProperty(nameof(Link.BotPolicy), nameof(LinkDto.BotPolicy), Use = nameof(BotPolicyText))]
    private static partial LinkDto ToDtoCore(
        this Link link,
        string domainHost,
        string shortUrl,
        int routeCount,
        string? createdByName);

    [MapPropertyFromSource(nameof(LinkRouteDto.Match), Use = nameof(MatchText))]
    [MapPropertyFromSource(nameof(LinkRouteDto.Conditions), Use = nameof(ConditionDtos))]
    public static partial LinkRouteDto ToDto(this LinkRoute route);

    public static ConversionKeyDto ToConversionKeyDto(this Link link, string secret)
        => MapConversionKey(link, secret);

    [MapProperty(nameof(Link.Id), nameof(ConversionKeyDto.LinkId))]
    [MapProperty(nameof(Link.Code), nameof(ConversionKeyDto.LinkCode))]
    [MapValue(nameof(ConversionKeyDto.Algorithm), "HMAC-SHA256")]
    [MapValue(nameof(ConversionKeyDto.HeaderName), "X-Signature")]
    [MapValue(nameof(ConversionKeyDto.SignatureFormat), "sha256={hex}")]
    [MapValue(nameof(ConversionKeyDto.SignedPayloadFormat), "{X-Timestamp}.{rawJsonBody}")]
    private static partial ConversionKeyDto MapConversionKey(Link link, string secret);

    public static LinkStatsDto ToLinkStatsDto(
        Link link,
        ShortDomain? domain,
        LinkClickAggregate aggregate,
        ConversionAggregate conversion,
        IReadOnlyList<VariantStatDto> variants,
        int days)
        => MapLinkStats(
            link,
            domain?.Host ?? string.Empty,
            domain is null ? $"/{link.Code}" : domain.BuildShortUrl(link.Code),
            aggregate.TotalClicks,
            aggregate.UniqueVisitors,
            aggregate.TodayClicks,
            days,
            conversion.Total,
            conversion.Value,
            aggregate.Trend,
            aggregate.Referers,
            aggregate.Devices,
            aggregate.Browsers,
            aggregate.OperatingSystems,
            aggregate.Countries,
            aggregate.Provinces,
            aggregate.UnknownCountryClicks,
            variants);

    [MapProperty(nameof(Link.Id), nameof(LinkStatsDto.LinkId))]
    private static partial LinkStatsDto MapLinkStats(
        Link link,
        string domainHost,
        string shortUrl,
        long totalClicks,
        long uniqueVisitors,
        long todayClicks,
        int days,
        long conversions,
        decimal conversionValue,
        IReadOnlyList<DailyClickDto> trend,
        IReadOnlyList<DimensionItemDto> referers,
        IReadOnlyList<DimensionItemDto> devices,
        IReadOnlyList<DimensionItemDto> browsers,
        IReadOnlyList<DimensionItemDto> operatingSystems,
        IReadOnlyList<DimensionItemDto> countries,
        IReadOnlyList<DimensionItemDto> provinces,
        long unknownCountryClicks,
        IReadOnlyList<VariantStatDto> variants);

    [UserMapping(Default = false)]
    private static string StatusText(LinkStatus status)
        => status == LinkStatus.Enabled ? "enabled" : "disabled";

    [UserMapping(Default = false)]
    private static string BotPolicyText(LinkBotPolicy policy) => policy switch
    {
        LinkBotPolicy.Block => BotPolicies.Block,
        LinkBotPolicy.IgnoreCount => BotPolicies.IgnoreCount,
        _ => BotPolicies.Allow,
    };

    [UserMapping(Default = false)]
    private static string MatchText(LinkRoute route)
        => route.GetConditions().Match == RouteMatchMode.Any ? "any" : "all";

    [UserMapping(Default = false)]
    [SuppressMessage("Performance", "CA1859", Justification = "Mapperly 的 Use 要求返回类型与目标成员完全一致。")]
    private static IReadOnlyList<RouteConditionDto> ConditionDtos(LinkRoute route)
        => route.GetConditions().Items.Select(MapCondition).ToList();

    private static partial RouteConditionDto MapCondition(RouteCondition condition);
}
