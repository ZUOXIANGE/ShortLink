using System.Net;
using System.Text.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.AspNetCore;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using Serilog;
using ShortLink.Api.Endpoints;
using ShortLink.Api.Infrastructure;
using ShortLink.Application;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Options;
using ShortLink.Contracts;
using ShortLink.Infrastructure;
using ShortLink.Infrastructure.Diagnostics;
using ShortLink.Infrastructure.Options;
using ShortLink.Infrastructure.Persistence;
using ShortLink.Mcp;
using ShortLink.Mcp.Diagnostics;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((_, _, configuration) => configuration
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", Serilog.Events.LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("service.name", "shortlink-api")
        .WriteTo.Console(
            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}"));

    // ── 分层注册 ─────────────────────────────────────────────
    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddApplication();

    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddSingleton<IProblemDetailsWriter, EnvelopeProblemDetailsWriter>();

    // UseExceptionHandler() 需要一个兜底委托：当所有 IExceptionHandler 都未处理（如响应已开始）时使用。
    builder.Services.Configure<ExceptionHandlerOptions>(options =>
        options.ExceptionHandler = context => AuthenticationSetup.WriteEnvelopeAsync(
            context,
            BizCodes.InternalError,
            BizCodes.Describe(BizCodes.InternalError)));
    builder.Services.ConfigureHttpJsonOptions(options =>
        options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

    builder.Services.AddJwtAuthentication();
    builder.Services.AddShortLinkAuthorization();

    // ── CORS：管理后台为独立站点，必须白名单来源 ─────────────
    var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            // 文件下载走的是字节流，前端需要读到文件名与业务码；
            // 跨域下这些响应头默认对 JS 不可见，必须显式暴露。
            .WithExposedHeaders(
                "X-Biz-Code",
                "Content-Disposition",
                "X-Export-Truncated",
                "X-Export-Total",
                "X-Export-Limit")));

    // ── API 版本 ────────────────────────────────────────────
    builder.Services
        .AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(1, 0);
            options.AssumeDefaultVersionWhenUnspecified = true;
            options.ReportApiVersions = true;
            options.ApiVersionReader = new UrlSegmentApiVersionReader();
        })
        .AddApiExplorer(options =>
        {
            options.GroupNameFormat = "'v'VVV";
            options.SubstituteApiVersionInUrl = true;
        })
        .AddOpenApi();

    // ── 限流：按 IP 分区，拒绝时也返回统一信封 ──────────────
    builder.Services.AddShortLinkRateLimiting();

    // ── MCP：把短链与统计能力暴露给 AI 客户端（默认关闭）──────
    builder.Services.AddShortLinkMcp(builder.Configuration);

    // ── 反向代理：仅在可信代理后面开启，避免伪造 X-Forwarded-* ──
    var trustForwardedHeaders = builder.Configuration.GetValue("ReverseProxy:TrustForwardedHeaders", false);
    if (trustForwardedHeaders)
    {
        var knownProxies = builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [];
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            options.ForwardLimit = 1;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();

            foreach (var address in knownProxies)
            {
                if (!IPAddress.TryParse(address, out var proxy))
                {
                    throw new InvalidOperationException($"ReverseProxy:KnownProxies 包含无效 IP：{address}");
                }

                options.KnownProxies.Add(proxy);
            }

            if (options.KnownProxies.Count == 0)
            {
                if (!builder.Environment.IsDevelopment())
                {
                    throw new InvalidOperationException(
                        "开启 ReverseProxy:TrustForwardedHeaders 时必须显式配置 ReverseProxy:KnownProxies，禁止信任所有上游。");
                }

                options.KnownProxies.Add(IPAddress.Loopback);
                options.KnownProxies.Add(IPAddress.IPv6Loopback);
            }
        });
    }

    // ── 可观测性：traces / metrics / logs 三信号 ────────────
    // 端点优先取本项目的 OpenTelemetry:Endpoint；为空则退回 OpenTelemetry 的标准环境变量
    // OTEL_EXPORTER_OTLP_ENDPOINT —— Aspire 的发布产物、k8s 侧车、Collector 注入的都是后者，
    // 只认自定义键会让「容器里链路追踪莫名其妙是空的」，而日志里没有任何线索。
    var otlpEndpoint = builder.Configuration["OpenTelemetry:Endpoint"];
    if (string.IsNullOrWhiteSpace(otlpEndpoint))
    {
        otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
    }

    var hasOtlp = !string.IsNullOrWhiteSpace(otlpEndpoint);

    // 标准变量同时约定协议：Collector 常见只开 4318（http/protobuf），
    // 一律按 gRPC 发会一直连接失败，且失败信息只出现在导出器内部日志里。
    var exportProtocol = builder.Configuration["OTEL_EXPORTER_OTLP_PROTOCOL"]?.Contains(
        "http", StringComparison.OrdinalIgnoreCase) == true
        ? OtlpExportProtocol.HttpProtobuf
        : OtlpExportProtocol.Grpc;

    void ConfigureOtlp(OpenTelemetry.Exporter.OtlpExporterOptions exporter)
    {
        exporter.Endpoint = new Uri(otlpEndpoint!);
        exporter.Protocol = exportProtocol;
    }

    builder.Services
        .AddOpenTelemetry()
        .ConfigureResource(resource => resource
            .AddService(serviceName: "shortlink-api", serviceVersion: "1.0.0")
            .AddAttributes(new Dictionary<string, object>
            {
                ["deployment.environment"] = builder.Environment.EnvironmentName,
            }))
        .WithTracing(tracing =>
        {
            tracing.AddAspNetCoreInstrumentation(options =>
                options.Filter = context => !context.Request.Path.StartsWithSegments("/health"));
            tracing.AddHttpClientInstrumentation();
            tracing.AddSource(ShortLinkDiagnostics.ActivitySourceName);
            tracing.AddSource(McpDiagnostics.ActivitySourceName);

            if (hasOtlp)
            {
                tracing.AddOtlpExporter(ConfigureOtlp);
            }
        })
        .WithMetrics(metrics =>
        {
            metrics.AddAspNetCoreInstrumentation();
            metrics.AddRuntimeInstrumentation();
            metrics.AddMeter(ShortLinkDiagnostics.MeterName);
            metrics.AddMeter(McpDiagnostics.MeterName);

            if (hasOtlp)
            {
                metrics.AddOtlpExporter(ConfigureOtlp);
            }
        });

    if (hasOtlp)
    {
        builder.Logging.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
            options.AddOtlpExporter(ConfigureOtlp);
        });
    }

    var app = builder.Build();

    // ── 管线顺序：异常收口 → 状态码归一化 → 日志 → 认证 → 限流 → 端点 ──
    if (trustForwardedHeaders)
    {
        app.UseForwardedHeaders();
    }

    app.UseExceptionHandler();
    app.UseMiddleware<ResponseNormalizationMiddleware>();
    app.UseSerilogRequestLogging();
    app.UseCors();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRateLimiter();

    app.MapOpenApi().WithDocumentPerVersion();
    app.MapScalarApiReference();
    app.MapHealthEndpoints();

    var apiVersionSet = app
        .NewApiVersionSet()
        .HasApiVersion(new ApiVersion(1, 0))
        .ReportApiVersions()
        .Build();

    var api = app
        .MapGroup("/api/v{version:apiVersion}")
        .WithApiVersionSet(apiVersionSet)
        .RequireAuthorization();

    api.MapAuthEndpoints();
    api.MapDomainEndpoints();
    api.MapLinkEndpoints();
    api.MapAnalyticsEndpoints();
    api.MapUserEndpoints();
    api.MapSecurityEndpoints();
    api.MapConversionEndpoints();

    // ── MCP：Streamable HTTP 传输 ───────────────────────────
    // 端点要求登录，工具内部再按角色判定（viewer 只能读，写工具要 member 及以上）；
    // 单独限流，按调用者 sub 分区。必须在短码兜底路由之前注册。
    var mcpOptions = builder.Configuration.ReadMcpOptions();
    if (mcpOptions.Enabled)
    {
        app.MapMcp(mcpOptions.Path)
            .RequireAuthorization(AuthorizationSetup.Viewer)
            .RequireRateLimiting(RateLimitingSetup.McpPolicy);
        Log.Information("MCP 端点已启用：{Path}", mcpOptions.Path);
    }

    LogCacheTopology(app);

    // 短码跳转：单段路径，必须在所有前缀路由之后注册。
    app.MapRedirectEndpoints();

    await using (var scope = app.Services.CreateAsyncScope())
    {
        await scope.ServiceProvider
            .GetRequiredService<DatabaseInitializer>()
            .InitializeAsync(app.Lifetime.ApplicationStopping)
            .ConfigureAwait(false);
    }

    Log.Information("ShortLink.Api 启动完成，环境 {Environment}", app.Environment.EnvironmentName);
    await app.RunAsync().ConfigureAwait(false);
}
catch (Exception exception)
{
    Log.Fatal(exception, "ShortLink.Api 启动失败");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}

/// <summary>
/// 启动期把缓存拓扑说清楚。
/// <para>
/// 未配置 Redis 背板时，短链更新/停用/删除只失效<b>本实例</b>的缓存，其他实例要靠 TTL 自然过期。
/// 这个降级不会报错、不会让探针变红，只会静默地让一条已停用的短链在多实例上继续跳转——
/// 所以在这里显式打出来，并且用环境线索决定音量。
/// </para>
/// <para>
/// 音量判据是 <c>KUBERNETES_SERVICE_HOST</c>：容器编排环境下滚动更新就是这个形态，
/// 多副本是常态，值得 Warning；本机 <c>dotnet run</c> 单实例，喊 Warning 只会让人习惯性忽略告警。
/// 这个判据不是精确的（单副本 k8s 也会 Warning），但错在「多说一句」而非「漏报」。
/// </para>
/// </summary>
static void LogCacheTopology(WebApplication app)
{
    var topology = app.Services.GetRequiredService<ICacheTopology>();

    if (topology.HasDistributedBackplane)
    {
        Log.Information("缓存背板：redis，跨实例失效可传播");
        return;
    }

    const string message =
        "缓存背板：in-process。未配置 Cache:RedisConnectionString，短链更新/停用/删除只失效本实例缓存，" +
        "其他实例最长 {TtlSeconds}s 后才看到变更（含停用与删除）。多实例部署必须配置 Redis。";

    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST")))
    {
        Log.Warning(message, topology.LinkTtlSeconds);
    }
    else
    {
        Log.Information(message, topology.LinkTtlSeconds);
    }
}
