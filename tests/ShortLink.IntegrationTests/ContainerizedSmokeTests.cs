using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShortLink.Application.Abstractions;
using ShortLink.Infrastructure.Persistence;
using Xunit;

namespace ShortLink.IntegrationTests;

/// <summary>
/// 在<b>真 MySQL</b> 上重放一遍冒烟路径：登录 → 建链 → 302 跳转 → 点击落库 → 统计可见。
/// <para>
/// 为什么单独要有这一套：其余集成测试都跑 SQLite，MySQL 方言（<c>ExecuteUpdateAsync</c>、
/// 索引、<c>utf8mb4</c>）与 MySQL 迁移程序集在 CI 里从来没被执行过。
/// 手工冒烟能发现这类问题，但它不可重复、也不会在回归时提醒任何人。
/// </para>
/// </summary>
[Collection(MySqlCollection.Name)]
public sealed class MySqlSmokeTests(MySqlHostFixture fixture)
{
    private const string TargetUrl = "https://example.com/it-mysql";

    [Fact]
    public async Task Smoke_Path_Should_Login_Create_Redirect_And_Count_On_MySql()
    {
        ContainerTestGate.SkipUnlessEnabled();

        using var client = SmokeApi.CreateClient(fixture.Factory!);
        await SmokeApi.AuthenticateAsync(client);

        var linkId = await SmokeApi.CreateLinkAsync(client, "itmysql", TargetUrl);

        var redirect = await client.GetAsync("/itmysql", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Contains(TargetUrl, redirect.Headers.Location!.ToString(), StringComparison.Ordinal);

        // 点击走消息总线批落库，用轮询等到条件成立，而不是猜一个 sleep 长度。
        await SmokeApi.WaitUntilAsync(
            async () => await SmokeApi.GetTotalClicksAsync(client, linkId) >= 1,
            "点击应在聚合窗口后落库到 MySQL，并能被统计接口读到");
    }

    [Fact]
    public async Task Health_Should_Be_Ready_And_Expose_Topology_On_MySql()
    {
        ContainerTestGate.SkipUnlessEnabled();

        using var client = SmokeApi.CreateClient(fixture.Factory!);

        var ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        // 全部依赖正常时探针只回 "ready"；任一依赖失败才会输出 "database=.../cache=..." 明细。
        // 所以要同时断言「是 ready」和「没有 fail 明细」，只匹配 ready 会漏掉「刚好没检查」。
        var body = await ready.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("ready", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fail", body, StringComparison.OrdinalIgnoreCase);

        // 没配 Redis 时背板必须是 in-process：这个字段是「多实例下缓存不失效」的唯一可读线索。
        using var info = JsonDocument.Parse(await client.GetStringAsync("/health/info", TestContext.Current.CancellationToken));
        var backplane = info.RootElement
            .GetProperty("data").GetProperty("cache").GetProperty("backplane").GetString();
        Assert.Equal("in-process", backplane);
    }

    [Fact]
    public void Host_Should_Really_Be_Backed_By_MySql()
    {
        ContainerTestGate.SkipUnlessEnabled();

        // 防止「以为在测 MySQL，其实悄悄跑回 SQLite」——那套用例就变成了自我安慰。
        // 从 DI 里拿真实的 DbContext 断言 provider，比断言配置字符串可靠。
        using var scope = fixture.Factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShortLinkDbContext>();

        Assert.True(db.Database.IsMySql(), "容器化冒烟必须真的跑在 MySQL 上，否则方言与迁移程序集等于没测");
    }
}

/// <summary>
/// 配了 Redis 的宿主：验证「Redis 连接串可用 + 背板生效 + 就绪探针通过」。
/// <para>
/// 这三件事一起构成上次那个坑的回归网：Aspire 的 Redis 容器默认 <c>requirepass</c>，
/// 连接串少 <c>password=</c> 时启动日志一切正常，只有 <c>/health/ready</c> 的 cache 一直 fail。
/// </para>
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class RedisHostSmokeTests(RedisHostFixture fixture)
{
    [Fact]
    public async Task Ready_Should_Pass_And_Report_Redis_Backplane()
    {
        ContainerTestGate.SkipUnlessEnabled();

        using var client = fixture.Factory!.CreateClient();

        var ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        // DB 与缓存任一失败都会在这里露出 fail 明细——上次连接串缺 password= 就是这个症状。
        var body = await ready.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("ready", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fail", body, StringComparison.OrdinalIgnoreCase);

        using var info = JsonDocument.Parse(await client.GetStringAsync("/health/info", TestContext.Current.CancellationToken));
        var backplane = info.RootElement
            .GetProperty("data").GetProperty("cache").GetProperty("backplane").GetString();
        Assert.Equal("redis", backplane);

        // 文案可以被配置"看起来对"而实际没连上；从 DI 取拓扑状态再确认一次。
        using var scope = fixture.Factory!.Services.CreateScope();
        var topology = scope.ServiceProvider.GetRequiredService<ICacheTopology>();
        Assert.True(topology.HasDistributedBackplane, "配了 Redis 就必须真的启用分布式背板");
    }
}
