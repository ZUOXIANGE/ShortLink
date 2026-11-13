using Microsoft.Extensions.Logging.Abstractions;
using ShortLink.Infrastructure.RateLimiting;
using StackExchange.Redis;
using Xunit;

namespace ShortLink.IntegrationTests;

/// <summary>
/// <see cref="DistributedWindowRateLimiter"/> 的 Redis 行为。
/// <para>
/// 这层逻辑在 SQLite 宿主里永远跑不到（未配 Redis 时走本地窗口），
/// 而它恰恰是「多实例共用配额」与「依赖抖动不能变成放行」两条约束的落点，
/// 所以必须对着真 Redis 验，不能只靠读 Lua 脚本。
/// </para>
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class DistributedRateLimiterTests(RedisHostFixture fixture)
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Acquire_Should_Stop_At_PermitLimit_And_Report_RetryAfter()
    {
        ContainerTestGate.SkipUnlessEnabled();

        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        using var limiter = CreateLimiter(redis, UniqueKey(), permitLimit: 3);

        for (var index = 0; index < 3; index++)
        {
            using var lease = await limiter.AcquireAsync(1);
            Assert.True(lease.IsAcquired, $"第 {index + 1} 次应当放行（窗口额度 3）");
        }

        using var rejected = await limiter.AcquireAsync(1);
        Assert.False(rejected.IsAcquired, "超出窗口额度必须被拒绝");

        // RetryAfter 是客户端退避的唯一依据；缺了它调用方只能瞎猜。
        Assert.True(rejected.TryGetMetadata("RetryAfter", out var metadata));
        var retryAfter = Assert.IsType<TimeSpan>(metadata);
        Assert.InRange(retryAfter, TimeSpan.FromMilliseconds(1), Window);
    }

    [Fact]
    public async Task Counter_Key_Must_Carry_Ttl_To_Avoid_Permanent_Ban()
    {
        ContainerTestGate.SkipUnlessEnabled();

        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var key = UniqueKey();
        using var limiter = CreateLimiter(redis, key, permitLimit: 5);

        using (await limiter.AcquireAsync(1))
        {
            // 只为把计数键创建出来
        }

        var ttl = await redis.GetDatabase().KeyTimeToLiveAsync(key);

        // null 表示「没有过期时间」——那正是 INCR 与 EXPIRE 拆成两步时最容易留下的永久键，
        // 一旦发生，这个 IP 会被窗口额度永久封死，且没有任何告警。
        Assert.NotNull(ttl);
        Assert.InRange(ttl!.Value, TimeSpan.FromSeconds(1), Window);
    }

    [Fact]
    public async Task Concurrent_Acquires_Should_Not_Overshoot_The_Window()
    {
        ContainerTestGate.SkipUnlessEnabled();

        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        using var limiter = CreateLimiter(redis, UniqueKey(), permitLimit: 5);

        // 20 个并发抢 5 个额度。若 INCR 与判定不是原子的，就会出现放行数 > 5。
        var leases = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => limiter.AcquireAsync(1).AsTask()));

        try
        {
            var acquired = leases.Count(lease => lease.IsAcquired);
            Assert.Equal(5, acquired);
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    [Fact]
    public async Task Redis_Failure_Should_Fall_Back_To_Local_Window_Instead_Of_Allowing_Everything()
    {
        ContainerTestGate.SkipUnlessEnabled();

        // 指向一个没人监听的端口：连接不会抛（AbortOnConnectFail=false），
        // 但每次脚本执行都会失败——这正是「Redis 抖动」在调用方看来的形态。
        await using var deadRedis = await ConnectionMultiplexer.ConnectAsync(
            "127.0.0.1:6390,abortConnect=false,connectTimeout=200,syncTimeout=200");

        using var limiter = new DistributedWindowRateLimiter(
            deadRedis,
            NullLogger<DistributedWindowRateLimiter>.Instance,
            $"shortlink:ratelimit:fallback:{Guid.NewGuid():N}",
            permitLimit: 2,
            window: Window);

        // 回落语义：既不能放行（打挂 Redis 即绕过限流），也不能全拒（把正常用户挡在外面）。
        using var first = await limiter.AcquireAsync(1);
        using var second = await limiter.AcquireAsync(1);
        using var third = await limiter.AcquireAsync(1);

        Assert.True(first.IsAcquired);
        Assert.True(second.IsAcquired);
        Assert.False(third.IsAcquired, "回落本地窗口后仍要按额度拒绝");
    }

    private static DistributedWindowRateLimiter CreateLimiter(
        IConnectionMultiplexer redis,
        string key,
        int permitLimit)
        => new(redis, NullLogger<DistributedWindowRateLimiter>.Instance, key, permitLimit, Window);

    private static string UniqueKey() => $"shortlink:ratelimit:it:{Guid.NewGuid():N}";
}
