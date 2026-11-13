using Microsoft.EntityFrameworkCore;
using ShortLink.Application.Abstractions;
using ShortLink.Contracts;
using ShortLink.Infrastructure.Persistence;

namespace ShortLink.Api.Endpoints;

public static class HealthEndpoints
{
    /// <summary>探针端点保留真实 HTTP 状态码，便于 K8s / 负载均衡判定，不走统一信封。</summary>
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // 存活探针：只证明进程还能响应，不查任何外部依赖——
        // 数据库抖动时不应该让编排系统重启所有实例。
        app.MapGet("/health", () => Results.Text("ok", "text/plain; charset=utf-8"))
            .AllowAnonymous()
            .ExcludeFromDescription();

        // 就绪探针：数据库必须可连；配置了 Redis 时还必须能 ping 通，
        // 否则本实例的缓存与限流都会异常，不应该被分到流量。
        app.MapGet("/health/ready", async (
                ShortLinkDbContext db,
                ICacheHealthProbe cache,
                CancellationToken cancellationToken) =>
            {
                var databaseOk = await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false);
                var cacheOk = await cache.IsAvailableAsync(cancellationToken).ConfigureAwait(false);

                if (databaseOk && cacheOk)
                {
                    return Results.Text("ready", "text/plain; charset=utf-8");
                }

                var detail = $"database={Flag(databaseOk)},cache={Flag(cacheOk)}";
                return Results.Text(detail, "text/plain; charset=utf-8", statusCode: StatusCodes.Status503ServiceUnavailable);
            })
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.MapGet("/health/info", (
                ICacheTopology cacheTopology,
                IStatisticsCalendar calendar) => ApiResponse.Ok(new
            {
                service = "ShortLink.Api",
                status = "ok",

                // 暴露缓存拓扑而不是留一句「请自行确认」：
                // in-process 意味着停用/删除一条短链后，其他实例最长要等 LinkTtlSeconds 才生效。
                cache = new
                {
                    backplane = cacheTopology.Backplane,
                    crossInstanceInvalidation = cacheTopology.HasDistributedBackplane,
                    linkTtlSeconds = cacheTopology.LinkTtlSeconds,
                },

                statistics = new { timeZone = calendar.TimeZoneId },
            }))
            .AllowAnonymous()
            .ExcludeFromDescription();

        return app;
    }

    private static string Flag(bool healthy) => healthy ? "ok" : "fail";
}
