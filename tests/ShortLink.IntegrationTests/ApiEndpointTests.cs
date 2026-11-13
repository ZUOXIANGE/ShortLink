using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using ShortLink.Contracts;
using ShortLink.Contracts.Auth;
using ShortLink.Contracts.Links;

namespace ShortLink.IntegrationTests;

/// <summary>
/// 端到端宿主：SQLite 临时库 + 真实迁移，覆盖登录、鉴权归一化、短链创建与跳转。
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"shortlink-it-{Guid.NewGuid():N}.db");

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:ConnectionString"] = $"Data Source={_databasePath}",
                ["Jwt:SigningKey"] = "integration-tests-signing-key-32-bytes-minimum",
                ["Seed:AdminUserName"] = "admin",
                ["Seed:AdminPassword"] = "Admin@123456",
                ["Seed:DefaultDomainHost"] = "localhost",
                ["Seed:DefaultDomainScheme"] = "http",
                ["Cache:RedisConnectionString"] = string.Empty,
                ["OpenTelemetry:Endpoint"] = string.Empty,
                ["RateLimit:AuthPermitPerMinute"] = "1000",
                ["RateLimit:ConversionPermitPerMinute"] = "1000",
                ["RateLimit:AbusePermitPerMinute"] = "1000",
                // MCP 默认关闭（生产要显式开启），测试宿主里打开以便用真实客户端打通端点。
                ["Mcp:Enabled"] = "true",
                ["Mcp:Path"] = "/mcp",
                ["Mcp:PermitPerMinute"] = "1000",
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        try
        {
            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }
        catch (IOException)
        {
            // 临时文件清理失败不影响测试结论。
        }
    }
}

/// <summary>
/// 两个测试类共用同一个宿主：Serilog 的静态 Logger 在每个进程中只能 freeze 一次，
/// 多建一个 <see cref="ApiFactory"/> 会抛 “The logger is already frozen”。
/// </summary>
[CollectionDefinition(ApiCollection.Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}

[Collection(ApiCollection.Name)]
public sealed class ApiEndpointTests(ApiFactory factory)
{
    [Fact]
    public async Task Unauthenticated_Request_Should_Return_Envelope_With_Unauthorized_Code()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/links");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<JsonElement>>(ApiFactory.JsonOptions);
        Assert.NotNull(envelope);
        Assert.Equal(BizCodes.Unauthorized, envelope!.Code);
        Assert.Equal(JsonValueKind.Null, envelope.Data.ValueKind);
    }

    [Fact]
    public async Task Login_With_Default_Admin_Should_Return_Token()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            UserName = "admin",
            Password = "Admin@123456",
        });

        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>(ApiFactory.JsonOptions);

        Assert.NotNull(envelope);
        Assert.Equal(BizCodes.Success, envelope!.Code);
        Assert.NotNull(envelope.Data);
        Assert.False(string.IsNullOrWhiteSpace(envelope.Data!.AccessToken));
        Assert.Equal("admin", envelope.Data.User.UserName);
    }

    [Fact]
    public async Task Login_With_Wrong_Password_Should_Return_InvalidCredentials()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            UserName = "admin",
            Password = "wrong-password",
        });

        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>(ApiFactory.JsonOptions);

        Assert.Equal(BizCodes.InvalidCredentials, envelope!.Code);
        Assert.Null(envelope.Data);
    }

    [Fact]
    public async Task Create_Link_Should_Be_Reachable_And_Redirect()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var createResponse = await client.PostAsJsonAsync("/api/v1/links", new CreateLinkRequest
        {
            TargetUrl = "https://example.com/landing",
            Code = "go2026",
            Title = "示例链接",
            RedirectType = 302,
        });

        var created = await createResponse.Content.ReadFromJsonAsync<ApiResponse<LinkDto>>(ApiFactory.JsonOptions);
        Assert.Equal(BizCodes.Success, created!.Code);
        Assert.Equal("go2026", created.Data!.Code);

        var redirect = await client.GetAsync("/go2026");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("https://example.com/landing", redirect.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Create_Link_With_Invalid_Target_Should_Return_Field_Errors()
    {
        var client = factory.CreateClient();
        var token = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/v1/links", new CreateLinkRequest
        {
            TargetUrl = "javascript:alert(1)",
            Code = "bad001",
        });

        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<LinkDto>>(ApiFactory.JsonOptions);

        Assert.Equal(BizCodes.InvalidArgument, envelope!.Code);
        Assert.NotNull(envelope.Errors);
        Assert.Contains("TargetUrl", envelope.Errors!.Keys);
    }

    [Fact]
    public async Task Unknown_Code_Should_Render_Html_404_Page()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/nope123");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("text/html", response.Content.Headers.ContentType!.MediaType!);
    }

    private static async Task<string> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            UserName = "admin",
            Password = "Admin@123456",
        });

        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>(ApiFactory.JsonOptions);
        Assert.Equal(BizCodes.Success, envelope!.Code);
        return envelope.Data!.AccessToken;
    }
}
