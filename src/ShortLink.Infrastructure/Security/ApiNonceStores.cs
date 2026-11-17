using System.Collections.Concurrent;
using ShortLink.Application.Abstractions;
using StackExchange.Redis;

namespace ShortLink.Infrastructure.Security;

/// <summary>
/// Redis nonce 去重。单条 <c>SET NX EX</c> 原子完成「判断 + 写入 + 过期」，
/// 不能拆成 EXISTS + SET：两步之间崩溃会漏掉过期时间，或把合法请求误判成重放。
/// </summary>
public sealed class RedisApiNonceStore(IConnectionMultiplexer redis) : IApiNonceStore
{
    public bool IsDistributed => true;

    public Task<bool> TryRegisterAsync(
        string clientKey,
        string nonce,
        TimeSpan timeToLive,
        CancellationToken cancellationToken)
        => redis.GetDatabase().StringSetAsync(
            $"shortlink:api:nonce:{clientKey}:{nonce}",
            "1",
            timeToLive,
            When.NotExists);
}

/// <summary>
/// 进程内 nonce 去重。单实例可用；多实例部署时各节点只认得自己见过的 nonce，
/// 重放保护会退化成「同一实例内有效」，启动日志会明确提示。
/// </summary>
public sealed class MemoryApiNonceStore(IClock clock) : IApiNonceStore
{
    private const int SweepIntervalSeconds = 60;

    private readonly ConcurrentDictionary<string, DateTime> _seen = new(StringComparer.Ordinal);
    private long _lastSweepTicks;

    public bool IsDistributed => false;

    public Task<bool> TryRegisterAsync(
        string clientKey,
        string nonce,
        TimeSpan timeToLive,
        CancellationToken cancellationToken)
    {
        SweepIfNeeded();

        var expiresAt = clock.UtcNow.Add(timeToLive);
        var added = _seen.TryAdd($"{clientKey}:{nonce}", expiresAt);
        return Task.FromResult(added);
    }

    private void SweepIfNeeded()
    {
        var now = clock.UtcNow;
        var lastTicks = Interlocked.Read(ref _lastSweepTicks);
        var last = lastTicks == 0 ? DateTime.MinValue : new DateTime(lastTicks, DateTimeKind.Utc);
        if ((now - last).TotalSeconds < SweepIntervalSeconds)
        {
            return;
        }

        Interlocked.Exchange(ref _lastSweepTicks, now.Ticks);

        foreach (var pair in _seen)
        {
            if (pair.Value <= now)
            {
                _seen.TryRemove(pair.Key, out _);
            }
        }
    }
}
