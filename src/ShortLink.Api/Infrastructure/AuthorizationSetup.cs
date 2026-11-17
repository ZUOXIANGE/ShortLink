using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using ShortLink.Contracts.Partner;
using ShortLink.Domain.Users;

namespace ShortLink.Api.Infrastructure;

/// <summary>
/// 角色策略。认证阶段会把 JWT 的 <c>role</c> 快照刷新为当前工作区成员角色（小写）。
/// 端点只声明「最低权限」，具体归属（成员是否能改他人短链）由应用层的 <c>LinkPolicy</c> 判定。
/// </summary>
public static class AuthorizationSetup
{
    /// <summary>只读：登录即可。</summary>
    public const string Viewer = "viewer";

    /// <summary>可写：Owner / Admin / Member。</summary>
    public const string Writer = "writer";

    /// <summary>管理层：Owner / Admin，可管理域名、成员与安全事件。</summary>
    public const string Manager = "manager";

    /// <summary>仅所有者：可转让工作区、管理管理员账号。</summary>
    public const string Owner = "owner-only";

    public static IServiceCollection AddShortLinkAuthorization(this IServiceCollection services)
    {
        var builder = services.AddAuthorizationBuilder()
            .AddPolicy(Viewer, policy => policy
                .RequireAuthenticatedUser())
            .AddPolicy(Writer, policy => policy
                .RequireRole(UserRoles.Owner, UserRoles.Admin, UserRoles.Member))
            .AddPolicy(Manager, policy => policy
                .RequireRole(UserRoles.Owner, UserRoles.Admin))
            .AddPolicy(Owner, policy => policy
                .RequireRole(UserRoles.Owner));

        AddPartnerPolicies(builder);

        return services;
    }

    /// <summary>
    /// 第三方策略显式绑定 <c>ApiKey</c> 方案：JWT 用户调 Partner 端点会得到 401，
    /// 签名凭据调后台端点同样 401，避免两套身份互相越权。
    /// </summary>
    private static void AddPartnerPolicies(AuthorizationBuilder builder)
    {
        builder
            .AddPolicy(ApiPolicies.LinksRead, policy => policy
                .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => HasScope(context.User, ApiScopes.LinksRead)))
            .AddPolicy(ApiPolicies.LinksWrite, policy => policy
                .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireRole(UserRoles.Owner, UserRoles.Admin, UserRoles.Member)
                .RequireAssertion(context => HasScope(context.User, ApiScopes.LinksWrite)))
            .AddPolicy(ApiPolicies.LinksDelete, policy => policy
                .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireRole(UserRoles.Owner, UserRoles.Admin, UserRoles.Member)
                .RequireAssertion(context => HasScope(context.User, ApiScopes.LinksDelete)))
            .AddPolicy(ApiPolicies.AnalyticsRead, policy => policy
                .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => HasScope(context.User, ApiScopes.AnalyticsRead)));
    }

    private static bool HasScope(ClaimsPrincipal user, string scope)
        => user
            .FindAll("scope")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(scope, StringComparer.Ordinal);
}

/// <summary>第三方开放接口的授权策略名。</summary>
public static class ApiPolicies
{
    public const string LinksRead = "api:links:read";

    public const string LinksWrite = "api:links:write";

    public const string LinksDelete = "api:links:delete";

    public const string AnalyticsRead = "api:analytics:read";
}
