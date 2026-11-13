using ShortLink.Contracts.Auth;
using ShortLink.Contracts.Domains;
using ShortLink.Contracts.Links;
using ShortLink.Contracts.Users;
using ShortLink.Domain.Domains;
using ShortLink.Domain.Links;
using ShortLink.Domain.Users;

namespace ShortLink.Application.Mapping;

/// <summary>领域对象到契约 DTO 的映射。全部时间字段以 UTC 输出，客户端负责本地化展示。</summary>
public static class DtoMapper
{
    public static UserDto ToDto(
        this User user,
        long workspaceId,
        string workspaceName,
        UserRole role,
        bool canManageGlobalDomains = false)
        => new(
            user.Id,
            user.UserName,
            user.DisplayName,
            role.ToClaimValue(),
            role.ToDisplayName(),
            workspaceId,
            workspaceName,
            canManageGlobalDomains);

    public static ManagedUserDto ToManagedDto(this User user, long workspaceId, UserRole role)
        => new(
            user.Id,
            user.UserName,
            user.DisplayName,
            role.ToClaimValue(),
            role.ToDisplayName(),
            user.IsEnabled,
            workspaceId,
            user.CreatedAtUtc,
            user.LastLoginAtUtc);

    public static DomainDto ToDto(this ShortDomain domain, long linkCount, bool canManage = false)
        => new(
            domain.Id,
            domain.Host,
            domain.Scheme,
            domain.Name,
            domain.IsDefault,
            domain.IsEnabled,
            linkCount,
            domain.CreatedAtUtc,
            canManage);

    public static LinkDto ToDto(
        this Link link,
        ShortDomain? domain,
        int routeCount = 0,
        string? createdByName = null)
    {
        var shortUrl = domain is null ? $"/{link.Code}" : domain.BuildShortUrl(link.Code);
        return new LinkDto(
            link.Id,
            link.WorkspaceId,
            link.DomainId,
            domain?.Host ?? string.Empty,
            link.Code,
            shortUrl,
            link.TargetUrl,
            link.Title,
            StatusText(link.Status),
            (int)link.RedirectType,
            link.ExpiresAtUtc,
            link.StartsAtUtc,
            link.HasPassword,
            link.Remark,
            link.ClickCount,
            link.MaxClicks,
            BotPolicyText(link.BotPolicy),
            link.IpAllowList,
            link.IpBlockList,
            link.FallbackUrl,
            link.AntiBlockEnabled,
            link.HasConversionSecret,
            routeCount,
            link.CreatedByUserId,
            createdByName,
            link.CreatedAtUtc,
            link.UpdatedAtUtc,
            link.LastAccessedAtUtc);
    }

    public static LinkRouteDto ToDto(this LinkRoute route)
    {
        var conditions = route.GetConditions();
        return new LinkRouteDto(
            route.Id,
            route.LinkId,
            route.Name,
            route.Priority,
            route.Weight,
            route.TargetUrl,
            conditions.Match == RouteMatchMode.Any ? "any" : "all",
            conditions.Items
                .Select(c => new RouteConditionDto { Field = c.Field, Op = c.Op, Value = c.Value })
                .ToList(),
            route.IsEnabled,
            route.ClickCount);
    }

    public static string StatusText(LinkStatus status)
        => status == LinkStatus.Enabled ? "enabled" : "disabled";

    public static string BotPolicyText(LinkBotPolicy policy) => policy switch
    {
        LinkBotPolicy.Block => BotPolicies.Block,
        LinkBotPolicy.IgnoreCount => BotPolicies.IgnoreCount,
        _ => BotPolicies.Allow,
    };

    public static LinkStatus ParseStatus(string? status)
        => string.Equals(status, "enabled", StringComparison.OrdinalIgnoreCase)
            ? LinkStatus.Enabled
            : LinkStatus.Disabled;
}
