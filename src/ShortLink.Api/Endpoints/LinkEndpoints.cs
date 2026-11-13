using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using ShortLink.Api.Infrastructure;
using ShortLink.Application.Analytics;
using ShortLink.Application.Links;
using ShortLink.Contracts;
using ShortLink.Contracts.Analytics;
using ShortLink.Contracts.Links;

namespace ShortLink.Api.Endpoints;

public static class LinkEndpoints
{
    public static RouteGroupBuilder MapLinkEndpoints(this RouteGroupBuilder group)
    {
        var links = group.MapGroup("/links").WithTags("短链");

        links.MapGet("/", async ([AsParameters] LinkQuery query, LinkService service, CancellationToken cancellationToken)
                => ApiResponse.Ok(await service.QueryAsync(query, cancellationToken)))
            .RequireAuthorization(AuthorizationSetup.Viewer)
            .WithSummary("分页查询短链")
            .Produces<ApiResponse<PagedResult<LinkDto>>>();

        links.MapGet("/{id:long}", async (long id, LinkService service, CancellationToken cancellationToken)
                => ApiResponse.Ok(await service.GetAsync(id, cancellationToken)))
            .RequireAuthorization(AuthorizationSetup.Viewer)
            .WithSummary("短链详情")
            .Produces<ApiResponse<LinkDto>>();

        links.MapPost("/", async (CreateLinkRequest request, LinkService service, CancellationToken cancellationToken)
                => ApiResponse.Ok(await service.CreateAsync(request, cancellationToken)))
            .RequireAuthorization(AuthorizationSetup.Writer)
            .WithSummary("创建短链（短码留空自动生成）")
            .Produces<ApiResponse<LinkDto>>();

        links.MapPut("/{id:long}", async (long id, UpdateLinkRequest request, LinkService service, CancellationToken cancellationToken)
                => ApiResponse.Ok(await service.UpdateAsync(id, request, cancellationToken)))
            .RequireAuthorization(AuthorizationSetup.Writer)
            .WithSummary("更新短链")
            .Produces<ApiResponse<LinkDto>>();

        links.MapPost("/{id:long}/toggle", async (long id, LinkService service, CancellationToken cancellationToken)
                => ApiResponse.Ok(await service.ToggleAsync(id, cancellationToken)))
            .RequireAuthorization(AuthorizationSetup.Writer)
            .WithSummary("启用 / 停用短链")
            .Produces<ApiResponse<LinkDto>>();

        links.MapDelete("/{id:long}", async (long id, LinkService service, CancellationToken cancellationToken) =>
            {
                await service.DeleteAsync(id, cancellationToken);
                return ApiResponse.Ok();
            })
            .RequireAuthorization(AuthorizationSetup.Writer)
            .WithSummary("删除短链")
            .Produces<ApiResponse<object?>>();

        links.MapPost("/batch", async (BatchLinkRequest request, LinkService service, CancellationToken cancellationToken)
                => ApiResponse.Ok(await service.BatchAsync(request, cancellationToken)))
            .RequireAuthorization(AuthorizationSetup.Writer)
            .WithSummary("批量操作（启停 / 删除 / 改域名 / 改过期时间，单次上限 500）")
            .Produces<ApiResponse<BatchResult>>();

        // CSV 导出走流式响应：不套统一信封（响应体不是 JSON），失败时才由中间件补信封。
        links.MapGet("/export", async (
                [AsParameters] LinkQuery query,
                ExportService export,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {
                // 在首字节写出前给出确定元数据；流式响应开始后就无法再补响应头。
                var metadata = await export.GetLinkExportMetadataAsync(query, cancellationToken).ConfigureAwait(false);
                httpContext.Response.Headers["X-Export-Truncated"] = metadata.Truncated ? "true" : "false";
                httpContext.Response.Headers["X-Export-Row-Count"] = metadata.RowCount.ToString(CultureInfo.InvariantCulture);
                httpContext.Response.Headers["X-Export-Row-Limit"] = ExportService.MaxRows.ToString(CultureInfo.InvariantCulture);

                // Content-Disposition 已在 CORS 中暴露；自定义参数让跨域浏览器也能读到截断状态。
                var contentDisposition = new ContentDispositionHeaderValue("attachment")
                {
                    FileNameStar = $"links-{DateTime.UtcNow:yyyyMMddHHmmss}.csv",
                };
                contentDisposition.Parameters.Add(new NameValueHeaderValue(
                    "truncated",
                    metadata.Truncated ? "true" : "false"));
                contentDisposition.Parameters.Add(new NameValueHeaderValue("row-count", metadata.RowCount.ToString(CultureInfo.InvariantCulture)));
                contentDisposition.Parameters.Add(new NameValueHeaderValue("row-limit", ExportService.MaxRows.ToString(CultureInfo.InvariantCulture)));
                httpContext.Response.Headers["Content-Disposition"] = contentDisposition.ToString();

                return Results.Stream(
                    async stream =>
                    {
                        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 16 * 1024, leaveOpen: true);
                        await export.WriteLinksAsync(query, writer, cancellationToken).ConfigureAwait(false);
                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    },
                    "text/csv; charset=utf-8");
            })
            .RequireAuthorization(AuthorizationSetup.Viewer)
            .WithSummary("导出短链列表 CSV")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv");

        links.MapGet("/{id:long}/stats", async (
                long id,
                int? days,
                AnalyticsService service,
                CancellationToken cancellationToken)
                => ApiResponse.Ok(await service.GetLinkStatsAsync(id, days ?? AnalyticsService.DefaultDays, cancellationToken)))
            .RequireAuthorization(AuthorizationSetup.Viewer)
            .WithSummary("单链访问统计（含 A/B 分支与转化）")
            .Produces<ApiResponse<LinkStatsDto>>();

        links.MapGet("/{id:long}/clicks", async (
                long id,
                int? take,
                AnalyticsService service,
                CancellationToken cancellationToken)
                => ApiResponse.Ok(await service.GetRecentClicksAsync(id, take ?? 20, cancellationToken)))
            .RequireAuthorization(AuthorizationSetup.Viewer)
            .WithSummary("最近访问明细")
            .Produces<ApiResponse<IReadOnlyList<ClickLogDto>>>();

        links.MapGet("/{id:long}/clicks/export", (
                long id,
                int? days,
                ExportService export,
                CancellationToken cancellationToken)
                => Results.Stream(
                    async stream =>
                    {
                        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 16 * 1024, leaveOpen: true);
                        await export.WriteClicksAsync(id, days ?? 30, writer, cancellationToken).ConfigureAwait(false);
                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    },
                    "text/csv; charset=utf-8",
                    $"clicks-{id}-{DateTime.UtcNow:yyyyMMddHHmmss}.csv"))
            .RequireAuthorization(AuthorizationSetup.Viewer)
            .WithSummary("导出单链点击明细 CSV")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv");

        links.MapGet("/{id:long}/routes", async (long id, RouteService service, CancellationToken cancellationToken)
                => ApiResponse.Ok(await service.ListAsync(id, cancellationToken)))
            .RequireAuthorization(AuthorizationSetup.Viewer)
            .WithSummary("查询条件路由 / A/B 分支")
            .Produces<ApiResponse<IReadOnlyList<LinkRouteDto>>>();

        links.MapPut("/{id:long}/routes", async (
                long id,
                SaveLinkRoutesRequest request,
                RouteService service,
                CancellationToken cancellationToken)
                => ApiResponse.Ok(await service.ReplaceAsync(id, request, cancellationToken)))
            .RequireAuthorization(AuthorizationSetup.Writer)
            .WithSummary("覆盖保存条件路由 / A/B 分支")
            .Produces<ApiResponse<IReadOnlyList<LinkRouteDto>>>();

        links.MapPost("/{id:long}/routes/test", async (
                long id,
                RouteTestRequest request,
                RouteService service,
                CancellationToken cancellationToken)
                => ApiResponse.Ok(await service.TestAsync(id, request, cancellationToken)))
            .RequireAuthorization(AuthorizationSetup.Viewer)
            .WithSummary("按给定上下文试算命中分支与分流比例")
            .Produces<ApiResponse<RouteTestResult>>();

        links.MapGet("/{id:long}/conversion-key", async (
                long id,
                LinkService service,
                CancellationToken cancellationToken)
                => ApiResponse.Ok(await service.GetConversionKeyAsync(id, cancellationToken)))
            .RequireAuthorization(AuthorizationSetup.Writer)
            .WithSummary("获取（或首次生成）转化回调密钥")
            .Produces<ApiResponse<ConversionKeyDto>>();

        return group;
    }
}
