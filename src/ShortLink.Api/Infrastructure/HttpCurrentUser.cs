using System.Security.Claims;
using ShortLink.Application.Abstractions;
using ShortLink.Domain.Users;

namespace ShortLink.Api.Infrastructure;

/// <summary>
/// 从 HttpContext 的 JWT 声明读取当前用户。
/// <c>MapInboundClaims</c> 已关闭，claim 名保持原样（<c>sub</c> / <c>role</c> / <c>ws</c>）。
/// </summary>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public long? UserId
        => long.TryParse(Principal?.FindFirstValue("sub"), out var id) ? id : null;

    public string? UserName => Principal?.FindFirstValue("name");

    public string? Role => Principal?.FindFirstValue("role");

    public long? WorkspaceId
        => long.TryParse(Principal?.FindFirstValue("ws"), out var id) && id > 0 ? id : null;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public UserRole RoleValue => UserRoles.TryParse(Role, out var role) ? role : UserRole.Viewer;

    public bool IsAdmin => RoleValue.AtLeast(UserRole.Admin);

    public bool AtLeast(UserRole required) => IsAuthenticated && RoleValue.AtLeast(required);
}
