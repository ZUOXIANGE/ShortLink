using Microsoft.AspNetCore.Mvc;
using ShortLink.Api.Infrastructure;
using ShortLink.Application.Security;
using ShortLink.Contracts;
using ShortLink.Contracts.Security;

namespace ShortLink.Api.Endpoints;

public static class SecurityEndpoints
{
    public static RouteGroupBuilder MapSecurityEndpoints(this RouteGroupBuilder group)
    {
        var security = group.MapGroup("/security").WithTags("安全");

        security.MapGet("/events", async (
                [AsParameters] SecurityEventQuery query,
                SecurityService service,
                CancellationToken cancellationToken)
                => ApiResponse.Ok(await service.QueryAsync(query, cancellationToken)))
            .RequireAuthorization(AuthorizationSetup.Manager)
            .WithSummary("分页查询安全事件")
            .Produces<ApiResponse<PagedResult<SecurityEventDto>>>();

        // 举报入口必须匿名可用：受害者往往既没有账号，也不该被要求先注册。
        group.MapPost("/abuse/reports", async (
                AbuseReportRequest request,
                HttpContext http,
                SecurityService service,
                CancellationToken cancellationToken) =>
            {
                await service.ReportAbuseAsync(
                    request,
                    http.Connection.RemoteIpAddress?.ToString(),
                    http.Request.Headers.UserAgent.ToString(),
                    cancellationToken);

                return ApiResponse.Ok();
            })
            .AllowAnonymous()
            .RequireRateLimiting("abuse")
            .WithTags("安全")
            .WithSummary("提交滥用举报（匿名）")
            .Produces<ApiResponse<object?>>();

        return group;
    }
}
