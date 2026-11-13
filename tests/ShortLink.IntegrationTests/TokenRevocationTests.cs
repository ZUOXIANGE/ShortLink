using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ShortLink.Contracts;
using ShortLink.Contracts.Auth;
using ShortLink.Contracts.Users;
using ShortLink.Domain.Users;
using ShortLink.Domain.Workspaces;
using ShortLink.Infrastructure.Persistence;
using Xunit;

namespace ShortLink.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class TokenRevocationTests(ApiFactory factory)
{
    [Fact]
    public async Task ResetPassword_Should_Revoke_Old_Token_Before_720_Minute_Expiry()
    {
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await LoginAsync(admin, "admin", "Admin@123456"));
        var userName = $"reset{Guid.NewGuid():N}"[..16];
        var user = await CreateUserAsync(admin, userName, "Before123", "member");
        var oldToken = await LoginAsync(factory.CreateClient(), userName, "Before123");

        var reset = await admin.PostAsJsonAsync(
            $"/api/v1/users/{user.Id}/reset-password",
            new ResetPasswordRequest { NewPassword = "After123" }, TestContext.Current.CancellationToken);
        var resetEnvelope = await reset.Content.ReadFromJsonAsync<ApiResponse<object?>>(ApiFactory.JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(BizCodes.Success, resetEnvelope!.Code);

        var oldClient = factory.CreateClient();
        oldClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", oldToken);
        var rejected = await oldClient.GetAsync("/api/v1/links", TestContext.Current.CancellationToken);
        var rejectedEnvelope = await rejected.Content.ReadFromJsonAsync<ApiResponse<JsonElement>>(ApiFactory.JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(BizCodes.Unauthorized, rejectedEnvelope!.Code);

        var newToken = await LoginAsync(factory.CreateClient(), userName, "After123");
        var newClient = factory.CreateClient();
        newClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
        var accepted = await newClient.GetAsync("/api/v1/links", TestContext.Current.CancellationToken);
        var acceptedEnvelope = await accepted.Content.ReadFromJsonAsync<ApiResponse<JsonElement>>(ApiFactory.JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(BizCodes.Success, acceptedEnvelope!.Code);
    }

    [Fact]
    public async Task CurrentUser_Should_Use_Token_Workspace_And_Membership_Role()
    {
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await LoginAsync(admin, "admin", "Admin@123456"));
        var userName = $"workspace{Guid.NewGuid():N}"[..16];
        var user = await CreateUserAsync(admin, userName, "Workspace123", "member");

        long workspaceId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShortLinkDbContext>();
            var workspace = Workspace.Create("第二工作区", $"ws-{Guid.NewGuid():N}", false, DateTime.UtcNow);
            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.WorkspaceMembers.Add(WorkspaceMember.Create(workspace.Id, user.Id, UserRole.Viewer, DateTime.UtcNow));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            workspaceId = workspace.Id;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "ShortLink",
            Audience = "ShortLink.Admin",
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddHours(1),
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", user.Id.ToString(CultureInfo.InvariantCulture)),
                new Claim("name", user.UserName),
                new Claim("role", "member"),
                new Claim("ws", workspaceId.ToString(CultureInfo.InvariantCulture)),
                new Claim("token_version", "0"),
            ]),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes("integration-tests-signing-key-32-bytes-minimum")),
                SecurityAlgorithms.HmacSha256),
        };
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            new JsonWebTokenHandler().CreateToken(descriptor));

        var response = await client.GetAsync("/api/v1/auth/me", TestContext.Current.CancellationToken);
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<UserDto>>(ApiFactory.JsonOptions, TestContext.Current.CancellationToken);

        Assert.Equal(BizCodes.Success, envelope!.Code);
        Assert.Equal(workspaceId, envelope.Data!.WorkspaceId);
        Assert.Equal("viewer", envelope.Data.Role);
    }

    [Fact]
    public async Task Legacy_Token_Without_Version_Should_Work_For_Unchanged_Account()
    {
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await LoginAsync(admin, "admin", "Admin@123456"));
        var userName = $"legacy{Guid.NewGuid():N}"[..16];
        var user = await CreateUserAsync(admin, userName, "Legacy123", "viewer");

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "ShortLink",
            Audience = "ShortLink.Admin",
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddHours(1),
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", user.Id.ToString(CultureInfo.InvariantCulture)),
                new Claim("name", user.UserName),
                new Claim("role", user.Role),
                new Claim("ws", "1"),
            ]),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes("integration-tests-signing-key-32-bytes-minimum")),
                SecurityAlgorithms.HmacSha256),
        };
        var legacyToken = new JsonWebTokenHandler().CreateToken(descriptor);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", legacyToken);

        var response = await client.GetAsync("/api/v1/links", TestContext.Current.CancellationToken);
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<JsonElement>>(ApiFactory.JsonOptions, TestContext.Current.CancellationToken);

        Assert.Equal(BizCodes.Success, envelope!.Code);
    }

    private static async Task<ManagedUserDto> CreateUserAsync(
        HttpClient admin,
        string userName,
        string password,
        string role)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/users", new CreateUserRequest
        {
            UserName = userName,
            DisplayName = userName,
            Password = password,
            Role = role,
        });
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<ManagedUserDto>>(ApiFactory.JsonOptions);
        Assert.True(envelope!.Code == BizCodes.Success, $"创建测试账号失败：{envelope.Msg}");
        return envelope.Data!;
    }

    private static async Task<string> LoginAsync(HttpClient client, string userName, string password)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            UserName = userName,
            Password = password,
        });
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>(ApiFactory.JsonOptions);
        Assert.True(envelope!.Code == BizCodes.Success, $"登录测试账号失败：{envelope.Msg}");
        return envelope.Data!.AccessToken;
    }
}
