using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using ShortLink.Mcp.Options;
using ShortLink.Mcp.Tools;

namespace ShortLink.Mcp;

/// <summary>
/// MCP 入口适配层的装配。
/// <para>
/// 只注册服务，<b>不注册路由</b>：端点映射由宿主（<c>ShortLink.Api</c>）完成，
/// 因为「用哪个授权策略」「挂不挂限流」属于宿主决策，Mcp 程序集不应该反向依赖 Api 的策略常量。
/// </para>
/// </summary>
public static class McpServiceCollectionExtensions
{
    /// <summary>
    /// 读取 <c>Mcp</c> 配置段。供宿主在映射端点前判断是否启用（关闭时不注册任何路由）。
    /// </summary>
    public static McpOptions ReadMcpOptions(this IConfiguration configuration)
    {
        var options = configuration.GetSection(McpOptions.SectionName).Get<McpOptions>() ?? new McpOptions();

        if (string.IsNullOrWhiteSpace(options.Path) || !options.Path.StartsWith('/'))
        {
            options.Path = "/mcp";
        }

        return options;
    }

    /// <summary>
    /// 注册 MCP Server 与全部工具。
    /// <para>
    /// 这里 <b>不</b> 按 <c>Mcp:Enabled</c> 做条件注册：启用开关只控制宿主是否映射端点。
    /// 原因很具体——服务注册发生在配置完全合并之前（容器与测试宿主都会追加配置源），
    /// 在这里读开关会让「容器注入 Mcp__Enabled=true」失效，表现为端点静默消失。
    /// 工具注册本身没有副作用，代价只是一次程序集扫描。
    /// </para>
    /// </summary>
    public static IServiceCollection AddShortLinkMcp(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<McpOptions>(configuration.GetSection(McpOptions.SectionName));

        services
            .AddMcpServer()
            .WithHttpTransport(transport =>
            {
                // 2.x 的默认值就是 Stateless，显式写出是为了让「不维护会话」成为可见的决策：
                // 本服务的工具都是一次性调用，不需要 sampling / elicitation 反向请求，也不需要
                // 为每个 AI 客户端维护长连接会话。代价是无法服务端主动推送。
                transport.Stateless = true;
            })
            .WithToolsFromAssembly(typeof(McpToolRunner).Assembly);

        // 工具类依赖 Application 层的 scoped 用例服务（LinkService 等）。
        // SDK 默认把它们注册为 transient，从 root provider 解析 scoped 依赖会抛
        // "Cannot resolve scoped service from root provider"，因此显式改为 scoped。
        services.AddScoped<McpToolRunner>();
        services.AddScoped<LinkTools>();
        services.AddScoped<AnalyticsTools>();
        services.AddScoped<DomainTools>();

        return services;
    }
}
