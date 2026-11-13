using ShortLink.Api.Infrastructure;
using ShortLink.Application.Domains;
using ShortLink.Contracts;
using ShortLink.Contracts.Domains;

namespace ShortLink.Api.Endpoints;

public static class DomainEndpoints
{
    public static RouteGroupBuilder MapDomainEndpoints(this RouteGroupBuilder group)
    {
        var domains = group.MapGroup("/domains").WithTags("域名");

        // 域名是全局资源：改一个 Host 会影响所有短链的解析与已分发链接，
        // 因此写操作收紧到 admin 及以上，读操作仍对全部登录用户开放（列表页需要它做筛选）。
        domains.MapGet("/", async (DomainService service, CancellationToken cancellationToken)
                => ApiResponse.Ok(await service.ListAsync(cancellationToken)))
            .RequireAuthorization(AuthorizationSetup.Viewer)
            .WithSummary("域名列表")
            .Produces<ApiResponse<IReadOnlyList<DomainDto>>>();

        domains.MapPost("/", async (CreateDomainRequest request, DomainService service, CancellationToken cancellationToken)
                => ApiResponse.Ok(await service.CreateAsync(request, cancellationToken)))
            .RequireAuthorization(AuthorizationSetup.Manager)
            .WithSummary("新增短链域名")
            .Produces<ApiResponse<DomainDto>>();

        domains.MapPut("/{id:long}", async (long id, UpdateDomainRequest request, DomainService service, CancellationToken cancellationToken)
                => ApiResponse.Ok(await service.UpdateAsync(id, request, cancellationToken)))
            .RequireAuthorization(AuthorizationSetup.Manager)
            .WithSummary("更新域名（Host 不可变更）")
            .Produces<ApiResponse<DomainDto>>();

        domains.MapDelete("/{id:long}", async (long id, DomainService service, CancellationToken cancellationToken) =>
            {
                await service.DeleteAsync(id, cancellationToken);
                return ApiResponse.Ok();
            })
            .RequireAuthorization(AuthorizationSetup.Manager)
            .WithSummary("删除域名（域名下仍有短链时拒绝）")
            .Produces<ApiResponse<object?>>();

        return group;
    }
}
