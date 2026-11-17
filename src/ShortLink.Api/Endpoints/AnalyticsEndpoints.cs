using ShortLink.Api.Infrastructure;
using ShortLink.Application.Analytics;
using ShortLink.Contracts;
using ShortLink.Contracts.Analytics;

namespace ShortLink.Api.Endpoints;

public sealed class AnalyticsEndpoints : IApiEndpointModule
{
    public static void MapEndpoints(RouteGroupBuilder group)
    {
        var analytics = group.MapGroup("/dashboard").WithTags("统计");

        analytics.MapGet("/overview", async (
                int? days,
                AnalyticsService service,
                CancellationToken cancellationToken)
                => ApiResponse.Ok(await service.GetDashboardAsync(days ?? 14, cancellationToken)))
            .RequireAuthorization(AuthorizationSetup.Viewer)
            .WithSummary("控制台总览")
            .Produces<ApiResponse<DashboardOverviewDto>>();
    }
}
