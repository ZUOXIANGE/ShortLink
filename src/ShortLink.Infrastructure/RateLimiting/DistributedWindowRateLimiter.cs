using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using ShortLink.Infrastructure.Diagnostics;
using StackExchange.Redis;

namespace ShortLink.Infrastructure.RateLimiting;

/// <summary>
/// 跨实例固定窗口限流器。用 Redis 的 <c>INCRBY</c> + <c>PEXPIRE</c> 做原子计数，
/// 让 N 个实例共享同一份配额。
/// <para>
/// 为什么需要它：<see cref="FixedWindowRateLimiter"/> 的计数器在进程内，
/// 3 个实例部署时「登录 10 次/分钟」实际等于 30 次/分钟，阈值形同虚设。
/// </para>
/// <para>
/// Redis 不可用时<b>回落到本地窗口</b>，而不是直接放行：限流后端抖动不应该让登录接口变成无限制，
/// 也不应该把正常用户全部拒掉。回落次数计入 <c>shortlink.ratelimit.fallbacks</c>，
/// 该指标持续大于 0 说明 Redis 链路有问题，需要看告警。
/// </para>
/// </summary>
public sealed class DistributedWindowRateLimiter : RateLimiter
{
    /// <summary>
    /// KEYS[1] 计数键；ARGV[1] 本次消耗许可数，ARGV[2] 窗口内总许可数，ARGV[3] 窗口毫秒数。
    /// 返回 <c>{1, 0}</c> 放行；<c>{0, 剩余毫秒}</c> 拒绝。
    /// 计键与过期必须在同一个脚本里完成，否则并发下会出现「永久键」把该 IP 永久封死。
    /// </summary>
    private const string WindowScript = """
        local permits = tonumber(ARGV[1])
        local limit = tonumber(ARGV[2])
        local window = tonumber(ARGV[3])
        local current = redis.call('INCRBY', KEYS[1], permits)
        local ttl = redis.call('PTTL', KEYS[1])
        if ttl < 0 then
            redis.call('PEXPIRE', KEYS[1], window)
            ttl = window
        end
        if current > limit then
            return {0, ttl}
        end
        return {1, 0}
        """;

    private readonly FixedWindowRateLimiter _fallback;

    public DistributedWindowRateLimiter(
        IConnectionMultiplexer redis,
        ILogger<DistributedWindowRateLimiter> logger,
        string key,
        int permitLimit,
        TimeSpan window)
    {
        Redis = redis;
        Logger = logger;
        Key = key;
        PermitLimit = permitLimit;
        Window = window;
        _fallback = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window,
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }

    private IConnectionMultiplexer Redis { get; }

    private ILogger Logger { get; }

    /// <summary>已带全局前缀的 Redis 键。</summary>
    private string Key { get; }

    private int PermitLimit { get; }

    private TimeSpan Window { get; }

    /// <summary>
    /// 空闲两个窗口后允许分区缓存回收。返回 null 会让「按 IP 分区」的限流器把每个出现过的 IP
    /// 永久留在内存里，公网接口下就是一条缓慢的内存泄漏。
    /// </summary>
    public override TimeSpan? IdleDuration => Window * 2;

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        try
        {
            var result = Redis.GetDatabase().ScriptEvaluate(
                WindowScript,
                new RedisKey[] { Key },
                Arguments(permitCount));

            return ToLease(result, permitCount);
        }
        catch (Exception exception) when (IsRedisFailure(exception))
        {
            Logger.LogWarning(exception, "Redis 限流不可用，回落本地窗口（key={Key}）", Key);
            ShortLinkDiagnostics.RateLimitFallbacks.Add(1);
            return _fallback.AttemptAcquire(permitCount);
        }
    }

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await Redis.GetDatabase()
                .ScriptEvaluateAsync(WindowScript, new RedisKey[] { Key }, Arguments(permitCount))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            return ToLease(result, permitCount);
        }
        catch (Exception exception) when (IsRedisFailure(exception) && !cancellationToken.IsCancellationRequested)
        {
            Logger.LogWarning(exception, "Redis 限流不可用，回落本地窗口（key={Key}）", Key);
            ShortLinkDiagnostics.RateLimitFallbacks.Add(1);
            return await _fallback.AcquireAsync(permitCount, cancellationToken).ConfigureAwait(false);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fallback.Dispose();
        }

        base.Dispose(disposing);
    }

    private RedisValue[] Arguments(int permitCount) =>
        [permitCount, PermitLimit, (long)Window.TotalMilliseconds];

    private RateLimitLease ToLease(RedisResult result, int permitCount)
    {
        try
        {
            var parts = (RedisResult[]?)result;
            if (result.IsNull || parts is null || parts.Length < 2)
            {
                return FallBackForMalformedResult(permitCount);
            }

            var acquired = (long)parts[0] == 1;
            var retryAfter = TimeSpan.FromMilliseconds(Math.Max((long)parts[1], 0));
            return new WindowLease(acquired, acquired ? null : retryAfter);
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            Logger.LogWarning(exception, "Redis 限流返回无法解析的结果，回落本地窗口（key={Key}）", Key);
            ShortLinkDiagnostics.RateLimitFallbacks.Add(1);
            return _fallback.AttemptAcquire(permitCount);
        }
    }

    private RateLimitLease FallBackForMalformedResult(int permitCount)
    {
        Logger.LogWarning("Redis 限流返回空或不完整结果，回落本地窗口（key={Key}）", Key);
        ShortLinkDiagnostics.RateLimitFallbacks.Add(1);
        return _fallback.AttemptAcquire(permitCount);
    }

    private static bool IsRedisFailure(Exception exception)
        => exception is RedisException or TimeoutException or ObjectDisposedException;

    /// <summary>最简租约：只需要「是否放行」与可选的 <c>RetryAfter</c>。</summary>
    private sealed class WindowLease(bool acquired, TimeSpan? retryAfter) : RateLimitLease
    {
        private const string RetryAfterMetadata = "RetryAfter";

        public override bool IsAcquired => acquired;

        public override IEnumerable<string> MetadataNames =>
            retryAfter is null ? [] : [RetryAfterMetadata];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (retryAfter is { } value && string.Equals(metadataName, RetryAfterMetadata, StringComparison.Ordinal))
            {
                metadata = value;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}
