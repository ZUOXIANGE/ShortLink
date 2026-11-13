using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using ShortLink.Api.Infrastructure;
using ShortLink.Application.Links;
using ShortLink.Contracts;
using ShortLink.Domain.Links;
using ShortLink.Infrastructure.Persistence;
using ShortLink.Mcp.Tools;
using Xunit;

using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace ShortLink.ArchitectureTests;

/// <summary>入口与共享契约的精确依赖边界，避免只按大层放行后留下旁路。</summary>
public sealed class DependencyBoundaryTests
{
    private static readonly Architecture Architecture = new ArchLoader()
        .LoadAssemblies(
            typeof(ApiResponse).Assembly,
            typeof(Link).Assembly,
            typeof(LinkService).Assembly,
            typeof(ShortLinkDbContext).Assembly,
            typeof(HttpCurrentUser).Assembly,
            typeof(McpToolRunner).Assembly)
        .Build();

    [Fact]
    public void Domain_Should_Not_Depend_On_AspNetCore()
    {
        IArchRule rule = Types()
            .That().ResideInNamespaceMatching("^ShortLink\\.Domain.*")
            .Should().NotDependOnAny(Types().That()
                .ResideInNamespaceMatching("^Microsoft\\.AspNetCore.*"))
            .Because("领域模型不能依赖 HTTP 宿主、MVC 或身份认证类型");

        rule.Check(Architecture);
    }

    [Fact]
    public void Contracts_Should_Not_Depend_On_Mcp()
    {
        IArchRule rule = Types()
            .That().ResideInNamespaceMatching("^ShortLink\\.Contracts.*")
            .Should().NotDependOnAny(Types().That()
                .ResideInNamespaceMatching("^ShortLink\\.Mcp.*"))
            .Because("共享契约必须保持入口无关，不能反向依赖 MCP 适配层");

        rule.Check(Architecture);
    }

    [Fact]
    public void Mcp_Should_Not_Depend_On_Application_Persistence_Abstractions()
    {
        IArchRule rule = Types()
            .That().ResideInNamespaceMatching("^ShortLink\\.Mcp.*")
            .Should().NotDependOnAny(Types().That()
                .ResideInNamespaceMatching("^ShortLink\\.Application\\.Abstractions\\.Persistence.*"))
            .Because("MCP 只能调用应用用例，直接依赖持久化端口同样会绕过权限与工作区边界");

        rule.Check(Architecture);
    }
}
