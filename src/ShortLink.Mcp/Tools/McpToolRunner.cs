using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ShortLink.Application.Abstractions;
using ShortLink.Contracts;
using ShortLink.Domain.Users;
using ShortLink.Mcp.Diagnostics;

namespace ShortLink.Mcp.Tools;

/// <summary>
/// 工具执行外壳：鉴权 → 执行 → 异常映射 → 埋点与审计。
/// <para>
/// 存在的理由是两个「不能漏」的边界：
/// </para>
/// <list type="number">
/// <item>
/// <b>错误语义</b>：HTTP 侧靠 <c>IExceptionHandler</c> 把 <see cref="BusinessException"/> 转成信封，
/// 但那个处理器只在 HTTP 管线里生效，<b>管不到工具方法</b>。若让业务异常裸抛，
/// 客户端只会收到 "An error occurred invoking 'xxx'."，业务码与可操作提示全部丢失，
/// 模型也不知道该改参数还是该换工具。因此必须在这里映射为
/// <see cref="McpException"/>（→ <c>IsError = true</c>）并保留业务码。
/// </item>
/// <item>
/// <b>工具级鉴权</b>：端点上的 <c>RequireAuthorization</c> 只保证「有令牌」，
/// 不区分只读账号与可写账号。角色判断必须落到每个工具上，与 HTTP 端点用同一套
/// <see cref="UserRoles.AtLeast"/> 口径。
/// </item>
/// </list>
/// </summary>
public sealed class McpToolRunner(ICurrentUser user, ILogger<McpToolRunner> logger)
{
    public async Task<T> RunAsync<T>(
        string toolName,
        UserRole requiredRole,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        using var activity = McpDiagnostics.ActivitySource.StartActivity(toolName, ActivityKind.Server);
        activity?.SetTag("mcp.tool.name", toolName);
        activity?.SetTag("mcp.actor", user.UserName ?? "anonymous");

        var started = Stopwatch.GetTimestamp();

        try
        {
            EnsureAuthorized(toolName, requiredRole);

            var result = await action(cancellationToken).ConfigureAwait(false);

            Record(toolName, isError: false, started, activity);
            return result;
        }
        catch (BusinessException exception)
        {
            // 业务失败 → IsError = true。文案里保留业务码，模型据此判断是「参数错」还是「无权限」。
            Record(toolName, isError: true, started, activity, exception.Code);
            throw new McpException($"[{exception.Code}] {exception.Message}");
        }
        catch (OperationCanceledException)
        {
            // 客户端取消不是错误，但也要留痕，否则会表现为「工具没反应」。
            activity?.SetTag("mcp.tool.is_error", true);
            activity?.SetTag("mcp.tool.cancelled", true);
            throw;
        }
        catch (Exception exception)
        {
            // 未预期异常：完整堆栈只进日志，响应只给固定文案（错误文本会展示给用户与模型）。
            logger.LogError(exception, "MCP 工具 {Tool} 执行失败", toolName);
            Record(toolName, isError: true, started, activity);
            throw new McpException("工具执行失败，请稍后重试。若持续失败请联系管理员。");
        }
    }

    /// <summary>参数层面的前置校验。失败按业务码返回，让模型能自行纠正而不是整轮重来。</summary>
    public static void EnsureArgument(bool condition, string message)
    {
        if (!condition)
        {
            throw new BusinessException(BizCodes.InvalidArgument, message);
        }
    }

    private void EnsureAuthorized(string toolName, UserRole requiredRole)
    {
        if (!user.IsAuthenticated)
        {
            McpDiagnostics.ToolRejections.Add(1, new KeyValuePair<string, object?>("tool", toolName));
            throw new BusinessException(BizCodes.Unauthorized, "MCP 调用需要携带有效的访问令牌（Bearer）");
        }

        if (!user.AtLeast(requiredRole))
        {
            McpDiagnostics.ToolRejections.Add(1, new KeyValuePair<string, object?>("tool", toolName));
            throw new BusinessException(
                BizCodes.Forbidden,
                $"该工具需要「{requiredRole.ToDisplayName()}」及以上角色，当前角色为「{user.RoleValue.ToDisplayName()}」");
        }
    }

    private void Record(string toolName, bool isError, long started, Activity? activity, int? bizCode = null)
    {
        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        activity?.SetTag("mcp.tool.is_error", isError);
        if (bizCode is { } code)
        {
            activity?.SetTag("mcp.biz_code", code);
        }

        // 用完即弃的标签数组：TagList 是 struct，不产生堆分配。
        var tags = new TagList
        {
            { "tool", toolName },
            { "is_error", isError },
        };

        McpDiagnostics.ToolCalls.Add(1, tags);
        McpDiagnostics.ToolDuration.Record(elapsedMs, new KeyValuePair<string, object?>("tool", toolName));

        if (isError)
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning(
                    "MCP 工具 {Tool} 失败：用户 {User}，业务码 {BizCode}，耗时 {Elapsed:F0}ms",
                    toolName,
                    user.UserName ?? "anonymous",
                    bizCode,
                    elapsedMs);
            }
        }
        else if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "MCP 工具 {Tool} 成功：用户 {User}，耗时 {Elapsed:F0}ms",
                toolName,
                user.UserName ?? "anonymous",
                elapsedMs);
        }
    }
}
