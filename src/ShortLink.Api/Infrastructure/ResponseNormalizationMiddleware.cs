using Microsoft.Extensions.Options;
using ShortLink.Contracts;
using ShortLink.Mcp.Options;

namespace ShortLink.Api.Infrastructure;

/// <summary>
/// 框架层状态码归一化：404 / 405 / 415 等由框架直接产生的结果也要转成统一信封。
/// 白名单排除探针、OpenAPI 文档、静态资源、短码跳转与 MCP 端点。
/// </summary>
public sealed class ResponseNormalizationMiddleware(RequestDelegate next, IOptions<McpOptions> mcpOptions)
{
    private static readonly string[] ExcludedPrefixes =
    [
        "/health",
        "/scalar",
        "/openapi",
        "/_framework",
        "/_content",
        "/_blazor",
        "/favicon",
    ];

    /// <summary>
    /// MCP 端点必须比 HTTP API 更「像协议」：客户端靠 401 触发重新认证、
    /// 靠 JSON-RPC 错误体判断调用是否成功。把它归一化成 200 + 业务信封，
    /// 客户端会认为请求成功却又解析不出 JSON-RPC 响应，表现为「连上了但没有工具」。
    /// 路径来自配置，改 <c>Mcp:Path</c> 时不需要同步改这里。
    /// </summary>
    private readonly string? _mcpPath = mcpOptions.Value.Enabled ? mcpOptions.Value.Path : null;

    public async Task InvokeAsync(HttpContext context)
    {
        var isApi = context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
        if (isApi)
        {
            context.Response.OnStarting(() =>
            {
                if (context.Response.StatusCode >= 400
                    && context.Response.Headers["X-Biz-Code"] == BizCodes.Success.ToString(System.Globalization.CultureInfo.InvariantCulture))
                {
                    var mapped = MapFrameworkCode(context.Response.StatusCode, context.Request.Path);
                    var errorCode = mapped == 0 ? BizCodes.InternalError : mapped;
                    context.Response.Headers["X-Biz-Code"] = errorCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                return Task.CompletedTask;
            });
            context.Response.Headers["X-Biz-Code"] = BizCodes.Success.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        await next(context).ConfigureAwait(false);

        if (context.Response.HasStarted || IsExcluded(context.Request.Path))
        {
            return;
        }

        var code = MapFrameworkCode(context.Response.StatusCode, context.Request.Path);

        if (code == 0)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers["X-Biz-Code"] = code.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response
            .WriteAsJsonAsync(ApiResponse.Fail(code, BizCodes.Describe(code)), context.RequestAborted)
            .ConfigureAwait(false);
    }

    private static int MapFrameworkCode(int statusCode, PathString path)
        => statusCode switch
        {
            StatusCodes.Status400BadRequest => BizCodes.InvalidArgument,
            StatusCodes.Status401Unauthorized => BizCodes.Unauthorized,
            StatusCodes.Status403Forbidden => BizCodes.Forbidden,
            StatusCodes.Status404NotFound when IsUnsupportedVersion(path) => BizCodes.ApiVersionUnsupported,
            StatusCodes.Status404NotFound => BizCodes.NotFound,
            StatusCodes.Status405MethodNotAllowed => BizCodes.NotFound,
            StatusCodes.Status415UnsupportedMediaType => BizCodes.InvalidArgument,
            StatusCodes.Status429TooManyRequests => BizCodes.RateLimited,
            >= 500 => BizCodes.InternalError,
            _ => 0,
        };

    private static bool IsUnsupportedVersion(PathString path)
    {
        var segments = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments is ["api", { } version, ..]
               && version.Length > 1
               && version[0] is 'v' or 'V'
               && int.TryParse(version.AsSpan(1), out var number)
               && number != 1;
    }

    private bool IsExcluded(PathString path)
    {
        foreach (var prefix in ExcludedPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (_mcpPath is not null && path.StartsWithSegments(_mcpPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        // 单段路径（如 /abc123）是短码跳转，错误页由跳转端点自行渲染 HTML，不能再套信封。
        var trimmed = value.Trim('/');
        return trimmed.Length > 0 && !trimmed.Contains('/');
    }
}
