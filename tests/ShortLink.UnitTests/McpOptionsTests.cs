using Microsoft.Extensions.Configuration;
using ShortLink.Mcp;
using ShortLink.Mcp.Options;
using Xunit;

namespace ShortLink.UnitTests;

/// <summary>
/// MCP 配置归一化。
/// <para>
/// 这些断言防的是「配置写错但没人发现」：路径少了前导 <c>/</c> 时，
/// <c>ResponseNormalizationMiddleware</c> 的白名单会匹配不到该路径，
/// 结果 MCP 端点的 401 被归一化成 200 信封，客户端表现为「连上了但没有工具」——排查成本很高。
/// </para>
/// </summary>
public sealed class McpOptionsTests
{
    private const string DefaultPath = "/mcp";

    private static IConfiguration Build(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Enabled_Should_Default_To_False()
    {
        var options = Build([]).ReadMcpOptions();

        // 默认关闭是刻意的：MCP 等于一个可被 AI 调用的写入口，升级不应自动打开。
        Assert.False(options.Enabled);
        Assert.Equal(DefaultPath, options.Path);
        Assert.True(options.PermitPerMinute > 0);
    }

    [Fact]
    public void Enabled_Should_Be_Read_From_Configuration()
    {
        var options = Build(new Dictionary<string, string?>
        {
            ["Mcp:Enabled"] = "true",
            ["Mcp:Path"] = "/ai-tools",
            ["Mcp:PermitPerMinute"] = "30",
        }).ReadMcpOptions();

        Assert.True(options.Enabled);
        Assert.Equal("/ai-tools", options.Path);
        Assert.Equal(30, options.PermitPerMinute);
    }

    [Theory]
    [InlineData("mcp")]
    [InlineData("")]
    [InlineData("   ")]
    public void Invalid_Path_Should_Fall_Back_To_Default(string configured)
    {
        var options = Build(new Dictionary<string, string?>
        {
            ["Mcp:Enabled"] = "true",
            ["Mcp:Path"] = configured,
        }).ReadMcpOptions();

        Assert.Equal(DefaultPath, options.Path);
    }

    [Fact]
    public void Section_Name_Should_Stay_In_Sync_With_AppSettings()
    {
        // 段名被改掉时容器里的 Mcp__Enabled 会静默失效，端点凭空消失。
        Assert.Equal("Mcp", McpOptions.SectionName);
    }
}
