using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using ShortLink.Contracts;
using ShortLink.Infrastructure.Diagnostics;
using ShortLink.Mcp.Options;

namespace ShortLink.Api.Infrastructure;

/// <summary>
/// 全局唯一异常收口：所有异常转成 HTTP 200 + 统一信封，未处理异常只记 Error 日志并返回固定兜底文案。
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (code, msg, errors) = Map(httpContext, exception);

        if (code == BizCodes.InternalError)
        {
            logger.LogError(exception, "未处理异常，traceId={TraceId}", httpContext.TraceIdentifier);
        }
        else
        {
            logger.LogWarning(
                "业务失败 code={Code}, path={Path}, traceId={TraceId}",
                code,
                httpContext.Request.Path,
                httpContext.TraceIdentifier);
        }

        if (code != BizCodes.Success)
        {
            Activity.Current?.SetStatus(ActivityStatusCode.Error, msg);
            Activity.Current?.SetTag("business.code", code);
            ShortLinkDiagnostics.BusinessFailures.Add(1, new KeyValuePair<string, object?>("code", code));
        }

        if (httpContext.Response.HasStarted)
        {
            return false;
        }

        if (IsMcpRequest(httpContext))
        {
            httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
            httpContext.Response.ContentType = "application/json; charset=utf-8";
            await httpContext.Response.WriteAsJsonAsync(new
            {
                jsonrpc = "2.0",
                id = (string?)null,
                error = new { code = -32603, message = "Internal error" },
            }, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (IsRedirectOrProbe(httpContext.Request.Path, out var isRedirect))
        {
            httpContext.Response.StatusCode = code == BizCodes.InvalidArgument
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status500InternalServerError;
            httpContext.Response.ContentType = isRedirect
                ? "text/html; charset=utf-8"
                : "text/plain; charset=utf-8";
            await httpContext.Response.WriteAsync(
                isRedirect
                    ? Endpoints.RedirectPageRenderer.ErrorPage("暂时无法访问", "系统繁忙，请稍后重试。")
                    : "Service unavailable",
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        httpContext.Response.Headers["X-Biz-Code"] = code.ToString(System.Globalization.CultureInfo.InvariantCulture);
        httpContext.Response.ContentType = "application/json; charset=utf-8";
        await httpContext.Response
            .WriteAsJsonAsync(ApiResponse.Fail(code, msg, errors), cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    private static bool IsMcpRequest(HttpContext context)
    {
        var options = context.RequestServices.GetService<Microsoft.Extensions.Options.IOptions<McpOptions>>()?.Value;
        return options is { Enabled: true }
               && context.Request.Path.StartsWithSegments(options.Path, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRedirectOrProbe(PathString path, out bool isRedirect)
    {
        if (path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
        {
            isRedirect = false;
            return true;
        }

        var value = path.Value?.Trim('/');
        isRedirect = !string.IsNullOrEmpty(value)
                     && !value.Contains('/')
                     && !path.StartsWithSegments("/scalar", StringComparison.OrdinalIgnoreCase)
                     && !path.StartsWithSegments("/openapi", StringComparison.OrdinalIgnoreCase)
                     && !path.StartsWithSegments("/favicon", StringComparison.OrdinalIgnoreCase);
        return isRedirect;
    }

    private static (int Code, string Msg, IReadOnlyDictionary<string, string[]>? Errors) Map(
        HttpContext httpContext,
        Exception exception)
        => exception switch
        {
            BusinessException business => (business.Code, business.Message, business.Errors),

            // 注意：必须限定命名空间，System.ComponentModel.DataAnnotations 有同名异常
            FluentValidation.ValidationException validation => (
                BizCodes.InvalidArgument,
                "参数校验失败",
                validation.Errors
                    .GroupBy(e => string.IsNullOrEmpty(e.PropertyName) ? "_" : e.PropertyName)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(e => e.ErrorMessage).Distinct().ToArray(),
                        StringComparer.Ordinal)),

            BadHttpRequestException badRequest => (
                BizCodes.InvalidArgument,
                string.IsNullOrWhiteSpace(badRequest.Message) ? "请求体格式不正确" : badRequest.Message,
                null),

            DbUpdateConcurrencyException => (BizCodes.Conflict, "数据已被修改，请重试", null),

            DbUpdateException => (BizCodes.Conflict, "数据写入冲突，请检查是否重复提交", null),

            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested => (
                BizCodes.ClientClosedRequest,
                "请求已取消",
                null),

            _ => (BizCodes.InternalError, "系统繁忙，请稍后重试", null),
        };
}
