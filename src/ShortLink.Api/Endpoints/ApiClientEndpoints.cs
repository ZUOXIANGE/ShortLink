using ShortLink.Api.Infrastructure;
using ShortLink.Application.Partner;
using ShortLink.Contracts;
using ShortLink.Contracts.Partner;

namespace ShortLink.Api.Endpoints;

/// <summary>
/// API 凭据管理。属于后台能力，只接受 JWT 且要求所有者：
/// 签发凭据等于把工作区数据交给外部系统，不能让管理员或成员自行扩大集成面。
/// </summary>
public static class ApiClientEndpoints
{
    public static RouteGroupBuilder MapApiClientEndpoints(this RouteGroupBuilder group)
    {
        var clients = group.MapGroup("/api-clients").WithTags("API 凭据");

        clients.MapGet("/", async (ApiClientService service, CancellationToken cancellationToken)
                => ApiResponse.Ok(await service.ListAsync(cancellationToken).ConfigureAwait(false)))
            .RequireAuthorization(AuthorizationSetup.Owner)
            .WithSummary("API 凭据列表")
            .Produces<ApiResponse<IReadOnlyList<ApiClientDto>>>();

        clients.MapPost("/", async (
                CreateApiClientRequest request,
                ApiClientService service,
                CancellationToken cancellationToken)
                => ApiResponse.Ok(await service.CreateAsync(request, cancellationToken).ConfigureAwait(false)))
            .RequireAuthorization(AuthorizationSetup.Owner)
            .WithSummary("创建 API 凭据（secret 只返回一次）")
            .Produces<ApiResponse<CreateApiClientResponse>>();

        clients.MapPut("/{id:long}", async (
                long id,
                UpdateApiClientRequest request,
                ApiClientService service,
                CancellationToken cancellationToken)
                => ApiResponse.Ok(await service.UpdateAsync(id, request, cancellationToken).ConfigureAwait(false)))
            .RequireAuthorization(AuthorizationSetup.Owner)
            .WithSummary("更新 API 凭据")
            .Produces<ApiResponse<ApiClientDto>>();

        clients.MapPost("/{id:long}/rotate", async (
                long id,
                ApiClientService service,
                CancellationToken cancellationToken)
                => ApiResponse.Ok(await service.RotateAsync(id, cancellationToken).ConfigureAwait(false)))
            .RequireAuthorization(AuthorizationSetup.Owner)
            .WithSummary("轮换 API 凭据 secret（旧 secret 立即失效）")
            .Produces<ApiResponse<RotateApiClientSecretResponse>>();

        clients.MapPost("/{id:long}/revoke", async (
                long id,
                ApiClientService service,
                CancellationToken cancellationToken) =>
            {
                await service.RevokeAsync(id, cancellationToken).ConfigureAwait(false);
                return ApiResponse.Ok();
            })
            .RequireAuthorization(AuthorizationSetup.Owner)
            .WithSummary("吊销 API 凭据")
            .Produces<ApiResponse<object?>>();

        return group;
    }
}
