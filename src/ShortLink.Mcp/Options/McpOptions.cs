namespace ShortLink.Mcp.Options;

/// <summary>
/// MCP Server 配置。
/// <para>
/// <see cref="Enabled"/> <b>默认 false</b>：MCP 是新增的网络暴露面（一个能被 AI 客户端调用的写入口），
/// 升级到本版本时不应自动打开。要启用必须显式配置 <c>Mcp:Enabled=true</c>。
/// </para>
/// </summary>
public sealed class McpOptions
{
    public const string SectionName = "Mcp";

    /// <summary>是否挂载 MCP 端点。关闭时 <c>/mcp</c> 完全不存在（不是返回 404 信封，而是没有路由）。</summary>
    public bool Enabled { get; set; }

    /// <summary>端点路径，默认 <c>/mcp</c>。必须以 <c>/</c> 开头。</summary>
    public string Path { get; set; } = "/mcp";

    /// <summary>
    /// 单个调用者每分钟允许的工具调用次数。按 <c>sub</c> 分区（未带令牌时回落到 IP），
    /// 避免 AI 侧的批量调用把主库打满。
    /// </summary>
    public int PermitPerMinute { get; set; } = 120;
}
