using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShortLink.Contracts;
using ShortLink.Contracts.Analytics;
using ShortLink.Contracts.Auth;
using ShortLink.Contracts.Links;
using ShortLink.Contracts.Users;
using Xunit;

namespace ShortLink.IntegrationTests;

/// <summary>P1 / P2 能力的端到端验证：角色、批量、导出、条件路由、转化回调。</summary>
[Collection(ApiCollection.Name)]
public sealed class PlatformEndpointTests(ApiFactory factory)
{
    private const string AdminPassword = "Admin@123456";

    [Fact]
    public async Task Owner_Should_Create_Member_And_Member_Should_Not_Manage_Users()
    {
        var owner = factory.CreateClient();
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(owner, "admin", AdminPassword));

        var create = await owner.PostAsJsonAsync("/api/v1/users", new CreateUserRequest
        {
            UserName = "member01",
            DisplayName = "成员一号",
            Password = "Member@123456",
            Role = "member",
        }, TestContext.Current.CancellationToken);

        var created = await create.Content.ReadFromJsonAsync<ApiResponse<ManagedUserDto>>(ApiFactory.JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(BizCodes.Success, created!.Code);
        Assert.Equal("member", created.Data!.Role);
        Assert.Equal("成员", created.Data.RoleName);

        var member = factory.CreateClient();
        member.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await LoginAsync(member, "member01", "Member@123456"));

        var forbidden = await member.GetAsync("/api/v1/users", TestContext.Current.CancellationToken);
        var envelope = await forbidden.Content.ReadFromJsonAsync<ApiResponse<JsonElement>>(ApiFactory.JsonOptions, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, forbidden.StatusCode);
        Assert.Equal(BizCodes.Forbidden, envelope!.Code);

        // 成员仍可读短链列表，说明「只读管理接口」而非整体拒绝。
        var links = await member.GetAsync("/api/v1/links", TestContext.Current.CancellationToken);
        var linksEnvelope = await links.Content.ReadFromJsonAsync<ApiResponse<JsonElement>>(ApiFactory.JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(BizCodes.Success, linksEnvelope!.Code);
    }

    [Fact]
    public async Task Batch_Disable_Should_Return_Per_Item_Result()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, "admin", AdminPassword));

        var first = await CreateLinkAsync(client, "batch01");
        var second = await CreateLinkAsync(client, "batch02");

        var response = await client.PostAsJsonAsync("/api/v1/links/batch", new BatchLinkRequest
        {
            Action = BatchActions.Disable,
            Ids = [first, second, 999_999],
        }, TestContext.Current.CancellationToken);

        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<BatchResult>>(ApiFactory.JsonOptions, TestContext.Current.CancellationToken);

        Assert.Equal(BizCodes.Success, envelope!.Code);
        Assert.Equal(3, envelope.Data!.Total);
        Assert.Equal(2, envelope.Data.Succeeded);
        Assert.Equal(1, envelope.Data.Failed);

        var missing = envelope.Data.Items.Single(i => i.Id == 999_999);
        Assert.False(missing.Success);
        Assert.Equal(BizCodes.LinkNotFound, missing.Code);
    }

    [Fact]
    public async Task Batch_Should_Reject_Too_Many_Items()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, "admin", AdminPassword));

        var response = await client.PostAsJsonAsync("/api/v1/links/batch", new BatchLinkRequest
        {
            Action = BatchActions.Enable,
            Ids = Enumerable.Range(1, 501).Select(i => (long)i).ToList(),
        }, TestContext.Current.CancellationToken);

        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<BatchResult>>(ApiFactory.JsonOptions, TestContext.Current.CancellationToken);

        Assert.Equal(BizCodes.InvalidArgument, envelope!.Code);
        Assert.NotNull(envelope.Errors);
    }

    [Fact]
    public async Task Export_Should_Stream_Csv_With_Bom_And_Header()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, "admin", AdminPassword));

        await CreateLinkAsync(client, "csv0001");

        var response = await client.GetAsync("/api/v1/links/export?pageSize=50", TestContext.Current.CancellationToken);

        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.True(
            string.Equals("text/csv", response.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal),
            $"期望 text/csv，实际 {response.Content.Headers.ContentType?.MediaType}；响应体：{Encoding.UTF8.GetString(bytes)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(bytes.Length > 3);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        Assert.Equal("false", Assert.Single(response.Headers.GetValues("X-Export-Truncated")));
        Assert.Contains(response.Content.Headers.ContentDisposition!.Parameters, parameter =>
            parameter.Name == "truncated" && parameter.Value == "false");

        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.Contains("短码", text, StringComparison.Ordinal);
        Assert.Contains("csv0001", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Conditional_Route_Should_Pick_Target_By_Country_Header()
    {
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, "admin", AdminPassword));

        var linkId = await CreateLinkAsync(client, "route01");

        var save = await client.PutAsJsonAsync($"/api/v1/links/{linkId}/routes", new SaveLinkRoutesRequest
        {
            Routes =
            [
                new LinkRouteInput
                {
                    Name = "国内",
                    Priority = 10,
                    TargetUrl = "https://cn.example.com/landing",
                    Match = "all",
                    Conditions = [new RouteConditionDto { Field = "country", Op = "eq", Value = "CN" }],
                },
                new LinkRouteInput
                {
                    Name = "默认",
                    Priority = 0,
                    TargetUrl = "https://global.example.com/landing",
                },
            ],
        }, TestContext.Current.CancellationToken);

        var saved = await save.Content.ReadFromJsonAsync<ApiResponse<IReadOnlyList<LinkRouteDto>>>(ApiFactory.JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(BizCodes.Success, saved!.Code);
        Assert.Equal(2, saved.Data!.Count);

        using var cnRequest = new HttpRequestMessage(HttpMethod.Get, "/route01");
        cnRequest.Headers.Add("CF-IPCountry", "CN");
        var cnRedirect = await client.SendAsync(cnRequest, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, cnRedirect.StatusCode);
        Assert.Equal("https://cn.example.com/landing", cnRedirect.Headers.Location!.ToString());

        var globalRedirect = await client.GetAsync("/route01", TestContext.Current.CancellationToken);
        Assert.Equal("https://global.example.com/landing", globalRedirect.Headers.Location!.ToString());

        // 规则测试接口应按 1000 次模拟给出分流比例。
        var test = await client.PostAsJsonAsync($"/api/v1/links/{linkId}/routes/test", new RouteTestRequest
        {
            Country = "CN",
            Repeat = 1000,
        }, TestContext.Current.CancellationToken);

        var tested = await test.Content.ReadFromJsonAsync<ApiResponse<RouteTestResult>>(ApiFactory.JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(BizCodes.Success, tested!.Code);
        Assert.Equal("国内", tested.Data!.RouteName);
        Assert.NotEmpty(tested.Data.Distribution);
    }

    [Fact]
    public async Task Click_Cap_Should_Disable_Link_After_Threshold()
    {
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, "admin", AdminPassword));

        var create = await client.PostAsJsonAsync("/api/v1/links", new CreateLinkRequest
        {
            TargetUrl = "https://example.com/cap",
            Code = "cap001",
            MaxClicks = 1,
        }, TestContext.Current.CancellationToken);

        var created = await create.Content.ReadFromJsonAsync<ApiResponse<LinkDto>>(ApiFactory.JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(BizCodes.Success, created!.Code);

        var first = await client.GetAsync("/cap001", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);

        // 点击落库是消息驱动的批处理（聚合窗口默认 500ms）。
        // 用「轮询到条件成立」代替固定 sleep：成功了就立刻继续，坏了则在超时点明确失败，
        // 而不是靠一个刚好够长的魔法数字去掩盖真实故障。
        await WaitUntilAsync(
            async () => (await client.GetAsync("/cap001")).StatusCode == HttpStatusCode.Gone,
            "点击达到上限后应停用短链并返回 410");
    }

    [Fact]
    public async Task Stats_Should_Include_Fallback_Variant_Without_Blowing_Up()
    {
        // 回归用例：兜底命中（未配置任何条件路由）的点击其 RouteId 为 null，
        // 分支统计必须把 null 折叠成兜底行，而不是抛 ArgumentNullException 变成 500。
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, "admin", AdminPassword));

        var linkId = await CreateLinkAsync(client, "stat001");

        var redirect = await client.GetAsync("/stat001", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);

        await WaitUntilAsync(
            async () => (await GetStatsAsync(client, linkId))?.TotalClicks >= 1,
            "点击应在聚合窗口后被写入并能被统计到");

        var statsResponse = await client.GetAsync($"/api/v1/links/{linkId}/stats?days=7", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, statsResponse.StatusCode);

        var stats = await statsResponse.Content.ReadFromJsonAsync<ApiResponse<LinkStatsDto>>(ApiFactory.JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(BizCodes.Success, stats!.Code);
        Assert.True(stats.Data!.TotalClicks >= 1);
        Assert.Contains(stats.Data.Variants, v => v.Name == "兜底地址" && v.Clicks >= 1);
    }

    [Fact]
    public async Task Burst_Clicks_Should_All_Survive_Batching()
    {
        // 批处理的目的是把 N 次点击合并成少数几次写入，不是「抽稀」。
        // 这条用例盯的就是这个区别：突发 8 次，统计必须一条不少。
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, "admin", AdminPassword));

        var linkId = await CreateLinkAsync(client, "burst01");

        const int clicks = 8;
        for (var index = 0; index < clicks; index++)
        {
            var redirect = await client.GetAsync("/burst01", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        }

        await WaitUntilAsync(
            async () => (await GetStatsAsync(client, linkId))?.TotalClicks >= clicks,
            $"{clicks} 次点击应全部落库（批处理只允许合并写入，不允许丢）");
    }

    /// <summary>读取短链统计；宿主尚未就绪或返回错误信封时给 <c>null</c>，交给轮询重试。</summary>
    private static async Task<LinkStatsDto?> GetStatsAsync(HttpClient client, long linkId)
    {
        var response = await client.GetAsync($"/api/v1/links/{linkId}/stats?days=7");
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return null;
        }

        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<LinkStatsDto>>(ApiFactory.JsonOptions);
        return envelope?.Code == BizCodes.Success ? envelope.Data : null;
    }

    /// <summary>
    /// 轮询等待条件成立。
    /// <para>
    /// 消息链路的「何时完成」由调用方给定条件表达，而不是由测试里的 sleep 猜测——
    /// 后者写短了会偶发失败，写长了会拖慢整个套件，且失败时报错信息毫无指向性。
    /// </para>
    /// </summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        Assert.Fail($"等待超时：{because}");
    }

    [Fact]
    public async Task Conversion_Callback_Should_Reject_Bad_Signature_And_Be_Idempotent()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, "admin", AdminPassword));

        var linkId = await CreateLinkAsync(client, "conv001");

        var keyResponse = await client.GetAsync($"/api/v1/links/{linkId}/conversion-key", TestContext.Current.CancellationToken);
        var key = await keyResponse.Content.ReadFromJsonAsync<ApiResponse<ConversionKeyDto>>(ApiFactory.JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(BizCodes.Success, key!.Code);
        Assert.Equal("HMAC-SHA256", key.Data!.Algorithm);

        var body = JsonSerializer.Serialize(new ConversionCallbackRequest
        {
            LinkId = linkId,
            EventId = "order-0001",
            Metric = "order_paid",
            Value = 99.5m,
        }, ApiFactory.JsonOptions);

        var anonymous = factory.CreateClient();

        var bad = await PostSignedAsync(anonymous, body, key.Data.Secret, validSignature: false);
        var badEnvelope = await bad.Content.ReadFromJsonAsync<ApiResponse<ConversionResultDto>>(ApiFactory.JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(BizCodes.ConversionSignatureInvalid, badEnvelope!.Code);

        var first = await PostSignedAsync(anonymous, body, key.Data.Secret, validSignature: true);
        var firstEnvelope = await first.Content.ReadFromJsonAsync<ApiResponse<ConversionResultDto>>(ApiFactory.JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(BizCodes.Success, firstEnvelope!.Code);
        Assert.False(firstEnvelope.Data!.Duplicate);
        Assert.Equal(1, firstEnvelope.Data.TotalConversions);

        var replay = await PostSignedAsync(anonymous, body, key.Data.Secret, validSignature: true);
        var replayEnvelope = await replay.Content.ReadFromJsonAsync<ApiResponse<ConversionResultDto>>(ApiFactory.JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(BizCodes.Success, replayEnvelope!.Code);
        Assert.True(replayEnvelope.Data!.Duplicate);
        Assert.Equal(1, replayEnvelope.Data.TotalConversions);
    }

    [Fact]
    public async Task Abuse_Report_Should_Be_Recorded_And_Visible_To_Managers()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, "admin", AdminPassword));

        await CreateLinkAsync(client, "abuse01");

        var anonymous = factory.CreateClient();
        var report = await anonymous.PostAsJsonAsync("/api/v1/abuse/reports", new ShortLink.Contracts.Security.AbuseReportRequest
        {
            Code = "abuse01",
            Reason = "该链接跳转到仿冒的支付页面，请尽快处理。",
        }, TestContext.Current.CancellationToken);

        var envelope = await report.Content.ReadFromJsonAsync<ApiResponse<object?>>(ApiFactory.JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(BizCodes.Success, envelope!.Code);

        var events = await client.GetAsync("/api/v1/security/events?type=abuse_reported&days=7", TestContext.Current.CancellationToken);
        var page = await events.Content.ReadFromJsonAsync<ApiResponse<PagedResult<ShortLink.Contracts.Security.SecurityEventDto>>>(ApiFactory.JsonOptions, TestContext.Current.CancellationToken);

        Assert.Equal(BizCodes.Success, page!.Code);
        Assert.Contains(page.Data!.Items, e => e.Code == "abuse01");
    }

    private static async Task<HttpResponseMessage> PostSignedAsync(
        HttpClient client,
        string body,
        string secret,
        bool validSignature)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var payload = $"{timestamp}.{body}";
        var signature = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        if (!validSignature)
        {
            signature = new string('0', signature.Length);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/conversions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        request.Headers.Add("X-Timestamp", timestamp);
        request.Headers.Add("X-Signature", $"sha256={signature}");

        return await client.SendAsync(request);
    }

    private static async Task<long> CreateLinkAsync(HttpClient client, string code)
    {
        var response = await client.PostAsJsonAsync("/api/v1/links", new CreateLinkRequest
        {
            TargetUrl = $"https://example.com/{code}",
            Code = code,
            Title = code,
        });

        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<LinkDto>>(ApiFactory.JsonOptions);
        Assert.Equal(BizCodes.Success, envelope!.Code);
        return envelope.Data!.Id;
    }

    private static async Task<string> LoginAsync(HttpClient client, string userName, string password)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            UserName = userName,
            Password = password,
        });

        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>(ApiFactory.JsonOptions);
        Assert.Equal(BizCodes.Success, envelope!.Code);
        return envelope.Data!.AccessToken;
    }
}
