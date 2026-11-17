using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShortLink.Application.Partner;
using ShortLink.Contracts;
using ShortLink.Contracts.Auth;
using ShortLink.Contracts.Partner;
using ShortLink.Contracts.Users;
using Xunit;

namespace ShortLink.IntegrationTests;

/// <summary>
/// 第三方签名接口端到端用例：签名正确性、时间窗、重放、scope/角色与吊销。
/// 所有数据都通过现有服务创建，断言的是「凭据身份真正走进了同一套工作区与对象级权限」。
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PartnerApiTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions JsonOptions = ApiFactory.JsonOptions;

    [Fact]
    public async Task Signed_Request_Should_Create_Read_And_Delete_Link()
    {
        var admin = await LoginAdminAsync();
        var credential = await CreateCredentialAsync(
            admin,
            await CreateUserAsync(admin, "owner"),
            "owner",
            [ApiScopes.LinksRead, ApiScopes.LinksWrite, ApiScopes.LinksDelete, ApiScopes.AnalyticsRead]);

        var create = await SendSignedAsync(
            credential,
            HttpMethod.Post,
            "/api/v1/partner/links",
            body: new { targetUrl = $"https://example.com/{Guid.NewGuid():N}", title = "第三方创建" });

        var created = await ReadAsync<PartnerLinkDto>(create);
        Assert.Equal(BizCodes.Success, created.Code);
        Assert.NotNull(created.Data);
        Assert.NotEmpty(created.Data!.Code);

        var detail = await ReadAsync<PartnerLinkDto>(
            await SendSignedAsync(credential, HttpMethod.Get, $"/api/v1/partner/links/{created.Data.Id}"));
        Assert.Equal(created.Data.Code, detail.Data!.Code);

        var stats = await ReadAsync<JsonElement>(
            await SendSignedAsync(
                credential,
                HttpMethod.Get,
                $"/api/v1/partner/links/{created.Data.Id}/stats",
                query: [new("days", "7")]));
        Assert.Equal(BizCodes.Success, stats.Code);

        var deleted = await ReadAsync<object>(
            await SendSignedAsync(credential, HttpMethod.Delete, $"/api/v1/partner/links/{created.Data.Id}"));
        Assert.Equal(BizCodes.Success, deleted.Code);
    }

    [Fact]
    public async Task Wrong_Signature_Should_Return_18002()
    {
        var admin = await LoginAdminAsync();
        var credential = await CreateCredentialAsync(
            admin,
            await CreateUserAsync(admin, "member"),
            "member",
            [ApiScopes.LinksRead]);

        var response = await SendSignedAsync(
            credential,
            HttpMethod.Get,
            "/api/v1/partner/links",
            signatureOverride: "sha256=" + new string('0', 64));

        var envelope = await ReadAsync<object>(response);
        Assert.Equal(BizCodes.ApiSignatureInvalid, envelope.Code);
    }

    [Fact]
    public async Task Expired_Timestamp_Should_Return_18003()
    {
        var admin = await LoginAdminAsync();
        var credential = await CreateCredentialAsync(
            admin,
            await CreateUserAsync(admin, "member"),
            "member",
            [ApiScopes.LinksRead]);

        var response = await SendSignedAsync(
            credential,
            HttpMethod.Get,
            "/api/v1/partner/links",
            timestamp: DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds());

        var envelope = await ReadAsync<object>(response);
        Assert.Equal(BizCodes.ApiTimestampExpired, envelope.Code);
    }

    [Fact]
    public async Task Replayed_Nonce_Should_Return_18004()
    {
        var admin = await LoginAdminAsync();
        var credential = await CreateCredentialAsync(
            admin,
            await CreateUserAsync(admin, "member"),
            "member",
            [ApiScopes.LinksRead]);

        var nonce = Guid.NewGuid().ToString("N");
        var first = await ReadAsync<JsonElement>(
            await SendSignedAsync(credential, HttpMethod.Get, "/api/v1/partner/links", nonce: nonce));
        Assert.Equal(BizCodes.Success, first.Code);

        var second = await ReadAsync<object>(
            await SendSignedAsync(credential, HttpMethod.Get, "/api/v1/partner/links", nonce: nonce));
        Assert.Equal(BizCodes.ApiNonceReplayed, second.Code);
    }

    [Fact]
    public async Task Viewer_Credential_Should_Not_Write_Even_With_Write_Scope()
    {
        var admin = await LoginAdminAsync();
        var credential = await CreateCredentialAsync(
            admin,
            await CreateUserAsync(admin, "viewer"),
            "viewer",
            [ApiScopes.LinksRead, ApiScopes.LinksWrite]);

        var response = await SendSignedAsync(
            credential,
            HttpMethod.Post,
            "/api/v1/partner/links",
            body: new { targetUrl = "https://example.com/viewer" });

        var envelope = await ReadAsync<object>(response);
        Assert.Equal(BizCodes.ApiScopeDenied, envelope.Code);
    }

    [Fact]
    public async Task Missing_Scope_Should_Return_18005()
    {
        var admin = await LoginAdminAsync();
        var credential = await CreateCredentialAsync(
            admin,
            await CreateUserAsync(admin, "owner"),
            "owner",
            [ApiScopes.LinksRead]);

        var response = await SendSignedAsync(
            credential,
            HttpMethod.Post,
            "/api/v1/partner/links",
            body: new { targetUrl = "https://example.com/no-scope" });

        var envelope = await ReadAsync<object>(response);
        Assert.Equal(BizCodes.ApiScopeDenied, envelope.Code);
    }

    [Fact]
    public async Task Member_Credential_Should_Not_Modify_Other_Users_Link()
    {
        var admin = await LoginAdminAsync();
        var memberId = await CreateUserAsync(admin, "member");
        var credential = await CreateCredentialAsync(
            admin,
            memberId,
            "member",
            [ApiScopes.LinksRead, ApiScopes.LinksWrite]);

        var adminLink = await CreateLinkAsAdminAsync(admin, $"https://example.com/{Guid.NewGuid():N}");

        var response = await SendSignedAsync(
            credential,
            HttpMethod.Put,
            $"/api/v1/partner/links/{adminLink}",
            body: new { targetUrl = "https://example.com/changed", title = "越权修改" });

        var envelope = await ReadAsync<object>(response);
        Assert.Equal(BizCodes.Forbidden, envelope.Code);
    }

    [Fact]
    public async Task Member_Credential_Should_Modify_Own_Link()
    {
        var admin = await LoginAdminAsync();
        var memberId = await CreateUserAsync(admin, "member");
        var credential = await CreateCredentialAsync(
            admin,
            memberId,
            "member",
            [ApiScopes.LinksRead, ApiScopes.LinksWrite]);

        var created = await ReadAsync<PartnerLinkDto>(
            await SendSignedAsync(
                credential,
                HttpMethod.Post,
                "/api/v1/partner/links",
                body: new { targetUrl = $"https://example.com/{Guid.NewGuid():N}", title = "自己的链接" }));

        var updated = await ReadAsync<PartnerLinkDto>(
            await SendSignedAsync(
                credential,
                HttpMethod.Put,
                $"/api/v1/partner/links/{created.Data!.Id}",
                body: new { targetUrl = "https://example.com/updated", title = "已更新" }));

        Assert.Equal(BizCodes.Success, updated.Code);
        Assert.Equal("已更新", updated.Data!.Title);
    }

    [Fact]
    public async Task Revoked_Credential_Should_Return_18001()
    {
        var admin = await LoginAdminAsync();
        var credential = await CreateCredentialAsync(
            admin,
            await CreateUserAsync(admin, "member"),
            "member",
            [ApiScopes.LinksRead]);

        var revoke = await admin.PostAsync(
            $"/api/v1/api-clients/{credential.Id}/revoke",
            null,
            Ct);
        var revoked = await revoke.Content.ReadFromJsonAsync<ApiResponse<object>>(JsonOptions, Ct);
        Assert.Equal(BizCodes.Success, revoked!.Code);

        var envelope = await ReadAsync<object>(
            await SendSignedAsync(credential, HttpMethod.Get, "/api/v1/partner/links"));
        Assert.Equal(BizCodes.ApiKeyInvalid, envelope.Code);
    }

    [Fact]
    public async Task Unknown_Key_Should_Return_18001()
    {
        var credential = new Credential(
            0,
            "ak_" + Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"),
            string.Empty);

        var envelope = await ReadAsync<object>(
            await SendSignedAsync(credential, HttpMethod.Get, "/api/v1/partner/links"));
        Assert.Equal(BizCodes.ApiKeyInvalid, envelope.Code);
    }

    [Fact]
    public async Task Jwt_Should_Not_Access_Partner_And_Api_Key_Should_Not_Access_Admin()
    {
        var admin = await LoginAdminAsync();

        var partnerAsJwt = await ReadAsync<object>(await admin.GetAsync("/api/v1/partner/links", Ct));
        Assert.Equal(BizCodes.ApiKeyInvalid, partnerAsJwt.Code);

        var credential = await CreateCredentialAsync(
            admin,
            await CreateUserAsync(admin, "owner"),
            "owner",
            [ApiScopes.LinksRead]);

        var adminAsApiKey = await ReadAsync<object>(
            await SendSignedAsync(credential, HttpMethod.Get, "/api/v1/links"));
        Assert.Equal(BizCodes.Unauthorized, adminAsApiKey.Code);
    }

    private async Task<HttpClient> LoginAdminAsync()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest { UserName = "admin", Password = "Admin@123456" },
            Ct);

        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>(JsonOptions, Ct);
        Assert.Equal(BizCodes.Success, envelope!.Code);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", envelope.Data!.AccessToken);
        return client;
    }

    private static async Task<long> CreateUserAsync(HttpClient admin, string role)
    {
        var name = $"partner_{role}_{Guid.NewGuid():N}"[..24];
        var response = await admin.PostAsJsonAsync(
            "/api/v1/users",
            new CreateUserRequest
            {
                UserName = name,
                DisplayName = name,
                Password = "Partner@123456",
                Role = role,
            },
            Ct);

        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<ManagedUserDto>>(JsonOptions, Ct);
        Assert.Equal(BizCodes.Success, envelope!.Code);
        return envelope.Data!.Id;
    }

    private static async Task<Credential> CreateCredentialAsync(
        HttpClient admin,
        long userId,
        string role,
        IReadOnlyList<string> scopes)
    {
        var response = await admin.PostAsJsonAsync(
            "/api/v1/api-clients",
            new CreateApiClientRequest
            {
                Name = $"集成 {Guid.NewGuid():N}"[..16],
                UserId = userId,
                Role = role,
                Scopes = scopes.ToList(),
                RateLimitPerMinute = 1000,
            },
            Ct);

        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<CreateApiClientResponse>>(JsonOptions, Ct);
        Assert.Equal(BizCodes.Success, envelope!.Code);
        return new Credential(
            envelope.Data!.Client.Id,
            envelope.Data.Client.KeyId,
            envelope.Data.Secret,
            envelope.Data.Client.Name);
    }

    private static async Task<long> CreateLinkAsAdminAsync(HttpClient admin, string targetUrl)
    {
        var response = await admin.PostAsJsonAsync(
            "/api/v1/links",
            new { targetUrl, title = "管理员创建" },
            Ct);

        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<JsonElement>>(JsonOptions, Ct);
        Assert.Equal(BizCodes.Success, envelope!.Code);
        return envelope.Data.GetProperty("id").GetInt64();
    }

    private async Task<HttpResponseMessage> SendSignedAsync(
        Credential credential,
        HttpMethod method,
        string path,
        IEnumerable<KeyValuePair<string, string?>>? query = null,
        object? body = null,
        long? timestamp = null,
        string? nonce = null,
        string? signatureOverride = null)
    {
        var pairs = (query ?? []).ToList();
        var url = pairs.Count == 0
            ? path
            : $"{path}?{string.Join('&', pairs.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value ?? string.Empty)}"))}";

        var json = body is null ? null : JsonSerializer.Serialize(body, JsonOptions);
        var bodyBytes = json is null ? [] : Encoding.UTF8.GetBytes(json);
        var timestampValue = (timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .ToString(CultureInfo.InvariantCulture);
        var nonceValue = nonce ?? Guid.NewGuid().ToString("N");

        var canonical = PartnerSignature.Build(method.Method, path, pairs, bodyBytes, timestampValue, nonceValue);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(credential.Secret));
        var signature = signatureOverride
                        ?? "sha256=" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("X-Api-Key", credential.KeyId);
        request.Headers.TryAddWithoutValidation("X-Timestamp", timestampValue);
        request.Headers.TryAddWithoutValidation("X-Nonce", nonceValue);
        request.Headers.TryAddWithoutValidation("X-Signature", signature);

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return await factory.CreateClient().SendAsync(request, Ct);
    }

    private static async Task<ApiResponse<T>> ReadAsync<T>(HttpResponseMessage response)
    {
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<T>>(JsonOptions, Ct);
        Assert.NotNull(envelope);
        return envelope!;
    }

    private sealed record Credential(long Id, string KeyId, string Secret, string Name);
}
