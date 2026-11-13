using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ShortLink.Contracts.Auth;
using ShortLink.Domain.Users;
using Xunit;

namespace ShortLink.IntegrationTests;

/// <summary>
/// MCP 端点测试：用真实 <see cref="McpClient"/> 打自己的服务端。
/// <para>
/// 刻意不 mock 协议层——MCP 的问题几乎都出在「协议行为」而不是「业务逻辑」：
/// 鉴权头有没有带上、错误到底是 <c>IsError</c> 还是 JSON-RPC error、
/// scoped 依赖能不能在无会话模式下解析、结构化输出能不能反序列化。
/// 这些只有真跑一遍才知道。
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class McpToolTests(ApiFactory factory)
{
    private const string AdminPassword = "Admin@123456";

    [Fact]
    public async Task Tools_Should_Be_Discoverable()
    {
        await using var client = await CreateClientAsync("admin", AdminPassword);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var names = tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(
            [
                "shortlink_create",
                "shortlink_dashboard",
                "shortlink_delete",
                "shortlink_get",
                "shortlink_list",
                "shortlink_list_domains",
                "shortlink_recent_clicks",
                "shortlink_set_enabled",
                "shortlink_stats",
            ],
            names);

        // 描述是模型选择工具的唯一依据，必须写到「做什么 / 有无副作用」而不是只写名字。
        var create = tools.Single(t => t.Name == "shortlink_create");
        Assert.Contains("创建", create.Description, StringComparison.Ordinal);

        var delete = tools.Single(t => t.Name == "shortlink_delete");
        Assert.Contains("不可恢复", delete.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_Get_And_Stats_Should_Work_End_To_End()
    {
        await using var client = await CreateClientAsync("admin", AdminPassword);
        var code = $"mcp{Guid.NewGuid():N}"[..8];

        var created = await client.CallToolAsync(
            "shortlink_create",
            new Dictionary<string, object>
            {
                ["targetUrl"] = "https://example.com/mcp-e2e",
                ["code"] = code,
                ["title"] = "MCP 端到端用例",
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(true, created.IsError);
        using var createdJson = JsonDocument.Parse(TextOf(created));
        var shortUrl = createdJson.RootElement.GetProperty("shortUrl").GetString();
        Assert.False(string.IsNullOrWhiteSpace(shortUrl));
        Assert.Equal(code, createdJson.RootElement.GetProperty("code").GetString());

        // 按短链地址反查：外部集成与模型手里通常只有 URL，没有数据库 ID。
        var fetched = await client.CallToolAsync(
            "shortlink_get",
            new Dictionary<string, object> { ["shortUrl"] = shortUrl! },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(true, fetched.IsError);
        using var fetchedJson = JsonDocument.Parse(TextOf(fetched));
        Assert.Equal(code, fetchedJson.RootElement.GetProperty("code").GetString());

        var stats = await client.CallToolAsync(
            "shortlink_stats",
            new Dictionary<string, object> { ["shortUrl"] = shortUrl!, ["days"] = 7 },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(true, stats.IsError);
        using var statsJson = JsonDocument.Parse(TextOf(stats));
        Assert.Equal(code, statsJson.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Business_Failure_Should_Set_IsError_With_BizCode()
    {
        await using var client = await CreateClientAsync("admin", AdminPassword);

        var result = await client.CallToolAsync(
            "shortlink_get",
            new Dictionary<string, object> { ["linkId"] = 987654321L },
            cancellationToken: TestContext.Current.CancellationToken);

        // 这是 MCP 最容易踩的坑：业务失败若被当成成功返回（HTTP 风格的信封），
        // 模型会拿着 {"code":13001} 继续推理并给出错误结论。
        Assert.True(result.IsError is true);
        Assert.Contains("[13001]", TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Viewer_Should_Be_Able_To_Read_But_Not_Create()
    {
        var admin = factory.CreateClient();
        var adminToken = await LoginAsync(admin, "admin", AdminPassword);
        var viewerName = $"mcpv{Guid.NewGuid():N}"[..10];

        admin.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
        var createUser = await admin.PostAsJsonAsync("/api/v1/users", new ShortLink.Contracts.Users.CreateUserRequest
        {
            UserName = viewerName,
            DisplayName = "MCP 只读账号",
            Password = "Viewer@123456",
            Role = UserRoles.Viewer,
        });
        var envelope = await createUser.Content.ReadFromJsonAsync<ShortLink.Contracts.ApiResponse<ShortLink.Contracts.Users.ManagedUserDto>>(ApiFactory.JsonOptions);
        Assert.True(envelope!.Code == ShortLink.Contracts.BizCodes.Success, envelope.Msg);

        await using var client = await CreateClientAsync(viewerName, "Viewer@123456");

        // 读：放行。
        var list = await client.CallToolAsync(
            "shortlink_list",
            new Dictionary<string, object> { ["pageSize"] = 5 },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(true, list.IsError);

        // 写：必须 IsError + 40301。端点只校验「已登录」，角色判定在工具层。
        var create = await client.CallToolAsync(
            "shortlink_create",
            new Dictionary<string, object> { ["targetUrl"] = "https://example.com/should-not-exist" },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(create.IsError is true);
        Assert.Contains("[40301]", TextOf(create), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_Argument_Should_Set_IsError_With_InvalidArgument_Code()
    {
        await using var client = await CreateClientAsync("admin", AdminPassword);

        var result = await client.CallToolAsync(
            "shortlink_create",
            new Dictionary<string, object> { ["targetUrl"] = "   " },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError is true);
        Assert.Contains("[40001]", TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Endpoint_Should_Reject_Anonymous_With_Real_401()
    {
        // 断言真实状态码而不是「客户端抛异常」：早期实现里 401 被 JWT challenge 直接写成
        // 200 + 信封，MCP 客户端依然会报错，但错误原因是「响应无法解析」而不是「未认证」，
        // 客户端也就不会去重新认证。必须钉住 401 与 WWW-Authenticate 头。
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2026-07-28","capabilities":{},"clientInfo":{"name":"probe","version":"1.0"}}}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");

        var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(
            response.Headers.Contains("WWW-Authenticate"),
            "401 必须带 WWW-Authenticate，MCP 客户端据此触发重新认证与资源发现");
    }

    [Fact]
    public void View_Role_Names_Should_Match()
    {
        // 工具里用 ToDisplayName() 拼提示文案，若枚举与显示名脱节，错误信息会变成「需要「」及以上角色」。
        Assert.Equal("成员", UserRole.Member.ToDisplayName());
        Assert.Equal("只读", UserRole.Viewer.ToDisplayName());
    }

    [Fact]
    public async Task Set_Enabled_Should_Be_Idempotent()
    {
        await using var client = await CreateClientAsync("admin", AdminPassword);
        var code = $"mcp{Guid.NewGuid():N}"[..8];

        var created = await client.CallToolAsync(
            "shortlink_create",
            new Dictionary<string, object>
            {
                ["targetUrl"] = "https://example.com/mcp-toggle",
                ["code"] = code,
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(true, created.IsError);
        using var createdJson = JsonDocument.Parse(TextOf(created));
        var linkId = createdJson.RootElement.GetProperty("id").GetInt64();
        Assert.Equal("enabled", createdJson.RootElement.GetProperty("status").GetString());

        // 连续两次「停用」：若工具走的是 toggle，第二次会把链接翻回启用。
        for (var i = 0; i < 2; i++)
        {
            var disable = await client.CallToolAsync(
                "shortlink_set_enabled",
                new Dictionary<string, object> { ["linkId"] = linkId, ["enabled"] = false },
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotEqual(true, disable.IsError);
            using var disableJson = JsonDocument.Parse(TextOf(disable));
            Assert.Equal("disabled", disableJson.RootElement.GetProperty("status").GetString());
        }

        var enable = await client.CallToolAsync(
            "shortlink_set_enabled",
            new Dictionary<string, object> { ["linkId"] = linkId, ["enabled"] = true },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(true, enable.IsError);
        using var enableJson = JsonDocument.Parse(TextOf(enable));
        Assert.Equal("enabled", enableJson.RootElement.GetProperty("status").GetString());
    }

    private async Task<McpClient> CreateClientAsync(string userName, string password)
    {
        var token = await LoginAsync(factory.CreateClient(), userName, password);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/mcp"),
                Name = $"shortlink-tests-{userName}",
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {token}",
                },
            },
            factory.CreateClient(),
            NullLoggerFactory.Instance);

        return await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static string TextOf(CallToolResult result)
        => string.Join('\n', result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private static async Task<string> LoginAsync(HttpClient client, string userName, string password)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            UserName = userName,
            Password = password,
        });

        var envelope = await response.Content.ReadFromJsonAsync<ShortLink.Contracts.ApiResponse<LoginResponse>>(ApiFactory.JsonOptions);
        Assert.True(envelope!.Code == ShortLink.Contracts.BizCodes.Success, $"登录失败（{userName}）：{envelope.Msg}");
        return envelope.Data!.AccessToken;
    }
}
