using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ShortLink.Mcp.Diagnostics;

/// <summary>
/// MCP 工具调用的可观测性埋点。
/// <para>
/// 必须独立于 HTTP 指标：Streamable HTTP 传输下每次工具调用都是 <c>POST /mcp</c>，
/// HTTP 层面永远是 200，看状态码完全分不出工具是成功还是失败。判定只能靠
/// <c>mcp.tool.is_error</c> 标签，这与本项目「全 200 信封」的补偿思路是一致的。
/// </para>
/// </summary>
public static class McpDiagnostics
{
    public const string ActivitySourceName = "ShortLink.Mcp";

    public const string MeterName = "ShortLink.Mcp";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>工具调用总数，标签 <c>tool</c> / <c>is_error</c> / <c>role</c>。</summary>
    public static readonly Counter<long> ToolCalls = Meter.CreateCounter<long>(
        "shortlink.mcp.tool_calls",
        unit: "{call}",
        description: "MCP 工具调用次数（按工具名与成败分组）");

    /// <summary>工具调用耗时，标签 <c>tool</c>。</summary>
    public static readonly Histogram<double> ToolDuration = Meter.CreateHistogram<double>(
        "shortlink.mcp.tool_duration",
        unit: "ms",
        description: "MCP 工具执行耗时");

    /// <summary>工具被拒绝的次数（角色不足 / 未认证 / 越权访问他人短链）。</summary>
    public static readonly Counter<long> ToolRejections = Meter.CreateCounter<long>(
        "shortlink.mcp.tool_rejections",
        unit: "{call}",
        description: "MCP 工具鉴权拒绝次数");
}
