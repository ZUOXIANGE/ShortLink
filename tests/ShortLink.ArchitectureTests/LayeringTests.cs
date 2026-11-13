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

/// <summary>把分层约束固化成可执行规则，架构变更与规则变更必须同一次提交。</summary>
public sealed class LayeringTests
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

    private static readonly IObjectProvider<IType> EntityFrameworkTypes =
        Types().That().ResideInNamespaceMatching("^Microsoft\\.EntityFrameworkCore.*").As("EF Core 类型");

    private static readonly IObjectProvider<IType> ApplicationForbiddenTypes = Types()
        .That().ResideInNamespaceMatching("^Microsoft\\.EntityFrameworkCore.*")
        .Or().ResideInNamespaceMatching("^Microsoft\\.AspNetCore\\.Http.*")
        .As("EF Core 与 ASP.NET Core HTTP 类型");

    [Fact]
    public void Domain_Should_Not_Depend_On_EntityFramework()
    {
        IArchRule rule = Types()
            .That().ResideInNamespaceMatching("^ShortLink\\.Domain.*")
            .Should().NotDependOnAny(EntityFrameworkTypes)
            .Because("领域层必须保持零框架依赖，便于单元测试与长期演进");

        rule.Check(Architecture);
    }

    [Fact]
    public void Application_Should_Not_Depend_On_EntityFramework_Or_AspNetCore()
    {
        IArchRule rule = Types()
            .That().ResideInNamespaceMatching("^ShortLink\\.Application.*")
            .Should().NotDependOnAny(ApplicationForbiddenTypes)
            .Because("应用层只依赖端口接口，持久化与传输细节属于基础设施与宿主");

        rule.Check(Architecture);
    }

    [Fact]
    public void Contracts_Should_Not_Depend_On_Server_Side_Layers()
    {
        IArchRule rule = Types()
            .That().ResideInNamespaceMatching("^ShortLink\\.Contracts.*")
            .Should().NotDependOnAny(Types().That()
                .ResideInNamespaceMatching("^ShortLink\\.(Domain|Application|Infrastructure|Api).*"))
            .Because("Contracts 由后端与 Blazor WASM 共享，不能引用服务端实现");

        rule.Check(Architecture);
    }

    [Fact]
    public void Api_Should_Not_Reference_ProblemDetails()
    {
        IArchRule rule = Types()
            .That().ResideInNamespaceMatching("^ShortLink\\.Api.*")
            .Should().NotDependOnAny(Types().That().HaveFullNameMatching(".*ProblemDetails.*"))
            .Because("默认契约是 HTTP 200 + 统一信封，混用 ProblemDetails 会产生两套错误结构");

        rule.Check(Architecture);
    }

    [Fact]
    public void Infrastructure_Should_Not_Depend_On_Api()
    {
        IArchRule rule = Types()
            .That().ResideInNamespaceMatching("^ShortLink\\.Infrastructure.*")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching("^ShortLink\\.Api.*"))
            .Because("依赖方向必须单向：Api → Infrastructure → Application → Domain");

        rule.Check(Architecture);
    }

    [Fact]
    public void Mcp_Should_Not_Depend_On_Persistence_Or_Host()
    {
        IArchRule rule = Types()
            .That().ResideInNamespaceMatching("^ShortLink\\.Mcp.*")
            .Should().NotDependOnAny(Types().That()
                .ResideInNamespaceMatching("^ShortLink\\.(Infrastructure|Api).*"))
            .Because("MCP 是又一个入口适配层，只能调 Application 用例；直接碰持久化会绕过工作区隔离与角色校验，"
                + "与 HTTP 端点出现两套权限口径");

        rule.Check(Architecture);
    }

    [Fact]
    public void Mcp_Tools_Should_Not_Touch_DbContext()
    {
        IArchRule rule = Types()
            .That().ResideInNamespaceMatching("^ShortLink\\.Mcp\\.Tools.*")
            .Should().NotDependOnAny(Types().That().HaveFullNameMatching(".*ShortLinkDbContext.*"))
            .Because("工具方法只做协议转换 + 参数校验 + 调应用层；查库逻辑出现在工具里必然漏掉工作区过滤");

        rule.Check(Architecture);
    }
}
