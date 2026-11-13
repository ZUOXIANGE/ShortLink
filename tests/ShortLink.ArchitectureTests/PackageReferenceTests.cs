using System.Xml.Linq;
using Xunit;

namespace ShortLink.ArchitectureTests;

/// <summary>
/// 包引用层面的约束：Aspire 只允许出现在 <c>ShortLink.AppHost</c>。
/// <para>
/// ArchUnitNET 看的是已编译的类型依赖，而「引了编排包但还没用」在类型层面是隐形的。
/// 这条边界一旦越过去，应用产物就绑死在 Aspire 的启动方式上：换 compose / k8s 起不来，
/// 线上问题也没法脱离 Aspire 复现。所以在 csproj 上直接卡。
/// </para>
/// </summary>
public sealed class PackageReferenceTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Theory]
    [InlineData("src/ShortLink.Domain/ShortLink.Domain.csproj")]
    [InlineData("src/ShortLink.Contracts/ShortLink.Contracts.csproj")]
    [InlineData("src/ShortLink.Application/ShortLink.Application.csproj")]
    [InlineData("src/ShortLink.Infrastructure/ShortLink.Infrastructure.csproj")]
    [InlineData("src/ShortLink.Mcp/ShortLink.Mcp.csproj")]
    [InlineData("src/ShortLink.Api/ShortLink.Api.csproj")]
    [InlineData("src/ShortLink.Web/ShortLink.Web.csproj")]
    public void Application_Projects_Should_Not_Reference_Aspire_Packages(string relativePath)
    {
        var project = XDocument.Load(Path.Combine(RepositoryRoot, relativePath));

        string[] aspirePackages = project.Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => (string?)element.Attribute("Include"))
            .OfType<string>()
            .Where(id => id.StartsWith("Aspire.", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            aspirePackages.Length == 0,
            $"{relativePath} 引用了 Aspire 包：{string.Join(", ", aspirePackages)}。" +
            "编排属于 AppHost 的职责，应用侧只能通过 IConfiguration 接收显式配置键。");
    }

    [Fact]
    public void AppHost_Should_Own_The_Orchestration_Packages()
    {
        var project = XDocument.Load(
            Path.Combine(RepositoryRoot, "src/ShortLink.AppHost/ShortLink.AppHost.csproj"));

        string[] packages = project.Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => (string?)element.Attribute("Include"))
            .OfType<string>()
            .ToArray();

        Assert.Contains(packages, id => id.StartsWith("Aspire.Hosting.", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "ShortLink.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException("找不到仓库根目录（未找到 ShortLink.slnx）");
    }
}
