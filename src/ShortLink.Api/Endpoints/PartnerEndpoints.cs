using Microsoft.AspNetCore.Mvc;
using ShortLink.Api.Infrastructure;
using ShortLink.Application.Analytics;
using ShortLink.Application.Links;
using ShortLink.Application.Mapping;
using ShortLink.Contracts;
using ShortLink.Contracts.Analytics;
using ShortLink.Contracts.Links;
using ShortLink.Contracts.Partner;

namespace ShortLink.Api.Endpoints;

/// <summary>
/// 第三方开放接口。所有端点只接受签名凭据，业务数据仍由应用层按工作区与角色过滤，
/// 这里刻意不注入仓储或 DbContext，避免出现绕过 <c>LinkPolicy</c> 的旁路。
/// </summary>
public static class PartnerEndpoints
{
    public static RouteGroupBuilder MapPartnerEndpoints(this RouteGroupBuilder group)
    {
        var partner = group.MapGroup("/partner")
            .WithTags("第三方开放")
            .RequireRateLimiting(RateLimitingSetup.PartnerPolicy);

        partner.MapGet("/links", async (
                [AsParameters] LinkQuery query,
                LinkService service,
                CancellationToken cancellationToken) =>
            {
                var page = await service.QueryAsync(query, cancellationToken).ConfigureAwait(false);
                return ApiResponse.Ok(new PagedResult<PartnerLinkDto>(
                    page.Items.Select(link => link.ToPartnerDto()).ToList(),
                    page.Total,
                    page.Page,
                    page.PageSize));
            })
            .RequireAuthorization(ApiPolicies.LinksRead)
            .WithSummary("分页查询短链（第三方）")
            .Produces<ApiResponse<PagedResult<PartnerLinkDto>>>();

        partner.MapGet("/links/resolve", async (
                string url,
                LinkService service,
                CancellationToken cancellationToken)
                => ApiResponse.Ok((await service.GetByShortUrlAsync(url, cancellationToken)
                    .ConfigureAwait(false)).ToPartnerDto()))
            .RequireAuthorization(ApiPolicies.LinksRead)
            .WithSummary("按短链地址或短码反查（第三方）")
            .Produces<ApiResponse<PartnerLinkDto>>();

        partner.MapGet("/links/{id:long}", async (
                long id,
                LinkService service,
                CancellationToken cancellationToken)
                => ApiResponse.Ok((await service.GetAsync(id, cancellationToken)
                    .ConfigureAwait(false)).ToPartnerDto()))
            .RequireAuthorization(ApiPolicies.LinksRead)
            .WithSummary("短链详情（第三方）")
            .Produces<ApiResponse<PartnerLinkDto>>();

        partner.MapPost("/links", async (
                CreateLinkRequest request,
                LinkService service,
                CancellationToken cancellationToken)
                => ApiResponse.Ok((await service.CreateAsync(request, cancellationToken)
                    .ConfigureAwait(false)).ToPartnerDto()))
            .RequireAuthorization(ApiPolicies.LinksWrite)
            .WithSummary("创建短链（第三方）")
            .Produces<ApiResponse<PartnerLinkDto>>();

        partner.MapPut("/links/{id:long}", async (
                long id,
                UpdateLinkRequest request,
                LinkService service,
                CancellationToken cancellationToken)
                => ApiResponse.Ok((await service.UpdateAsync(id, request, cancellationToken)
                    .ConfigureAwait(false)).ToPartnerDto()))
            .RequireAuthorization(ApiPolicies.LinksWrite)
            .WithSummary("更新短链（第三方）")
            .Produces<ApiResponse<PartnerLinkDto>>();

        // 幂等设置目标状态；不要暴露 toggle，两个并发请求会把状态翻回原值。
        partner.MapPost("/links/{id:long}/enable", async (
                long id,
                LinkService service,
                CancellationToken cancellationToken)
                => ApiResponse.Ok((await service.SetEnabledAsync(id, true, cancellationToken)
                    .ConfigureAwait(false)).ToPartnerDto()))
            .RequireAuthorization(ApiPolicies.LinksWrite)
            .WithSummary("启用短链（第三方）")
            .Produces<ApiResponse<PartnerLinkDto>>();

        partner.MapPost("/links/{id:long}/disable", async (
                long id,
                LinkService service,
                CancellationToken cancellationToken)
                => ApiResponse.Ok((await service.SetEnabledAsync(id, false, cancellationToken)
                    .ConfigureAwait(false)).ToPartnerDto()))
            .RequireAuthorization(ApiPolicies.LinksWrite)
            .WithSummary("停用短链（第三方）")
            .Produces<ApiResponse<PartnerLinkDto>>();

        partner.MapDelete("/links/{id:long}", async (
                long id,
                LinkService service,
                CancellationToken cancellationToken) =>
            {
                await service.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
                return ApiResponse.Ok();
            })
            .RequireAuthorization(ApiPolicies.LinksDelete)
            .WithSummary("删除短链（第三方）")
            .Produces<ApiResponse<object?>>();

        partner.MapGet("/links/{id:long}/stats", async (
                long id,
                int? days,
                AnalyticsService service,
                CancellationToken cancellationToken)
                => ApiResponse.Ok(await service.GetLinkStatsAsync(
                        id,
                        days ?? AnalyticsService.DefaultDays,
                        cancellationToken)
                    .ConfigureAwait(false)))
            .RequireAuthorization(ApiPolicies.AnalyticsRead)
            .WithSummary("单链访问统计（第三方，仅聚合数据）")
            .Produces<ApiResponse<LinkStatsDto>>();

        partner.MapGet("/overview", async (
                int? days,
                AnalyticsService service,
                CancellationToken cancellationToken) =>
            {
                var overview = await service.GetDashboardAsync(
                        days ?? AnalyticsService.DefaultDays,
                        cancellationToken)
                    .ConfigureAwait(false);

                return ApiResponse.Ok(overview.ToPartnerDto());
            })
            .RequireAuthorization(ApiPolicies.AnalyticsRead)
            .WithSummary("工作区总览（第三方，仅聚合数据）")
            .Produces<ApiResponse<PartnerOverviewDto>>();

        return group;
    }
}
