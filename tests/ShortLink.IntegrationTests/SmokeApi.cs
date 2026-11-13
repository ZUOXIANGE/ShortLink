using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ShortLink.IntegrationTests;

/// <summary>
/// 容器化冒烟共用的 HTTP 动作。
/// <para>
/// 三个 provider 走的是同一串动作（登录 → 建链 → 跳转 → 读统计），
/// 抽出来是为了让它们只差「宿主」这一项，不会各自漂移成三份略有不同的断言。
/// </para>
/// </summary>
internal static class SmokeApi
{
    public static HttpClient CreateClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public static async Task AuthenticateAsync(HttpClient client)
    {
        var token = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public static async Task<string> LoginAsync(HttpClient client)
    {
        var body = new JsonObject
        {
            ["userName"] = "admin",
            ["password"] = ContainerizedApiFactory.AdminPassword,
        };

        using var response = await client.PostAsync("/api/v1/auth/login", JsonContent(body));
        response.EnsureSuccessStatusCode();

        using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(200, envelope.RootElement.GetProperty("code").GetInt32());

        return envelope.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;
    }

    public static async Task<long> CreateLinkAsync(HttpClient client, string shortCode, string targetUrl)
    {
        var body = new JsonObject
        {
            ["title"] = $"容器化冒烟 {shortCode}",
            ["targetUrl"] = targetUrl,
            ["domainId"] = 1,
            ["code"] = shortCode,
        };

        using var response = await client.PostAsync("/api/v1/links", JsonContent(body));
        response.EnsureSuccessStatusCode();

        using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(200, envelope.RootElement.GetProperty("code").GetInt32());
        return envelope.RootElement.GetProperty("data").GetProperty("id").GetInt64();
    }

    /// <summary>读取统计；宿主未就绪或返回业务失败时给 <c>-1</c>，交给轮询重试。</summary>
    public static async Task<long> GetTotalClicksAsync(HttpClient client, long linkId)
    {
        using var response = await client.GetAsync($"/api/v1/links/{linkId}/stats?days=7");
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return -1;
        }

        using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (envelope.RootElement.GetProperty("code").GetInt32() != 200)
        {
            return -1;
        }

        return envelope.RootElement.GetProperty("data").GetProperty("totalClicks").GetInt64();
    }

    /// <summary>轮询等待条件成立：成功即继续，超时带原因失败。</summary>
    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        Assert.Fail($"等待超时：{because}");
    }

    private static StringContent JsonContent(JsonNode body) =>
        new(body.ToJsonString(), Encoding.UTF8, "application/json");
}
