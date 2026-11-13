using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShortLink.Infrastructure.Persistence;
using Xunit;

namespace ShortLink.IntegrationTests;

/// <summary>
/// 在<b>真 PostgreSQL</b> 上重放冒烟路径。
/// <para>
/// 第三个 provider 的意义不只是「多跑一遍」：迁移程序集是独立的一份，
/// 生成它的 EF 版本/命名策略与 SQLite、MySQL 那份彼此独立，
/// 而 PostgreSQL 对标识符大小写、identity 列、索引名的处理都与 MySQL 不同——
/// 这些差异只有在真库上跑一遍才会暴露。
/// </para>
/// </summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlSmokeTests(PostgreSqlHostFixture fixture)
{
    private const string TargetUrl = "https://example.com/it-postgres";

    [Fact]
    public async Task Smoke_Path_Should_Login_Create_Redirect_And_Count_On_PostgreSql()
    {
        ContainerTestGate.SkipUnlessEnabled();

        using var client = SmokeApi.CreateClient(fixture.Factory!);
        await SmokeApi.AuthenticateAsync(client);

        var linkId = await SmokeApi.CreateLinkAsync(client, "itpgsql", TargetUrl);

        var redirect = await client.GetAsync("/itpgsql");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Contains(TargetUrl, redirect.Headers.Location!.ToString(), StringComparison.Ordinal);

        await SmokeApi.WaitUntilAsync(
            async () => await SmokeApi.GetTotalClicksAsync(client, linkId) >= 1,
            "点击应在聚合窗口后落库到 PostgreSQL，并能被统计接口读到");
    }

    [Fact]
    public async Task Health_Should_Be_Ready_On_PostgreSql()
    {
        ContainerTestGate.SkipUnlessEnabled();

        using var client = SmokeApi.CreateClient(fixture.Factory!);

        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        var body = await ready.Content.ReadAsStringAsync();
        Assert.Contains("ready", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fail", body, StringComparison.OrdinalIgnoreCase);

        using var info = JsonDocument.Parse(await client.GetStringAsync("/health/info"));
        var backplane = info.RootElement
            .GetProperty("data").GetProperty("cache").GetProperty("backplane").GetString();
        Assert.Equal("in-process", backplane);
    }

    [Fact]
    public void Host_Should_Really_Be_Backed_By_PostgreSql()
    {
        ContainerTestGate.SkipUnlessEnabled();

        using var scope = fixture.Factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShortLinkDbContext>();

        Assert.True(db.Database.IsNpgsql(), "PostgreSQL 用例必须真的跑在 PostgreSQL 上");
    }
}
