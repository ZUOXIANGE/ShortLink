using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Options;
using ShortLink.Contracts;
using ShortLink.Contracts.Auth;
using ShortLink.Contracts.Domains;
using ShortLink.Contracts.Users;
using ShortLink.Domain.Analytics;
using ShortLink.Infrastructure.Analytics;
using ShortLink.Infrastructure.Persistence;
using Xunit;

namespace ShortLink.IntegrationTests;

/// <summary>
/// 工程化加固相关的回归用例：权限收口、健康探针、点击明细保留策略。
/// <para>
/// 这些能力都属于「没有功能页面、但线上会出事」的类型，
/// 因此每条都必须能被测试直接驱动，而不是靠人工点一遍。
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class HardeningTests(ApiFactory factory)
{
    private const string AdminPassword = "Admin@123456";

    [Fact]
    public async Task Viewer_Should_Read_Domains_But_Not_Create_Them()
    {
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await LoginAsync(admin, "admin", AdminPassword));

        // 用随机用户名避免与其他用例的 fixture 数据互相干扰。
        var viewerName = $"view{Guid.NewGuid():N}"[..12];
        var created = await admin.PostAsJsonAsync("/api/v1/users", new CreateUserRequest
        {
            UserName = viewerName,
            DisplayName = "只读观测员",
            Password = "Viewer@123456",
            Role = "viewer",
        }, TestContext.Current.CancellationToken);
        var createdEnvelope = await created.Content.ReadFromJsonAsync<ApiResponse<ManagedUserDto>>(ApiFactory.JsonOptions, TestContext.Current.CancellationToken);
        Assert.True(createdEnvelope!.Code == BizCodes.Success, $"创建 viewer 失败：{createdEnvelope.Msg}");

        var viewer = factory.CreateClient();
        viewer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await LoginAsync(viewer, viewerName, "Viewer@123456"));

        // 读：放行。
        var list = await viewer.GetAsync("/api/v1/domains", TestContext.Current.CancellationToken);
        var listEnvelope = await list.Content.ReadFromJsonAsync<ApiResponse<JsonElement>>(ApiFactory.JsonOptions, TestContext.Current.CancellationToken);
        Assert.True(listEnvelope!.Code == BizCodes.Success, $"viewer 应能读域名列表，实际：{listEnvelope.Msg}");

        // 写：必须 40301。这条是回归点——此前 viewer 能删域名。
        var forbidden = await viewer.PostAsJsonAsync("/api/v1/domains", new CreateDomainRequest
        {
            Host = $"deny-{Guid.NewGuid():N}.example.com",
            Scheme = "https",
            Name = "越权尝试",
            IsEnabled = true,
        }, TestContext.Current.CancellationToken);
        var forbiddenEnvelope = await forbidden.Content.ReadFromJsonAsync<ApiResponse<JsonElement>>(ApiFactory.JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(BizCodes.Forbidden, forbiddenEnvelope!.Code);

        var delete = await viewer.DeleteAsync("/api/v1/domains/1", TestContext.Current.CancellationToken);
        var deleteEnvelope = await delete.Content.ReadFromJsonAsync<ApiResponse<JsonElement>>(ApiFactory.JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(BizCodes.Forbidden, deleteEnvelope!.Code);
    }

    [Fact]
    public async Task Health_Should_Stay_Live_And_Ready_Without_Redis()
    {
        var client = factory.CreateClient();

        // /health 只证明进程活着，不查任何外部依赖。
        var live = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("ok", (await live.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Trim());

        // /health/ready 同时校验 DB 与缓存。本宿主未配置 Redis，
        // 探针应视作「不使用缓存 = 健康」，而不是误报 503。
        var ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("ready", (await ready.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Trim());
    }

    [Fact]
    public async Task Retention_Purge_Should_Delete_Only_Expired_Clicks()
    {
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await LoginAsync(admin, "admin", AdminPassword));

        var linkId = await CreateLinkAsync(admin, $"ret{Guid.NewGuid():N}"[..8]);
        var now = new DateTime(2026, 1, 15, 4, 0, 0, DateTimeKind.Utc);
        const int retentionDays = 30;
        var cutoff = now.AddDays(-retentionDays);

        // 造 40 条过期、5 条未过期的明细。过期数据必须被删，未过期的一条都不能动。
        await SeedClicksAsync(linkId, cutoff.AddDays(-1), 40);
        await SeedClicksAsync(linkId, cutoff.AddDays(1), 5);

        using var scope = factory.Services.CreateScope();
        var service = ActivatorUtilities.CreateInstance<ClickRetentionService>(scope.ServiceProvider);

        var purged = await service.PurgeAsync(
            now,
            new AnalyticsRetentionOptions
            {
                Enabled = true,
                ClickRetentionDays = retentionDays,
                BatchSize = 500,
                MaxRowsPerRun = 10_000,
                BatchDelayMilliseconds = 0,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(40, purged);

        var db = scope.ServiceProvider.GetRequiredService<ShortLinkDbContext>();
        var remaining = await db.ClickEvents.CountAsync(c => c.LinkId == linkId, TestContext.Current.CancellationToken);
        Assert.Equal(5, remaining);
        Assert.False(await db.ClickEvents.AnyAsync(c => c.LinkId == linkId && c.ClickedAtUtc < cutoff, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Retention_Should_Be_Disabled_By_Default()
    {
        // 默认关闭是刻意设计：删数据不可逆，必须由部署方显式开启。
        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<AnalyticsRetentionOptions>>()
            .Value;

        Assert.False(options.Enabled);
        Assert.True(options.ClickRetentionDays > 0);
    }

    private async Task SeedClicksAsync(long linkId, DateTime clickedAtUtc, int count)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShortLinkDbContext>();

        // 用宿主里的同一份统计日历算日期，而不是在测试里另写一套：
        // 否则测试造的数据与生产写入的数据会落在不同日界上，掩盖真正的口径错误。
        var calendar = scope.ServiceProvider.GetRequiredService<IStatisticsCalendar>();

        for (var i = 0; i < count; i++)
        {
            db.ClickEvents.Add(ClickEvent.Create(
                workspaceId: 1,
                linkId: linkId,
                domainId: 1,
                routeId: null,
                code: $"seed{i}",
                clickedAtUtc: clickedAtUtc.AddSeconds(i),
                clickDate: calendar.ToDate(clickedAtUtc.AddSeconds(i)),
                visitorHash: Guid.NewGuid().ToString("N"),
                ip: "127.0.0.1",
                userAgent: "integration-test",
                referer: null,
                country: null,
                province: null,
                device: "desktop",
                browser: "chrome",
                operatingSystem: "linux",
                isBot: false));
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Health_Info_Should_Expose_Cache_Topology_And_Statistics_TimeZone()
    {
        // 这两项都是「配置对错只体现在数字上」的东西：缓存有没有跨实例背板、统计按哪个时区切日。
        // 把它们暴露在探针里，运维才不用读代码才知道自己站在哪一侧。
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/info", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var data = document.RootElement.GetProperty("data");

        var cache = data.GetProperty("cache");
        Assert.Equal("in-process", cache.GetProperty("backplane").GetString());
        Assert.False(cache.GetProperty("crossInstanceInvalidation").GetBoolean());
        Assert.True(cache.GetProperty("linkTtlSeconds").GetInt32() > 0);

        Assert.Equal("UTC", data.GetProperty("statistics").GetProperty("timeZone").GetString());
    }

    private static async Task<long> CreateLinkAsync(HttpClient client, string code)
    {
        var response = await client.PostAsJsonAsync("/api/v1/links", new ShortLink.Contracts.Links.CreateLinkRequest
        {
            Code = code,
            TargetUrl = "https://example.com/retention",
            Title = "保留策略用例",
        });

        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<ShortLink.Contracts.Links.LinkDto>>(ApiFactory.JsonOptions);
        Assert.True(envelope!.Code == BizCodes.Success, $"创建短链失败：{envelope.Msg}");
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
        Assert.True(envelope!.Code == BizCodes.Success, $"登录失败（{userName}）：{envelope.Msg}");
        return envelope.Data!.AccessToken;
    }
}
