using System.Diagnostics.CodeAnalysis;
using Riok.Mapperly.Abstractions;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Contracts.Analytics;
using ShortLink.Contracts.Auth;
using ShortLink.Contracts.Domains;
using ShortLink.Contracts.Links;
using ShortLink.Contracts.Security;
using ShortLink.Contracts.Users;
using ShortLink.Domain.Domains;
using ShortLink.Domain.Links;
using ShortLink.Domain.Security;
using ShortLink.Domain.Users;

namespace ShortLink.Application.Mapping;

/// <summary>
/// 领域对象到契约 DTO 的映射。全部时间字段以 UTC 输出，客户端负责本地化展示。
/// <para>
/// 映射由 Mapperly 在编译期生成：字段对应关系在声明上可见，新增字段时缺映射会直接编译告警，
/// 避免运行期反射或手写赋值漏字段。
/// </para>
/// </summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public static partial class DtoMapper
{
    public static UserDto ToDto(
        this User user,
        long workspaceId,
        string workspaceName,
        UserRole role,
        bool canManageGlobalDomains = false)
        => MapUser(
            user,
            workspaceId,
            workspaceName,
            role.ToClaimValue(),
            role.ToDisplayName(),
            canManageGlobalDomains);

    public static ManagedUserDto ToManagedDto(this User user, long workspaceId, UserRole role)
        => MapManagedUser(user, workspaceId, role.ToClaimValue(), role.ToDisplayName());

    [MapperIgnoreSource(nameof(User.Role))]
    private static partial UserDto MapUser(
        User user,
        long workspaceId,
        string workspaceName,
        string role,
        string roleName,
        bool canManageGlobalDomains);

    [MapperIgnoreSource(nameof(User.Role))]
    private static partial ManagedUserDto MapManagedUser(
        User user,
        long workspaceId,
        string role,
        string roleName);

    public static partial DomainDto ToDto(this ShortDomain domain, long linkCount, bool canManage = false);

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

    [MapProperty(nameof(SecurityEvent.Type), nameof(SecurityEventDto.Type), Use = nameof(SecurityTypeCode))]
    [MapProperty(nameof(SecurityEvent.Type), nameof(SecurityEventDto.TypeName), Use = nameof(SecurityTypeName))]
    public static partial SecurityEventDto ToDto(this SecurityEvent securityEvent);

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

    [UserMapping(Default = false)]
    private static string SecurityTypeCode(SecurityEventType type)
        => SecurityEventTypes.ToCode((int)type);

    [UserMapping(Default = false)]
    private static string SecurityTypeName(SecurityEventType type)
        => SecurityEventTypes.ToName(SecurityTypeCode(type));
}
