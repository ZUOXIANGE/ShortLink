using Microsoft.Extensions.DependencyInjection;
using ShortLink.Application.Abstractions;
using StackExchange.Redis;

namespace ShortLink.Infrastructure.Health;

/// <summary>
/// Redis 就绪探测。未配置 Redis（未注册 <see cref="IConnectionMultiplexer"/>）时直接返回健康，
/// 因为此时缓存退化为进程内，不构成外部依赖。
/// </summary>
public sealed class RedisCacheHealthProbe(IServiceProvider services) : ICacheHealthProbe
{
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(1);

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<IConnectionMultiplexer>() is not { } redis)
        {
            return true;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PingTimeout);

            // Ping 走的是 PING 命令，比 IsConnected 可靠：后者在网络闪断后可能短暂误报。
            var latency = await redis.GetDatabase().PingAsync().WaitAsync(timeout.Token).ConfigureAwait(false);
            return latency < PingTimeout;
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }
}
