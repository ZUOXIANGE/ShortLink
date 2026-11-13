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
        services.AddAuthorizationBuilder()
            .AddPolicy(Viewer, policy => policy
                .RequireAuthenticatedUser())
            .AddPolicy(Writer, policy => policy
                .RequireRole(UserRoles.Owner, UserRoles.Admin, UserRoles.Member))
            .AddPolicy(Manager, policy => policy
                .RequireRole(UserRoles.Owner, UserRoles.Admin))
            .AddPolicy(Owner, policy => policy
                .RequireRole(UserRoles.Owner));

        return services;
    }
}
