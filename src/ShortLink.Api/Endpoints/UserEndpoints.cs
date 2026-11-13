using Microsoft.AspNetCore.Mvc;
using ShortLink.Api.Infrastructure;
using ShortLink.Application.Users;
using ShortLink.Contracts;
using ShortLink.Contracts.Users;

namespace ShortLink.Api.Endpoints;

public static class UserEndpoints
{
    public static RouteGroupBuilder MapUserEndpoints(this RouteGroupBuilder group)
    {
        var users = group.MapGroup("/users").WithTags("用户与角色");

        users.MapGet("/", async ([AsParameters] UserQuery query, UserService service, CancellationToken cancellationToken)
                => ApiResponse.Ok(await service.QueryAsync(query, cancellationToken)))
            .RequireAuthorization(AuthorizationSetup.Manager)
            .WithSummary("分页查询账号")
            .Produces<ApiResponse<PagedResult<ManagedUserDto>>>();

        users.MapPost("/", async (CreateUserRequest request, UserService service, CancellationToken cancellationToken)
                => ApiResponse.Ok(await service.CreateAsync(request, cancellationToken)))
            .RequireAuthorization(AuthorizationSetup.Manager)
            .WithSummary("新建账号并分配角色")
            .Produces<ApiResponse<ManagedUserDto>>();

        users.MapPut("/{id:long}", async (
                long id,
                UpdateUserRequest request,
                UserService service,
                CancellationToken cancellationToken)
                => ApiResponse.Ok(await service.UpdateAsync(id, request, cancellationToken)))
            .RequireAuthorization(AuthorizationSetup.Manager)
            .WithSummary("修改账号资料 / 角色 / 启停")
            .Produces<ApiResponse<ManagedUserDto>>();

        users.MapPost("/{id:long}/reset-password", async (
                long id,
                ResetPasswordRequest request,
                UserService service,
                CancellationToken cancellationToken) =>
            {
                await service.ResetPasswordAsync(id, request, cancellationToken);
                return ApiResponse.Ok();
            })
            .RequireAuthorization(AuthorizationSetup.Manager)
            .WithSummary("重置指定账号密码")
            .Produces<ApiResponse<object?>>();

        return group;
    }
}
