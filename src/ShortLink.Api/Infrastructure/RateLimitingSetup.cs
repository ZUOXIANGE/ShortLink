using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using ShortLink.Application.Options;
using ShortLink.Contracts;
using ShortLink.Infrastructure.RateLimiting;
using ShortLink.Mcp.Options;
using StackExchange.Redis;

namespace ShortLink.Api.Infrastructure;

/// <summary>
/// 限流装配。认证、转化、举报与短链密码策略都<b>按来源 IP 分区</b>：
/// 早期实现把登录限流写成 <c>AddFixedWindowLimiter("auth")</c>，那是全进程共享的一个窗口，
/// 任何人都能用 10 次错误登录把其他用户的登录口堵死。
/// <para>
/// 配置了 Redis（且未显式关闭 <c>RateLimit:UseDistributed</c>）时使用
/// <see cref="DistributedWindowRateLimiter"/> 共享配额，否则退化为进程内窗口。
/// 阈值在选项解析阶段读取，这样测试宿主/容器的配置覆盖才会生效。
/// </para>
/// </summary>
public static class RateLimitingSetup
{
    /// <summary>MCP 端点策略名。工具调用按调用者分区，而不是按 IP。</summary>
    public const string McpPolicy = "mcp";

    /// <summary>短链访问密码策略名，按来源 IP 与短码共同分区。</summary>
    public const string LinkPasswordPolicy = "link-password";

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddShortLinkRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status200OK;
            options.OnRejected = async (context, _) =>
            {
                var http = context.HttpContext;
                if (!http.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                {
                    http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    {
                        http.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }

                    var mcp = http.RequestServices.GetService<IOptions<McpOptions>>()?.Value;
                    if (mcp is { Enabled: true } && http.Request.Path.StartsWithSegments(mcp.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    http.Response.ContentType = "text/html; charset=utf-8";
                    await http.Response.WriteAsync(
                        Endpoints.RedirectPageRenderer.ErrorPage("请求过于频繁", "请稍后再试。"),
                        http.RequestAborted).ConfigureAwait(false);
                    return;
                }

                await AuthenticationSetup.WriteEnvelopeAsync(
                    http,
                    BizCodes.RateLimited,
                    BizCodes.Describe(BizCodes.RateLimited)).ConfigureAwait(false);
            };
        });

        services
            .AddOptions<RateLimiterOptions>()
            .Configure<IOptions<RateLimitOptions>>((options, accessor) =>
            {
                var limits = accessor.Value;

                options.AddPolicy("auth", context => CreatePartition(context, "auth", limits.AuthPermitPerMinute));
                options.AddPolicy("conversion", context => CreatePartition(context, "conversion", limits.ConversionPermitPerMinute));
                options.AddPolicy("abuse", context => CreatePartition(context, "abuse", limits.AbusePermitPerMinute));
                options.AddPolicy(LinkPasswordPolicy, context => CreatePartition(
                    context,
                    LinkPasswordPolicy,
                    limits.LinkPasswordPermitPerMinute,
                    LinkPasswordKey));
            });

        // MCP 按「调用者」分区：AI 客户端通常从同一出口 IP、用同一账号批量调用，
        // 按 IP 分区会把一个团队的行为算成一个人；按 sub 分区才对应真实配额归属。
        // 未携带令牌时回落到 IP（端点本身要求认证，这里只是兜底）。
        services
            .AddOptions<RateLimiterOptions>()
            .Configure<IOptions<McpOptions>>((options, accessor) =>
            {
                var permitLimit = Math.Max(accessor.Value.PermitPerMinute, 1);
                options.AddPolicy(McpPolicy, context => CreatePartition(context, McpPolicy, permitLimit, CallerKey));
            });

        return services;
    }

    private static RateLimitPartition<string> CreatePartition(
        HttpContext context,
        string policy,
        int permitLimit,
        Func<HttpContext, string>? keySelector = null)
    {
        var partitionKey = $"{policy}:{(keySelector ?? ClientKey)(context)}";

        return RateLimitPartition.Get(partitionKey, _ => CreateLimiter(context.RequestServices, policy, partitionKey, permitLimit));
    }

    /// <summary>调用者身份：优先 JWT 的 <c>sub</c>，没有令牌时回落 IP。</summary>
    private static string CallerKey(HttpContext context)
    {
        var subject = context.User?.FindFirst("sub")?.Value;
        return string.IsNullOrWhiteSpace(subject) ? ClientKey(context) : $"user:{subject}";
    }

    private static string LinkPasswordKey(HttpContext context)
    {
        var code = context.Request.RouteValues["code"]?.ToString()?.Trim();
        return $"{ClientKey(context)}:code:{(string.IsNullOrEmpty(code) ? "unknown" : code)}";
    }

    private static RateLimiter CreateLimiter(IServiceProvider services, string policy, string partitionKey, int permitLimit)
    {
        var distributed = services.GetService<IConnectionMultiplexer>();
        var useDistributed = services
            .GetRequiredService<IOptions<RateLimitOptions>>()
            .Value
            .UseDistributed ?? distributed is not null;

        if (!useDistributed || distributed is null)
        {
            return new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = Window,
                QueueLimit = 0,
            });
        }

        return new DistributedWindowRateLimiter(
            distributed,
            services.GetRequiredService<ILogger<DistributedWindowRateLimiter>>(),
            $"shortlink:rl:{partitionKey}",
            permitLimit,
            Window);
    }

    /// <summary>
    /// 分区键：取连接来源 IP。开启 <c>ReverseProxy:TrustForwardedHeaders</c> 后这里已是真实客户端 IP；
    /// 未开启时拿到的是代理 IP，所有用户会共享一个桶——这也是文档要求「在反代后必须开启」的原因。
    /// </summary>
    private static string ClientKey(HttpContext context)
        => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
