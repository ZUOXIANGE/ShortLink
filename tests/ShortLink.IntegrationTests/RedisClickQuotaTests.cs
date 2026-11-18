using Microsoft.Extensions.Logging.Abstractions;
using ShortLink.Application.Abstractions;
using ShortLink.Infrastructure.Analytics;
using StackExchange.Redis;
using Xunit;

namespace ShortLink.IntegrationTests;

/// <summary>
/// 点击封顶配额在真实 Redis 上的键生命周期。
/// <para>
/// SQLite 宿主永远走进程内配额，这条路径只能对着真 Redis 验。
/// 计数键不带 TTL 时，删除或长期闲置的短链会在 Redis 留下永久键——
/// 这类泄漏不会报错、不会让探针变红，只能靠断言 TTL 发现。
/// </para>
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class RedisClickQuotaTests(RedisHostFixture fixture)
{
    [Fact]
    public async Task Counter_Key_Must_Carry_Ttl_Across_Reserve_Release_And_Synchronize()
    {
        ContainerTestGate.SkipUnlessEnabled();

        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var quota = new RedisClickQuota(redis, NullLogger<RedisClickQuota>.Instance);
        var linkId = Random.Shared.NextInt64(1, long.MaxValue);
        var link = new CachedLink { Id = linkId, ClickCount = 0, MaxClicks = 10 };

        var counterKey = $"shortlink:click-quota:{linkId}";
        string? reservationKey = null;

        try
        {
            var reservation = await quota.TryReserveAsync(link, TestContext.Current.CancellationToken);
            Assert.NotNull(reservation);
            reservationKey = $"shortlink:click-quota-reservation:{reservation!.ReservationId:N}";

            var database = redis.GetDatabase();

            var counterTtl = await database.KeyTimeToLiveAsync(counterKey);
            Assert.NotNull(counterTtl);
            Assert.InRange(counterTtl.Value, TimeSpan.FromDays(1), TimeSpan.FromDays(8));

            var reservationTtl = await database.KeyTimeToLiveAsync(reservationKey);
            Assert.NotNull(reservationTtl);
            Assert.InRange(reservationTtl.Value, TimeSpan.FromDays(1), TimeSpan.FromDays(8));

            // 补偿释放走 DECR：计数键不能被变回「永久键」。
            await quota.ReleaseAsync(reservation.ReservationId, link.Id, TestContext.Current.CancellationToken);
            counterTtl = await database.KeyTimeToLiveAsync(counterKey);
            Assert.NotNull(counterTtl);

            // 落库后的校准同样会重写计数键，也必须继续带 TTL。
            await quota.SynchronizeAsync(link.Id, 3, TestContext.Current.CancellationToken);
            counterTtl = await database.KeyTimeToLiveAsync(counterKey);
            Assert.NotNull(counterTtl);
            Assert.InRange(counterTtl.Value, TimeSpan.FromDays(1), TimeSpan.FromDays(8));
        }
        finally
        {
            var keys = reservationKey is null
                ? new RedisKey[] { counterKey }
                : new RedisKey[] { counterKey, reservationKey };
            await redis.GetDatabase().KeyDeleteAsync(keys);
        }
    }
}
