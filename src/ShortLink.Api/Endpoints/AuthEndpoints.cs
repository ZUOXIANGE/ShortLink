using ShortLink.Api.Infrastructure;
using ShortLink.Application.Auth;
using ShortLink.Contracts;
using ShortLink.Contracts.Auth;

namespace ShortLink.Api.Endpoints;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group)
    {
        var auth = group.MapGroup("/auth").WithTags("认证");

        auth.MapPost("/login", async (LoginRequest request, AuthService service, CancellationToken cancellationToken)
                => ApiResponse.Ok(await service.LoginAsync(request, cancellationToken)))
            .AllowAnonymous()
            .RequireRateLimiting("auth")
            .WithSummary("登录并获取访问令牌")
            .Produces<ApiResponse<LoginResponse>>();

        auth.MapGet("/me", async (AuthService service, CancellationToken cancellationToken)
                => ApiResponse.Ok(await service.GetCurrentUserAsync(cancellationToken)))
            .RequireAuthorization(AuthorizationSetup.Viewer)
            .WithSummary("获取当前登录用户")
            .Produces<ApiResponse<UserDto>>();

        auth.MapPost("/change-password", async (
                ChangePasswordRequest request,
                AuthService service,
                CancellationToken cancellationToken) =>
            {
                await service.ChangePasswordAsync(request, cancellationToken);
                return ApiResponse.Ok();
            })
            .RequireAuthorization(AuthorizationSetup.Viewer)
            .WithSummary("修改当前用户密码")
            .Produces<ApiResponse<object?>>();

        return group;
    }
}
